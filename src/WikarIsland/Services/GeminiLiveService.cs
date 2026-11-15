using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.Wave;

namespace WikarIsland.Services;

/// <summary>
/// Живой разговор через Gemini Live (модель gemini-3.8-live): звук с микрофона идёт в Gemini потоком,
/// ответ приходит сразу голосом и играет, пока ещё генерируется. Действия на компьютере — те же инструменты,
/// что у обычной Капсулки (запуск, окна, громкость…). Слово «Капсулка» по-прежнему ловит офлайн-Vosk,
/// поэтому в интернет звук уходит только во время разговора.
/// Протокол: WebSocket BidiGenerateContent — setup → setupComplete → realtimeInput.audio (PCM 16 кГц)
/// ← serverContent.modelTurn (PCM 24 кГц), toolCall → toolResponse.
/// </summary>
public sealed class GeminiLiveService : IDisposable
{
    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";

    public static readonly (string id, string name)[] Voices =
    {
        ("Charon", "Charon — низкий, спокойный (как Джарвис)"),
        ("Orus", "Orus — твёрдый мужской"),
        ("Iapetus", "Iapetus — ясный мужской"),
        ("Fenrir", "Fenrir — бодрый мужской"),
        ("Kore", "Kore — женский"),
        ("Aoede", "Aoede — лёгкий женский"),
    };

    /// <summary>Ключ Gemini: свой (вкладка Live) или ключ ИИ, если мозгом выбран Gemini.</summary>
    public static string Key =>
        App.Settings.GeminiKey.Length > 0 ? App.Settings.GeminiKey
        : App.Settings.AiPreset == "gemini" ? App.Settings.AiKey : "";

    public static bool Available => App.Settings.LiveEnabled && Key.Length > 0;

    private readonly Dispatcher _ui = System.Windows.Application.Current.Dispatcher;
    private readonly Channel<byte[]> _audioIn = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _ws;
    private WaveOutEvent? _out;
    private BufferedWaveProvider? _play;
    private readonly StringBuilder _userText = new();
    private readonly StringBuilder _modelText = new();
    private bool _closed;

    /// <summary>Gemini что-то делает (говорит, ищет, ждёт инструмент) — обновляется при каждом сообщении.</summary>
    public DateTime LastActivity { get; private set; } = DateTime.Now;

    public bool Ready { get; private set; }

    /// <summary>Ответ ещё звучит (в буфере воспроизведения есть звук).</summary>
    public bool IsSpeaking => _play is { BufferedBytes: > 0 };

    public event Action<string>? UserSaid;    // распознанная речь пользователя (накопительно за ход)
    public event Action<string>? ModelSaid;   // текст ответа (накопительно за ход)
    public event Action? TurnDone;
    public event Action<string>? Failed;      // соединение не открылось или оборвалось
    public event Action? EndRequested;        // модель решила, что разговор окончен

    public void Open()
    {
        _out = new WaveOutEvent { DesiredLatency = 120 };
        _play = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromMinutes(2),
            DiscardOnBufferOverflow = true,
        };
        _out.Init(_play);
        _out.Volume = (float)Math.Clamp(App.Settings.VoiceVolume, 0.1, 1.0);
        _out.Play();
        _ = Task.Run(RunAsync);
    }

    /// <summary>Кусок PCM 16 кГц моно 16 бит с микрофона. До готовности сессии копится в очереди.</summary>
    public void SendAudio(byte[] pcm, int count)
    {
        if (_closed || count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(pcm, 0, copy, 0, count);
        _audioIn.Writer.TryWrite(copy);
    }

    /// <summary>Замолчать (клик по капсуле): обрываем воспроизведение.</summary>
    public void StopSpeaking() => _play?.ClearBuffer();

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _audioIn.Writer.TryComplete();
        _cts.Cancel();
        try
        {
            _out?.Stop();
        }
        catch
        {
            // уже остановлен
        }
        _out?.Dispose();
        _out = null;
        ClientWebSocket? ws = _ws;
        _ws = null;
        if (ws != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using var t = new CancellationTokenSource(1000);
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", t.Token);
                }
                catch
                {
                    // сервер уже закрыл
                }
                ws.Dispose();
            });
        }
    }

    public void Dispose() => Close();

    // ---------- соединение ----------

    private async Task RunAsync()
    {
        CancellationToken token = _cts.Token;
        try
        {
            var ws = new ClientWebSocket();
            _ws = ws;
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(8));
                await ws.ConnectAsync(new Uri($"{Endpoint}?key={Uri.EscapeDataString(Key)}"), connect.Token);
            }
            await SendJsonAsync(Setup(), token);
            Task sender = Task.Run(() => SendLoopAsync(token), token);
            await ReceiveLoopAsync(ws, token);
            await sender.ContinueWith(_ => { }, TaskScheduler.Default);
        }
        catch (Exception ex) when (token.IsCancellationRequested && ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // закрыли сами
        }
        catch (Exception ex)
        {
            App.LogError(new InvalidOperationException("Gemini Live: " + Describe(ex), ex));
            Fail(Describe(ex));
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "сервер не ответил вовремя",
        WebSocketException w when w.InnerException?.Message is { Length: > 0 } inner => inner,
        _ => ex.Message,
    };

    private void Fail(string message)
    {
        if (_closed) return;
        Failed?.Invoke(message);
    }

    private JsonObject Setup()
    {
        var functions = new JsonArray();
        foreach (JsonNode? tool in AssistantService.ToolDeclarations())
        {
            JsonNode? fn = tool?["function"];
            if (fn == null) continue;
            var decl = new JsonObject
            {
                ["name"] = fn["name"]!.ToString(),
                ["description"] = fn["description"]?.ToString() ?? "",
                ["behavior"] = "BLOCKING", // наши инструменты отрабатывают мгновенно — пусть модель дождётся результата
            };
            if (fn["parameters"] is JsonObject p && p["properties"] is JsonObject props && props.Count > 0)
                decl["parameters"] = GeminiSchema(p);
            functions.Add(decl);
        }
        functions.Add(new JsonObject
        {
            ["name"] = "end_conversation",
            ["description"] = "Закончить разговор: пользователь поблагодарил, попрощался или сказал «хватит», «отбой». Сначала коротко попрощайся.",
            ["behavior"] = "BLOCKING",
        });
        functions.Add(new JsonObject
        {
            ["name"] = "confirm_pending",
            ["description"] = "Выполнить действие, которое ждало подтверждения (выключение или перезагрузка компьютера), — только после явного «да» пользователя.",
            ["behavior"] = "BLOCKING",
        });

        return new JsonObject
        {
            ["setup"] = new JsonObject
            {
                ["model"] = "models/" + App.Settings.LiveModel,
                ["generationConfig"] = new JsonObject
                {
                    ["responseModalities"] = new JsonArray { JsonValue.Create("AUDIO") },
                    ["speechConfig"] = new JsonObject
                    {
                        ["voiceConfig"] = new JsonObject
                        {
                            ["prebuiltVoiceConfig"] = new JsonObject { ["voiceName"] = App.Settings.LiveVoice },
                        },
                    },
                },
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray(new JsonObject { ["text"] = AssistantService.LiveSystemPrompt() }),
                },
                ["tools"] = new JsonArray(
                    new JsonObject { ["functionDeclarations"] = functions },
                    new JsonObject { ["googleSearch"] = new JsonObject() }),
                ["inputAudioTranscription"] = new JsonObject(),
                ["outputAudioTranscription"] = new JsonObject(),
                ["contextWindowCompression"] = new JsonObject { ["slidingWindow"] = new JsonObject() },
            },
        };
    }

    /// <summary>JSON Schema из инструментов ИИ → схема Gemini: типы заглавными (OBJECT, STRING…), без пустых списков.</summary>
    private static JsonNode GeminiSchema(JsonNode node)
    {
        if (node is JsonObject o)
        {
            var r = new JsonObject();
            foreach ((string key, JsonNode? value) in o)
            {
                if (value == null) continue;
                if (key == "type") r[key] = value.ToString().ToUpperInvariant();
                else if (key == "required" && value is JsonArray { Count: 0 }) continue;
                else r[key] = GeminiSchema(value);
            }
            return r;
        }
        if (node is JsonArray a) return new JsonArray(a.Select(x => x == null ? null : GeminiSchema(x)).ToArray());
        return node.DeepClone();
    }

    private async Task SendLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (byte[] chunk in _audioIn.Reader.ReadAllAsync(token))
            {
                while (!Ready && !token.IsCancellationRequested) await Task.Delay(20, token);
                await SendJsonAsync(new JsonObject
                {
                    ["realtimeInput"] = new JsonObject
                    {
                        ["audio"] = new JsonObject
                        {
                            ["data"] = Convert.ToBase64String(chunk),
                            ["mimeType"] = "audio/pcm;rate=16000",
                        },
                    },
                }, token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // закрыли
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private async Task SendJsonAsync(JsonNode message, CancellationToken token)
    {
        ClientWebSocket? ws = _ws;
        if (ws == null || ws.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await _sendLock.WaitAsync(token);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[256 * 1024];
        using var message = new MemoryStream();
        while (!token.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, token);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    string why = ws.CloseStatusDescription ?? "";
                    if (!Ready || ws.CloseStatus != WebSocketCloseStatus.NormalClosure)
                    {
                        App.LogError(new InvalidOperationException($"Gemini Live закрыл соединение: {ws.CloseStatus} {why}"));
                        Fail(why.Length > 0 ? why : "сервер закрыл соединение");
                    }
                    return;
                }
                message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(message.ToArray()); // сервер шлёт JSON и текстом, и бинарными кадрами
            }
            catch
            {
                continue;
            }
            if (root is JsonObject obj) await HandleAsync(obj, token);
        }
    }

    private async Task HandleAsync(JsonObject msg, CancellationToken token)
    {
        LastActivity = DateTime.Now;

        if (msg["setupComplete"] != null)
        {
            Ready = true;
            return;
        }

        if (msg["serverContent"] is JsonObject content)
        {
            if (content["interrupted"]?.GetValue<bool>() == true) _play?.ClearBuffer();

            if (content["inputTranscription"]?["text"]?.ToString() is { Length: > 0 } heard)
            {
                if (_modelText.Length > 0) // новый ход пользователя
                {
                    _modelText.Clear();
                    _userText.Clear();
                }
                _userText.Append(heard);
                UserSaid?.Invoke(_userText.ToString().Trim());
            }

            if (content["modelTurn"]?["parts"] is JsonArray parts)
            {
                foreach (JsonNode? part in parts)
                {
                    if (part?["inlineData"]?["data"]?.ToString() is not { Length: > 0 } b64) continue;
                    byte[] pcm = Convert.FromBase64String(b64);
                    _play?.AddSamples(pcm, 0, pcm.Length);
                }
            }

            if (content["outputTranscription"]?["text"]?.ToString() is { Length: > 0 } said)
            {
                _modelText.Append(said);
                ModelSaid?.Invoke(_modelText.ToString().Trim());
            }

            if (content["turnComplete"]?.GetValue<bool>() == true)
            {
                _userText.Clear();
                TurnDone?.Invoke();
            }
        }

        if (msg["toolCall"]?["functionCalls"] is JsonArray calls)
        {
            var responses = new JsonArray();
            bool end = false;
            foreach (JsonNode? call in calls)
            {
                string id = call?["id"]?.ToString() ?? "";
                string name = call?["name"]?.ToString() ?? "";
                JsonObject args = call?["args"] as JsonObject ?? new JsonObject();
                string result;
                if (name == "end_conversation")
                {
                    end = true;
                    result = "Разговор окончен";
                }
                else
                {
                    try
                    {
                        // инструменты трогают окна WPF — выполняем в потоке интерфейса
                        result = await _ui.InvokeAsync(() => App.Assistant.RunLiveTool(name, (JsonObject)args.DeepClone()));
                    }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                        result = "Ошибка: " + ex.Message;
                    }
                }
                responses.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["response"] = new JsonObject { ["result"] = result },
                });
            }
            await SendJsonAsync(new JsonObject { ["toolResponse"] = new JsonObject { ["functionResponses"] = responses } }, token);
            if (end) EndRequested?.Invoke();
        }

        if (msg["goAway"] != null)
        {
            // сервер скоро закроет соединение (лимит ~10 минут) — следующий разговор откроет новое
            EndRequested?.Invoke();
        }
    }
}

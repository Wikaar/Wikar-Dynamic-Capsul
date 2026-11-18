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
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
    private WasapiOut? _out;
    private BufferedWaveProvider? _play;
    private readonly StringBuilder _userText = new();
    private readonly StringBuilder _modelText = new();
    private bool _closed;

    /// <summary>Gemini что-то делает (говорит, ищет, ждёт инструмент) — обновляется при каждом сообщении.</summary>
    public DateTime LastActivity { get; private set; } = DateTime.Now;

    public bool Ready { get; private set; }

    private volatile bool _turnActive; // модель отвечает: от первого звука ответа до turnComplete

    // «голос Капсулки вместо голоса Gemini»: звук модели не играем, а её текст озвучиваем своим голосом (Edge или Fish Audio)
    private readonly bool _ownVoice = App.Settings.LiveOwnVoice;
    private Channel<string>? _sentences;
    private readonly StringBuilder _pendingSpeech = new();
    private volatile Task? _speechTask;

    /// <summary>
    /// Ответ ещё идёт: модель продолжает присылать звук или он ещё не доигран. Раньше смотрели только на буфер,
    /// а он на миг пустеет между пакетами из сети — микрофон включался посреди ответа, шум и эхо колонок
    /// Gemini принимал за речь и обрывал сам себя.
    /// </summary>
    public bool IsSpeaking => _turnActive || _play is { BufferedBytes: > 0 } || _speechTask is { IsCompleted: false };

    public event Action<string>? UserSaid;    // распознанная речь пользователя (накопительно за ход)
    public event Action<string>? ModelSaid;   // текст ответа (накопительно за ход)
    public event Action? TurnDone;
    public event Action<string>? Failed;      // соединение не открылось или оборвалось
    public event Action? EndRequested;        // модель решила, что разговор окончен
    public event Action<string>? ToolCalled;  // модель вызвала инструмент — значит, услышала и действует

    public void Open()
    {
        _play = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
        {
            BufferDuration = TimeSpan.FromMinutes(2),
            DiscardOnBufferOverflow = true,
        };
        // 24 кГц моно → формат устройства вывода (обычно 48 кГц стерео float)
        int rate = 48000, channels = 2;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            WaveFormat mix = device.AudioClient.MixFormat;
            rate = mix.SampleRate;
            channels = mix.Channels;
        }
        catch
        {
            // по умолчанию 48 кГц стерео
        }
        ISampleProvider sp = new VolumeSampleProvider(_play.ToSampleProvider())
        {
            Volume = (float)Math.Clamp(App.Settings.VoiceVolume, 0.1, 1.0),
        };
        if (rate != 24000) sp = new WdlResamplingSampleProvider(sp, rate);
        if (channels == 2) sp = new MonoToStereoSampleProvider(sp);
        else if (channels > 2)
        {
            var multi = new MultiplexingSampleProvider(new[] { sp }, channels);
            multi.ConnectInputToOutput(0, 0);
            multi.ConnectInputToOutput(0, 1);
            sp = multi;
        }
        _out = new WasapiOut(AudioClientShareMode.Shared, 100);
        _out.Init(sp);
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
    public void StopSpeaking()
    {
        _play?.ClearBuffer();
        StopOwnVoice();
    }

    // ---------- свой голос ----------

    /// <summary>Кусок текста ответа: целые предложения сразу уходят в озвучку, хвост ждёт продолжения.</summary>
    private void QueueSpeech(string chunk)
    {
        if (_sentences == null)
        {
            Channel<string> channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
            _sentences = channel;
            _speechTask = _ui.InvokeAsync(() => App.Speech.SpeakStreamAsync(channel.Reader)).Task.Unwrap();
        }
        _pendingSpeech.Append(chunk);
        string text = _pendingSpeech.ToString();
        int cut = -1;
        for (int i = 0; i < text.Length - 1; i++)
            if (".!?…".IndexOf(text[i]) >= 0 && char.IsWhiteSpace(text[i + 1])) cut = i + 1;
        // расшифровка бывает без точек — длинный кусок режем по пробелу
        if (cut < 0 && text.Length > 160) cut = text.LastIndexOf(' ') > 40 ? text.LastIndexOf(' ') : -1;
        if (cut <= 0) return;
        _sentences.Writer.TryWrite(text[..cut].Trim());
        _pendingSpeech.Remove(0, cut);
    }

    /// <summary>Ход модели закончился: досказываем хвост и закрываем очередь.</summary>
    private void FlushSpeech()
    {
        if (_sentences == null) return;
        string rest = _pendingSpeech.ToString().Trim();
        if (rest.Length > 0) _sentences.Writer.TryWrite(rest);
        _pendingSpeech.Clear();
        _sentences.Writer.TryComplete();
        _sentences = null;
    }

    private void StopOwnVoice()
    {
        if (!_ownVoice) return;
        _sentences?.Writer.TryComplete();
        _sentences = null;
        _pendingSpeech.Clear();
        _ui.InvokeAsync(() => App.Speech.Stop());
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _audioIn.Writer.TryComplete();
        _sentences?.Writer.TryComplete();
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
                // без googleSearch: на бесплатном тарифе у Gemini 3 нет квоты на поиск, и вся сессия падает с «exceeded your quota»
                ["tools"] = App.Settings.LiveGoogleSearch
                    ? new JsonArray(new JsonObject { ["functionDeclarations"] = functions }, new JsonObject { ["googleSearch"] = new JsonObject() })
                    : new JsonArray(new JsonObject { ["functionDeclarations"] = functions }),
                ["realtimeInputConfig"] = new JsonObject
                {
                    // без «перебивания голосом» ответ договаривается до конца, что бы ни услышал микрофон
                    ["activityHandling"] = App.Settings.LiveBargeIn ? "START_OF_ACTIVITY_INTERRUPTS" : "NO_INTERRUPTION",
                    ["automaticActivityDetection"] = new JsonObject
                    {
                        ["startOfSpeechSensitivity"] = "START_SENSITIVITY_LOW", // кашель, клавиатура и фон — не речь
                        ["endOfSpeechSensitivity"] = "END_SENSITIVITY_LOW",     // не обрывать фразу на паузе
                        ["prefixPaddingMs"] = 300,
                        ["silenceDurationMs"] = 800,
                    },
                },
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
            App.LogVoice($"Live: подключён ({App.Settings.LiveModel})");
            return;
        }

        if (msg["serverContent"] is JsonObject content)
        {
            if (content["interrupted"]?.GetValue<bool>() == true)
            {
                _play?.ClearBuffer();
                StopOwnVoice();
                _turnActive = false;
            }

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
                    if (_ownVoice)
                    {
                        _turnActive = true; // звук Gemini не играем — говорит голос Капсулки по тексту ответа
                        continue;
                    }
                    byte[] pcm = Convert.FromBase64String(b64);
                    if (_play == null) continue;
                    // начало ответа: 200 мс тишины в запас, чтобы неровная доставка пакетов не давала «заиканий»
                    if (!_turnActive && _play.BufferedBytes == 0) _play.AddSamples(new byte[24000 * 2 / 5], 0, 24000 * 2 / 5);
                    _turnActive = true;
                    _play.AddSamples(pcm, 0, pcm.Length);
                }
            }

            if (content["outputTranscription"]?["text"]?.ToString() is { Length: > 0 } said)
            {
                _modelText.Append(said);
                ModelSaid?.Invoke(_modelText.ToString().Trim());
                if (_ownVoice) QueueSpeech(said);
            }

            if (content["turnComplete"]?.GetValue<bool>() == true)
            {
                if (_ownVoice) FlushSpeech();
                _turnActive = false;
                _userText.Clear();
                TurnDone?.Invoke();
            }
        }

        if (msg["toolCall"]?["functionCalls"] is JsonArray calls)
        {
            foreach (JsonNode? call in calls) ToolCalled?.Invoke(call?["name"]?.ToString() ?? "");
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
                        result = await await _ui.InvokeAsync(() => App.Assistant.RunLiveToolAsync(name, (JsonObject)args.DeepClone()));
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

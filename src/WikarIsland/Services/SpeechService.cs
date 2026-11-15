using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace WikarIsland.Services;

/// <summary>
/// Голос Капсулки: нейронные голоса Microsoft (как «Читать вслух» в Edge) через WebSocket,
/// при сбое — встроенный синтезатор Windows. Звук играет WPF MediaPlayer из временного mp3.
/// </summary>
public sealed class SpeechService
{
    public static readonly (string id, string name)[] Voices =
    {
        ("ru-RU-DmitryNeural", "Дмитрий (мужской)"),
        ("ru-RU-SvetlanaNeural", "Светлана (женский)"),
        ("ru-RU-DariyaNeural", "Дария (женский)"),
    };

    private const string WssUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";

    private readonly MediaPlayer _player = new();
    private TaskCompletionSource<bool>? _playing;
    private CancellationTokenSource? _cts;
    private int _fileIndex;

    public bool IsSpeaking { get; private set; }
    public string? LastError { get; private set; }

    public event Action? SpeakingChanged;

    public SpeechService()
    {
        _player.MediaEnded += (_, _) => _playing?.TrySetResult(true);
        _player.MediaFailed += (_, e) =>
        {
            LastError = e.ErrorException?.Message;
            _playing?.TrySetResult(false);
        };
    }

    /// <summary>Сказать фразу. Предыдущая обрывается. Возвращается, когда договорили.</summary>
    public async Task SpeakAsync(string text)
    {
        text = Clean(text);
        if (text.Length == 0) return;
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;

        SetSpeaking(true);
        try
        {
            byte[]? mp3 = null;
            try
            {
                mp3 = await SynthesizeAsync(text, App.Settings.VoiceName, cts.Token);
                LastError = null;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                App.LogError(new InvalidOperationException("Голос Edge не ответил, говорю голосом Windows", ex));
            }

            if (cts.IsCancellationRequested) return;
            if (mp3 is { Length: > 0 }) await PlayAsync(mp3, cts.Token);
            else await SpeakWindowsAsync(text, cts.Token);
        }
        finally
        {
            if (_cts == cts) SetSpeaking(false);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        try
        {
            _player.Stop();
            _player.Close();
        }
        catch
        {
            // плеер уже закрыт
        }
        _playing?.TrySetResult(false);
        if (IsSpeaking) SetSpeaking(false);
    }

    private void SetSpeaking(bool on)
    {
        IsSpeaking = on;
        SpeakingChanged?.Invoke();
    }

    /// <summary>Без markdown, ссылок и эмодзи — их не произнести.</summary>
    private static string Clean(string text)
    {
        string s = System.Text.RegularExpressions.Regex.Replace(text, @"https?://\S+", "ссылка");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[*_`#>|~]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[\p{So}\p{Cs}]", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        foreach (char c in s.ToCharArray())
            if (c < 32) s = s.Replace(c, ' ');
        return s.Length > 1500 ? s[..1500] : s;
    }

    // ---------- Edge ----------

    public static async Task<byte[]> SynthesizeAsync(string text, string voice, CancellationToken token)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await SynthesizeOnceAsync(text, voice, token);
            }
            catch (WebSocketException) when (attempt == 0)
            {
                // часто это 403 из-за расхождения часов — берём время у сервера и пробуем ещё раз
                await AdjustClockAsync(token);
            }
        }
        throw new InvalidOperationException("Сервис голоса не ответил");
    }

    private static async Task AdjustClockAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage r = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, "https://speech.platform.bing.com/"), token);
            if (r.Headers.Date is DateTimeOffset server)
                EdgeTtsDrm.ClockSkewSeconds += (server - DateTimeOffset.UtcNow).TotalSeconds;
        }
        catch
        {
            // без поправки
        }
    }

    private static async Task<byte[]> SynthesizeOnceAsync(string text, string voice, CancellationToken token)
    {
        string connectionId = Guid.NewGuid().ToString("N");
        string url = $"{WssUrl}?TrustedClientToken={EdgeTtsDrm.TrustedClientToken}&ConnectionId={connectionId}" +
                     $"&Sec-MS-GEC={EdgeTtsDrm.Generate()}&Sec-MS-GEC-Version={EdgeTtsDrm.SecMsGecVersion}";
        string major = EdgeTtsDrm.ChromiumVersion.Split('.')[0];

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", $"muid={Guid.NewGuid().ToString("N").ToUpperInvariant()};");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        CancellationToken ct = timeout.Token;

        await ws.ConnectAsync(new Uri(url), ct);

        string date = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                      + " GMT+0000 (Coordinated Universal Time)";
        string config =
            $"X-Timestamp:{date}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"}," +
            "\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
        await SendText(ws, config, ct);

        string ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ru-RU'>" +
            $"<voice name='{voice}'><prosody pitch='+0Hz' rate='{App.Settings.VoiceRate:+0;-0}%' volume='+0%'>" +
            SecurityElement.Escape(text) +
            "</prosody></voice></speak>";
        string request =
            $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{date}Z\r\nPath:ssml\r\n\r\n{ssml}";
        await SendText(ws, request, ct);

        using var audio = new MemoryStream();
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return audio.ToArray();
                message.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);

            byte[] data = message.ToArray();
            if (r.MessageType == WebSocketMessageType.Text)
            {
                string s = Encoding.UTF8.GetString(data);
                if (s.Contains("Path:turn.end", StringComparison.Ordinal)) break;
            }
            else if (data.Length >= 2)
            {
                // первые два байта — длина заголовка (big-endian), затем сам заголовок, затем mp3
                int headerLength = (data[0] << 8) | data[1];
                if (headerLength + 2 > data.Length) continue;
                string header = Encoding.ASCII.GetString(data, 2, headerLength);
                if (!header.Contains("Path:audio", StringComparison.Ordinal)) continue;
                int start = headerLength + 2;
                if (start < data.Length) audio.Write(data, start, data.Length - start);
            }
        }

        try
        {
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch
        {
            // сервер закрыл сам
        }
        return audio.ToArray();
    }

    private static Task SendText(ClientWebSocket ws, string text, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct);

    private async Task PlayAsync(byte[] mp3, CancellationToken token)
    {
        string dir = Path.Combine(Path.GetTempPath(), "WikarIsland");
        Directory.CreateDirectory(dir);
        // чередуем два файла: MediaPlayer может ещё держать предыдущий
        string file = Path.Combine(dir, $"voice{_fileIndex++ % 2}.mp3");
        await File.WriteAllBytesAsync(file, mp3, token);

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _playing = done;
        _player.Open(new Uri(file));
        _player.Volume = Math.Clamp(App.Settings.VoiceVolume, 0.1, 1.0);
        _player.Play();
        using (token.Register(() => done.TrySetResult(false)))
        {
            // страховка: если MediaEnded не пришёл, отпускаем через длину фразы с запасом
            Task safety = Task.Delay(TimeSpan.FromSeconds(5 + mp3.Length / 6000.0), CancellationToken.None);
            await Task.WhenAny(done.Task, safety);
        }
        _player.Close();
    }

    // ---------- запасной голос Windows ----------

    private static async Task SpeakWindowsAsync(string text, CancellationToken token)
    {
        try
        {
            using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            var voice = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices
                .FirstOrDefault(v => v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase));
            if (voice == null) return; // русского голоса в Windows нет — ответ всё равно виден в капсуле
            synth.Voice = voice;
            using var stream = await synth.SynthesizeTextToStreamAsync(text);
            using var player = new Windows.Media.Playback.MediaPlayer();
            var done = new TaskCompletionSource<bool>();
            player.MediaEnded += (_, _) => done.TrySetResult(true);
            player.MediaFailed += (_, _) => done.TrySetResult(false);
            player.Source = Windows.Media.Core.MediaSource.CreateFromStream(stream, stream.ContentType);
            player.Play();
            using (token.Register(() => done.TrySetResult(false)))
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(3 + text.Length / 8.0)));
            player.Pause();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WikarIsland.Services;

/// <summary>
/// «В Discord»: музыка встроенного плеера играет в голосовом канале через своего бота Capsul Radio (на NAS).
/// Плеер Капсулы остаётся главным и играет без звука, а бот повторяет за ним: трек, паузу, перемотку.
/// Поэтому очередь, «Моя волна», кнопки, медиаклавиши и голос работают как обычно.
/// Яндекс Музыка и радио — бот получает прямую ссылку на поток (у радио с пометкой live: эфир не перематывается);
/// YouTube Music — ID видео (звук бот берёт сам через yt-dlp).
/// </summary>
public sealed class DiscordBridge : IDisposable
{
    // /play для YouTube ждёт yt-dlp — даём время
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly HttpClient EventsHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly MusicHub _hub;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _eventsCts;
    private IMusicProvider? _muted;
    private string? _sentKey;
    private string? _rejectedKey; // трек, который бот не смог сыграть, — не повторяем его каждую секунду
    private bool _sentPlaying;
    private TimeSpan _lastPos;
    private DateTimeOffset _lastAt;
    private bool _sending;
    private bool _unsupportedShown;

    public DiscordBridge(MusicHub hub)
    {
        _hub = hub;
        _timer.Tick += async (_, _) => await SyncAsync();
    }

    /// <summary>Музыка сейчас идёт в Discord.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Включаем (проверяем бота) — кнопку на это время блокируем.</summary>
    public bool Busy { get; private set; }

    public static bool Configured => BaseUrl.Length > 0 && App.Settings.DiscordBotKey.Length > 0;

    private static string BaseUrl
    {
        get
        {
            string url = App.Settings.DiscordBotUrl.Trim().TrimEnd('/');
            if (url.Length > 0 && !url.Contains("://")) url = "http://" + url;
            return url;
        }
    }

    /// <summary>Адрес из настроек в нормальном виде: «192.168.1.10» → «http://192.168.1.10:8770».</summary>
    public static string NormalizeUrl(string raw)
    {
        string url = raw.Trim().TrimEnd('/');
        if (url.Length == 0) return "";
        if (!url.Contains("://")) url = "http://" + url;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && u.IsDefaultPort && !raw.Contains(":80") && u.Scheme == "http")
            url = $"{u.Scheme}://{u.Host}:8770";
        return url;
    }

    // ---------- включение и выключение ----------

    public async Task ToggleAsync()
    {
        if (Enabled) Disable("Музыка снова играет на компьютере.", pauseLocal: false, tellBot: true);
        else await EnableAsync();
    }

    public async Task EnableAsync()
    {
        if (Enabled || Busy) return;
        if (!Configured)
        {
            _hub.SetStatus("Сначала укажи адрес и ключ Discord-бота (правый клик по кнопке «В Discord»).");
            return;
        }
        Busy = true;
        _hub.SetStatus("Подключаюсь к Discord-боту…");
        try
        {
            string? error = await CheckAsync();
            if (error != null)
            {
                _hub.SetStatus("Discord-бот: " + error);
                return;
            }
            Enabled = true;
            _sentKey = null;
            _rejectedKey = null;
            _sentPlaying = false;
            _unsupportedShown = false;
            StartEvents();
            _timer.Start();
            _hub.SetStatus("Музыка играет в Discord, на компьютере — без звука.");
            await SyncAsync();
        }
        finally
        {
            Busy = false;
            _hub.RaiseChanged();
        }
    }

    /// <summary>Выключить «В Discord». pauseLocal — поставить плеер на паузу (иначе музыка внезапно заиграет на ПК).</summary>
    public void Disable(string? message, bool pauseLocal, bool tellBot)
    {
        if (!Enabled) return;
        Enabled = false;
        _timer.Stop();
        _eventsCts?.Cancel();
        _eventsCts = null;
        if (pauseLocal && _hub.Active?.State?.IsPlaying == true) _hub.Active.PlayPause();
        _muted?.SetMuted(false);
        _muted = null;
        if (tellBot && _sentKey != null) _ = PostAsync("/stop");
        _sentKey = null;
        _sentPlaying = false;
        if (message != null) _hub.SetStatus(message);
        else _hub.RaiseChanged();
    }

    /// <summary>Проверка связи: null — бот на месте, иначе понятная причина.</summary>
    public static async Task<string?> CheckAsync()
    {
        try
        {
            using HttpRequestMessage req = Request(HttpMethod.Get, "/status");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using HttpResponseMessage res = await Http.SendAsync(req, cts.Token);
            if (res.StatusCode == HttpStatusCode.Unauthorized) return "неверный ключ.";
            if (!res.IsSuccessStatusCode) return $"ответил ошибкой {(int)res.StatusCode}.";
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return "не отвечает. Проверь адрес (например, http://192.168.1.10:8770), что контейнер запущен и в bot.env задан BRIDGE_KEY.";
        }
    }

    // ---------- синхронизация с плеером ----------

    /// <summary>Сменился трек или пауза — синхронизировать сразу, не дожидаясь таймера.</summary>
    public void SyncSoon()
    {
        if (Enabled) _ = SyncAsync();
    }

    private async Task SyncAsync()
    {
        if (!Enabled || _sending) return;
        IMusicProvider? p = _hub.Active;

        // звук на ПК глушим у того сервиса, который сейчас играет
        if (p != _muted)
        {
            _muted?.SetMuted(false);
            p?.SetMuted(true);
            _muted = p;
        }

        MusicState? s = p?.State;
        string? key = s?.VideoId ?? s?.StreamUrl?.ToString();
        if (key != null && key == _rejectedKey) return;
        if (s == null || key == null)
        {
            if (s != null && p != null && p.Key is "spotify" or "ytvideo" && !_unsupportedShown)
            {
                _unsupportedShown = true;
                _hub.SetStatus("В Discord можно включить Яндекс Музыку, YouTube Music и радио.");
            }
            if (_sentKey != null && _sentPlaying) await SendAsync("/pause", null, () => _sentPlaying = false);
            return;
        }

        TimeSpan pos = PosOf(s);
        bool live = RadioProvider.IsLive(p); // эфир радио: без перемотки, бот всегда берёт поток «с сейчас»

        if (key != _sentKey)
        {
            if (!s.IsPlaying) return; // трек выбран, но ещё не заиграл — подождём
            var body = new JsonObject
            {
                ["kind"] = s.VideoId != null ? "youtube" : "url",
                ["title"] = s.Title,
                ["artist"] = s.Artist,
                ["cover"] = s.CoverUrl,
                ["durationMs"] = (long)s.Duration.TotalMilliseconds,
                // в самом начале трека шлём 0 — бот всё равно стартует с задержкой
                ["positionMs"] = live || pos < TimeSpan.FromSeconds(3) ? 0 : (long)pos.TotalMilliseconds,
                ["live"] = live,
                ["trackId"] = key.Length > 200 ? key[..200] : key,
            };
            if (s.VideoId != null) body["videoId"] = s.VideoId;
            else body["url"] = s.StreamUrl!.ToString();

            string sentKey = key;
            await SendAsync("/play", body, () =>
            {
                _sentKey = sentKey;
                _sentPlaying = true;
            }, onRejected: () => _rejectedKey = sentKey);
            Remember();
            return;
        }

        if (s.IsPlaying != _sentPlaying)
        {
            if (s.IsPlaying)
            {
                if (!live) await SendAsync("/seek", new JsonObject { ["positionMs"] = (long)pos.TotalMilliseconds }, null);
                await SendAsync("/resume", null, () => _sentPlaying = true);
            }
            else
            {
                await SendAsync("/pause", null, () => _sentPlaying = false);
            }
            Remember();
            return;
        }

        // перемотали в Капсуле — позиция плеера ушла от ожидаемой больше чем на 3 секунды.
        // Сравниваем плеер с самим собой, а не с ботом: бот может отставать на время запуска трека, и это нормально.
        if (s.IsPlaying && !live)
        {
            TimeSpan expected = _lastPos + (DateTimeOffset.Now - _lastAt);
            if (Math.Abs((pos - expected).TotalSeconds) > 3)
                await SendAsync("/seek", new JsonObject { ["positionMs"] = (long)pos.TotalMilliseconds }, null);
        }
        Remember();
    }

    private static TimeSpan PosOf(MusicState? s)
    {
        if (s == null) return TimeSpan.Zero;
        TimeSpan pos = s.Position + (s.IsPlaying ? DateTimeOffset.Now - s.PositionUpdated : TimeSpan.Zero);
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }

    /// <summary>Запомнить, где плеер сейчас (после запроса к боту — заново, запрос мог идти несколько секунд).</summary>
    private void Remember()
    {
        _lastPos = PosOf(_hub.Active?.State);
        _lastAt = DateTimeOffset.Now;
    }

    /// <summary>Запрос к боту. Нет связи — выключаем «В Discord» и ставим плеер на паузу.</summary>
    private async Task SendAsync(string path, JsonObject? body, Action? onOk, Action? onRejected = null)
    {
        _sending = true;
        try
        {
            using HttpRequestMessage req = Request(HttpMethod.Post, path);
            req.Content = JsonContent.Create(body ?? new JsonObject());
            using HttpResponseMessage res = await Http.SendAsync(req);
            if (!Enabled) return;
            if (res.IsSuccessStatusCode)
            {
                onOk?.Invoke();
                return;
            }
            string message = "";
            string error = "";
            try
            {
                JsonNode? n = JsonNode.Parse(await res.Content.ReadAsStringAsync());
                message = n?["message"]?.ToString() ?? "";
                error = n?["error"]?.ToString() ?? "";
            }
            catch
            {
                // не JSON — покажем код
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
            {
                Disable("Discord-бот: неверный ключ.", pauseLocal: true, tellBot: false);
                return;
            }
            if (error == "not_in_voice")
            {
                Disable("Discord-бот: " + message, pauseLocal: true, tellBot: false);
                return;
            }
            onRejected?.Invoke();
            _hub.SetStatus("Discord-бот: " + (message.Length > 0 ? message : $"ошибка {(int)res.StatusCode}"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (Enabled) Disable("Discord-бот не отвечает — музыка на паузе.", pauseLocal: true, tellBot: false);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            _sending = false;
        }
    }

    private static async Task PostAsync(string path)
    {
        try
        {
            using HttpRequestMessage req = Request(HttpMethod.Post, path);
            req.Content = JsonContent.Create(new JsonObject());
            using HttpResponseMessage _ = await Http.SendAsync(req);
        }
        catch
        {
            // бот недоступен — неважно
        }
    }

    private static HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + App.Settings.DiscordBotKey);
        return req;
    }

    // ---------- события от бота ----------

    private void StartEvents()
    {
        _eventsCts?.Cancel();
        _eventsCts = new CancellationTokenSource();
        _ = EventsLoopAsync(_eventsCts.Token);
    }

    private async Task EventsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using HttpRequestMessage req = Request(HttpMethod.Get, "/events");
                using HttpResponseMessage res = await EventsHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                res.EnsureSuccessStatusCode();
                using Stream stream = await res.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);
                string? ev = null;
                while (!ct.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (line.StartsWith("event: ")) ev = line[7..];
                    else if (line.StartsWith("data: ") && ev != null) OnEvent(ev, line[6..]);
                    else if (line.Length == 0) ev = null;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // связь оборвалась — переподключимся; если бот совсем пропал, это заметит SyncAsync
            }
            try
            {
                await Task.Delay(3000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnEvent(string ev, string data)
    {
        if (!Enabled) return;
        JsonNode? n;
        try
        {
            n = JsonNode.Parse(data);
        }
        catch
        {
            return;
        }
        switch (ev)
        {
            case "stopped":
                string reason = n?["reason"]?.ToString() ?? "";
                Disable(reason switch
                {
                    "radio" => "В Discord включили радио — музыка из Капсулы на паузе.",
                    "discord-stop" => "Музыку в Discord остановили командой /stop — в Капсуле она на паузе.",
                    "left" => "Бот вышел из голосового канала — музыка на паузе.",
                    _ => "Discord-бот остановил музыку.",
                }, pauseLocal: true, tellBot: false);
                break;
            case "error":
                _hub.SetStatus("Discord-бот: " + (n?["message"]?.ToString() ?? "ошибка"));
                break;
        }
    }

    public void Dispose()
    {
        if (!Enabled) return;
        Enabled = false;
        _timer.Stop();
        _eventsCts?.Cancel();
        // при выходе из Капсулы — остановить музыку в Discord (недолго ждём, без потока интерфейса)
        try
        {
            Task.Run(() => PostAsync("/stop")).Wait(1500);
        }
        catch
        {
            // неважно
        }
    }
}

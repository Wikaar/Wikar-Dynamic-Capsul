using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WikarIsland.Views;

namespace WikarIsland.Services;

/// <summary>
/// YouTube Music через настоящий сайт в невидимом встроенном браузере: тот же профиль WebView2, что у ИИ-вкладок,
/// поэтому вход в Google общий (если уже вошёл в Gemini — уже вошёл и сюда). Поиск — внутренним API сайта,
/// звук играет сама страница; остров только показывает и управляет. Никакого скачивания потоков в обход сайта.
/// </summary>
public sealed class YouTubeMusicProvider : IMusicProvider
{
    private const string Home = "https://music.youtube.com/";

    // фильтр «Песни» в поиске YouTube Music
    private const string SongsFilter = "EgWKAQIIAWoKEAkQBRAKEAMQBA==";

    private Window? _host;
    private WebView2? _web;
    private Task<CoreWebView2>? _init;
    private readonly Dictionary<int, TaskCompletionSource<string>> _pending = new();
    private int _nextId;
    private MusicState? _state;
    private bool _loggedIn;
    private bool _active;

    public string Key => "youtube";
    public string Name => "YouTube Music";
    public string MixName => "Понравившиеся";
    public string? FavoritesName => null;

    /// <summary>Искать и слушать можно и без входа; вход нужен для своих плейлистов.</summary>
    public bool IsLoggedIn => _loggedIn || App.Settings.MusicYoutubeUsed;
    public string Account => _loggedIn ? "аккаунт Google" : "";
    public MusicState? State => _active ? _state : null;

    public event Action? StateChanged;
    public event Action<string>? Error;

    // ---------- невидимая страница ----------

    private Task<CoreWebView2> CoreAsync()
    {
        if (_init is { IsFaulted: true }) _init = null;
        return _init ??= CreateAsync();
    }

    private async Task<CoreWebView2> CreateAsync()
    {
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
        // окно за пределами экрана: страница считает себя открытой и играет как обычная вкладка
        _host = new Window
        {
            Title = "YouTube Music — Wikar Island",
            Width = 480,
            Height = 360,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = _web,
        };
        _host.SourceInitialized += (_, _) =>
            NativeMethods.AddExStyle(new WindowInteropHelper(_host).Handle, NativeMethods.WS_EX_TOOLWINDOW);
        _host.Show();

        CoreWebView2Environment env = await PanelWindow.GetEnvironmentAsync();
        await _web.EnsureCoreWebView2Async(env);
        CoreWebView2 core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.WebMessageReceived += OnMessage;
        await NavigateAsync(core, Home);
        await CheckLoginAsync(core);
        return core;
    }

    private static Task NavigateAsync(CoreWebView2 core, string url)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Done(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            core.NavigationCompleted -= Done;
            tcs.TrySetResult();
        }
        core.NavigationCompleted += Done;
        core.Navigate(url);
        return Task.WhenAny(tcs.Task, Task.Delay(20000));
    }

    private async Task CheckLoginAsync(CoreWebView2 core)
    {
        string r = await core.ExecuteScriptAsync("!!(window.ytcfg && ytcfg.get('LOGGED_IN'))");
        _loggedIn = r == "true";
    }

    /// <summary>Страница ещё не открыта или застряла на согласии с cookies — внятная подсказка.</summary>
    private static void EnsureOnSite(CoreWebView2 core)
    {
        if (!core.Source.StartsWith(Home, StringComparison.OrdinalIgnoreCase))
            throw new MusicException("YouTube Music просит подтвердить cookies или вход: нажми «Войти» и прими условия на сайте.");
    }

    // ---------- вход ----------

    public async Task<string?> LoginAsync()
    {
        string start = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" + Uri.EscapeDataString(Home);
        string? url = await LoginWindow.CaptureAsync(start, u => u.StartsWith(Home, StringComparison.OrdinalIgnoreCase), "Вход в YouTube Music");
        if (url == null) return "Вход отменён.";
        App.Settings.MusicYoutubeUsed = true;
        App.Settings.Save();
        CoreWebView2 core = await CoreAsync();
        await NavigateAsync(core, Home);
        await CheckLoginAsync(core);
        return _loggedIn ? null : "Google не подтвердил вход — попробуй ещё раз.";
    }

    /// <summary>Выход из Google разлогинил бы и ИИ-вкладки, поэтому просто отключаем сервис в острове.</summary>
    public void Logout()
    {
        Stop();
        App.Settings.MusicYoutubeUsed = false;
        App.Settings.Save();
        _loggedIn = false;
        Error?.Invoke("YouTube Music отключён в острове. Аккаунт Google остался (он общий с ИИ-вкладками) — сменить его можно на сайте.");
    }

    public async Task RefreshAccountAsync()
    {
        // страницу открываем только когда сервисом реально пользуются — она тяжёлая
        if (_init == null) return;
        await CheckLoginAsync(await CoreAsync());
    }

    // ---------- поиск ----------

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken ct)
    {
        App.Settings.MusicYoutubeUsed = true;
        JsonNode? r = await InnertubeAsync("search", new Dictionary<string, object> { ["query"] = query, ["params"] = SongsFilter }, ct);
        var found = new List<JsonNode>();
        Walk(r, found);
        var list = new List<MusicTrack>();
        foreach (JsonNode item in found)
        {
            MusicTrack? t = ParseItem(item);
            if (t != null && list.All(x => x.Id != t.Id)) list.Add(t);
        }
        return list;
    }

    public Task<IReadOnlyList<MusicTrack>> FavoritesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MusicTrack>>(Array.Empty<MusicTrack>());

    /// <summary>Запрос к внутреннему API сайта изнутри страницы — с её cookies и настройками.</summary>
    private async Task<JsonNode?> InnertubeAsync(string endpoint, Dictionary<string, object> payload, CancellationToken ct)
    {
        CoreWebView2 core = await CoreAsync();
        EnsureOnSite(core);
        int id = ++_nextId;
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        string js =
            "(async()=>{try{const b=" + JsonSerializer.Serialize(payload) + ";b.context=ytcfg.get('INNERTUBE_CONTEXT');" +
            "const r=await fetch('/youtubei/v1/" + endpoint + "?prettyPrint=false',{method:'POST',credentials:'include'," +
            "headers:{'Content-Type':'application/json'},body:JSON.stringify(b)});" +
            "chrome.webview.postMessage(JSON.stringify({id:" + id + ",ok:r.ok,body:await r.text()}));}" +
            "catch(e){chrome.webview.postMessage(JSON.stringify({id:" + id + ",ok:false,body:String(e)}));}})();";
        await core.ExecuteScriptAsync(js);
        Task finished = await Task.WhenAny(tcs.Task, Task.Delay(15000, ct));
        if (finished != tcs.Task)
        {
            _pending.Remove(id);
            ct.ThrowIfCancellationRequested();
            throw new MusicException("YouTube Music не ответил.");
        }
        return JsonNode.Parse(await tcs.Task);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            JsonNode? n = JsonNode.Parse(e.TryGetWebMessageAsString());
            int id = (int?)n?["id"] ?? -1;
            if (!_pending.Remove(id, out TaskCompletionSource<string>? tcs)) return;
            if ((bool?)n?["ok"] == true) tcs.TrySetResult(n?["body"]?.ToString() ?? "");
            else tcs.TrySetException(new MusicException("YouTube Music: ошибка запроса."));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static void Walk(JsonNode? node, List<JsonNode> found)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (KeyValuePair<string, JsonNode?> kv in o)
                {
                    if (kv.Key == "musicResponsiveListItemRenderer" && kv.Value != null) found.Add(kv.Value);
                    else Walk(kv.Value, found);
                }
                break;
            case JsonArray a:
                foreach (JsonNode? x in a) Walk(x, found);
                break;
        }
    }

    private static string? FindString(JsonNode? node, string key)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (KeyValuePair<string, JsonNode?> kv in o)
                {
                    if (kv.Key == key && kv.Value is JsonValue v) return v.ToString();
                    string? inner = FindString(kv.Value, key);
                    if (inner != null) return inner;
                }
                break;
            case JsonArray a:
                foreach (JsonNode? x in a)
                {
                    string? inner = FindString(x, key);
                    if (inner != null) return inner;
                }
                break;
        }
        return null;
    }

    private static MusicTrack? ParseItem(JsonNode item)
    {
        string? videoId = item["playlistItemData"]?["videoId"]?.ToString() ?? FindString(item["overlay"], "videoId");
        if (string.IsNullOrEmpty(videoId)) return null;
        JsonArray cols = item["flexColumns"] as JsonArray ?? new JsonArray();

        string Column(int i) => cols.Count > i
            ? string.Concat(((cols[i]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]?["runs"]) as JsonArray ?? new JsonArray())
                .Select(r => r?["text"]?.ToString()))
            : "";

        string title = Column(0);
        if (title.Length == 0) return null;
        // «Исполнитель • Альбом • 3:45» (в общем поиске ещё «Песня • » в начале)
        List<string> parts = Column(1).Split(" • ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        if (parts.Count > 1 && parts[0] is "Песня" or "Song" or "Трек") parts.RemoveAt(0);
        TimeSpan duration = TimeSpan.Zero;
        string? last = parts.LastOrDefault();
        if (last != null && Regex.IsMatch(last, @"^\d+:\d\d(:\d\d)?$"))
        {
            string[] t = last.Split(':');
            duration = t.Length == 3
                ? new TimeSpan(int.Parse(t[0]), int.Parse(t[1]), int.Parse(t[2]))
                : new TimeSpan(0, int.Parse(t[0]), int.Parse(t[1]));
            parts.RemoveAt(parts.Count - 1);
        }
        string artist = parts.FirstOrDefault() ?? "";
        string album = parts.Count > 1 ? parts[1] : "";

        JsonArray? thumbs = item["thumbnail"]?["musicThumbnailRenderer"]?["thumbnail"]?["thumbnails"] as JsonArray;
        string? cover = thumbs?.LastOrDefault()?["url"]?.ToString();
        if (cover != null) cover = Regex.Replace(cover, @"=w\d+-h\d+", "=w400-h400");
        return new MusicTrack("youtube", videoId, title, artist, cover, duration) { Album = album };
    }

    // ---------- воспроизведение: управляем страницей ----------

    public async Task PlayAsync(IReadOnlyList<MusicTrack> queue, int index)
    {
        if (index < 0 || index >= queue.Count) return;
        App.Settings.MusicYoutubeUsed = true;
        await OpenAndPlayAsync("https://music.youtube.com/watch?v=" + Uri.EscapeDataString(queue[index].Id));
    }

    public async Task PlayMixAsync()
    {
        CoreWebView2 core = await CoreAsync();
        if (!_loggedIn) await CheckLoginAsync(core);
        if (!_loggedIn) throw new MusicException("Чтобы включить понравившиеся, войди в аккаунт Google (кнопка «Войти»).");
        await OpenAndPlayAsync("https://music.youtube.com/watch?list=LM");
    }

    private async Task OpenAndPlayAsync(string url)
    {
        CoreWebView2 core = await CoreAsync();
        _active = true;
        await NavigateAsync(core, url);
        EnsureOnSite(core);
        // если браузер всё же придержал автозапуск — запускаем сами
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            await PollAsync();
            if (_state?.IsPlaying == true) return;
        }
        await core.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');if(v&&v.paused)v.play();})()");
    }

    private void Script(string js)
    {
        if (_web?.CoreWebView2 is { } core) _ = core.ExecuteScriptAsync(js);
        _ = Task.Delay(300).ContinueWith(_ => PollAsync(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void PlayPause() => Script("(()=>{const v=document.querySelector('video');if(v){v.paused?v.play():v.pause();}})()");
    public void Next() => Script("document.querySelector('ytmusic-player-bar .next-button')?.click()");
    public void Previous() => Script("document.querySelector('ytmusic-player-bar .previous-button')?.click()");

    public void Seek(TimeSpan position) =>
        Script($"(()=>{{const v=document.querySelector('video');if(v)v.currentTime={position.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)};}})()");

    public void Stop()
    {
        if (_web?.CoreWebView2 is { } core) _ = core.ExecuteScriptAsync("(()=>{const v=document.querySelector('video');if(v)v.pause();})()");
        _active = false;
        _state = null;
        StateChanged?.Invoke();
    }

    /// <summary>Что играет — из Media Session страницы (то же, что сайт отдаёт в медиапульт Windows).</summary>
    public async Task PollAsync()
    {
        if (!_active || _web?.CoreWebView2 is not { } core) return;
        try
        {
            string raw = await core.ExecuteScriptAsync(
                "(()=>{const v=document.querySelector('video');const m=navigator.mediaSession&&navigator.mediaSession.metadata;" +
                "const art=m&&m.artwork&&m.artwork.length?m.artwork[m.artwork.length-1].src:'';" +
                "return {t:m?m.title:'',a:m?m.artist:'',c:art,p:!!(v&&!v.paused&&!v.ended),pos:v?v.currentTime:0," +
                "d:v&&isFinite(v.duration)?v.duration:0};})()");
            JsonNode? n = JsonNode.Parse(raw);
            string title = n?["t"]?.ToString() ?? "";
            MusicState? next = title.Length == 0 ? null : new MusicState
            {
                Title = title,
                Artist = n?["a"]?.ToString() ?? "",
                CoverUrl = n?["c"]?.ToString() is { Length: > 0 } c ? c : null,
                IsPlaying = (bool?)n?["p"] == true,
                Position = TimeSpan.FromSeconds((double?)n?["pos"] ?? 0),
                Duration = TimeSpan.FromSeconds((double?)n?["d"] ?? 0),
                PositionUpdated = DateTimeOffset.Now,
            };
            bool changed = next == null ? _state != null : !next.SameAs(_state) || Jumped(_state, next);
            _state = next;
            if (changed) StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    /// <summary>Перемотали (позиция ушла от ожидаемой больше чем на 3 секунды).</summary>
    private static bool Jumped(MusicState? before, MusicState now)
    {
        if (before == null) return false;
        TimeSpan expected = before.Position + (before.IsPlaying ? now.PositionUpdated - before.PositionUpdated : TimeSpan.Zero);
        return Math.Abs((now.Position - expected).TotalSeconds) > 3;
    }

    public void Dispose()
    {
        try
        {
            _web?.Dispose();
            _host?.Close();
        }
        catch
        {
            // при выходе — неважно
        }
    }
}

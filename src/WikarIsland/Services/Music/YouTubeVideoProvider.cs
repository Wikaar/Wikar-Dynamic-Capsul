using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WikarIsland.Views;

namespace WikarIsland.Services;

/// <summary>
/// Видео YouTube прямо в острове: настоящий youtube.com во встроенном браузере (тот же профиль WebView2,
/// что у ИИ-вкладок и YouTube Music — вход в Google общий), в своём окне: обычном, на весь экран или мини-плеером.
/// Поиск — внутренним API сайта изнутри страницы; управление — через API плеера на странице,
/// поэтому команды вроде «перемотай на 5:30» и «качество 1080» работают точно, а не нажатием клавиш.
/// </summary>
public sealed class YouTubeVideoProvider : IMusicProvider
{
    private const string Home = "https://www.youtube.com/";

    // фильтр «Видео» в поиске YouTube
    private const string VideosFilter = "EgIQAQ==";

    private const string Player = "document.getElementById('movie_player')";

    // слой «плеер на всё окно» для мини-плеера и полноэкранного режима по голосу (страничный requestFullscreen без клика не разрешён)
    private const string FillCss =
        "html.wikar-fill,html.wikar-fill body{overflow:hidden!important}" +
        "html.wikar-fill #movie_player{position:fixed!important;left:0!important;top:0!important;width:100vw!important;height:100vh!important;" +
        "z-index:2147483646!important;background:#000!important}" +
        "html.wikar-fill #movie_player .html5-video-container,html.wikar-fill #movie_player video{width:100%!important;height:100%!important;left:0!important;top:0!important}" +
        "html.wikar-fill #movie_player video{object-fit:contain!important}" +
        // предки плеера с transform/contain держали бы position:fixed внутри колонки страницы, а не всего окна
        "html.wikar-fill ytd-app,html.wikar-fill ytd-page-manager,html.wikar-fill ytd-watch-flexy,html.wikar-fill #columns," +
        "html.wikar-fill #primary,html.wikar-fill #primary-inner,html.wikar-fill #player,html.wikar-fill #player-container," +
        "html.wikar-fill #player-container-outer,html.wikar-fill #player-container-inner,html.wikar-fill #full-bleed-container," +
        "html.wikar-fill #player-full-bleed-container,html.wikar-fill ytd-player,html.wikar-fill ytd-player #container" +
        "{transform:none!important;contain:none!important;filter:none!important;will-change:auto!important;z-index:auto!important}" +
        // шапка, рекомендации справа и всё под видео — прочь, чтобы ничего не выглядывало поверх и рядом
        "html.wikar-fill #masthead-container,html.wikar-fill #secondary,html.wikar-fill #below,html.wikar-fill tp-yt-app-drawer," +
        "html.wikar-fill ytd-mini-guide-renderer{visibility:hidden!important}";

    // кнопка YouTube «Во весь экран»; запускается через DevTools как действие пользователя, иначе браузер не пустит в полный экран
    private const string PageFullscreenJs =
        "(()=>{if(document.fullscreenElement)return true;" +
        "const b=document.querySelector('#movie_player .ytp-fullscreen-button');if(b&&b.offsetParent!==null){b.click();return true;}" +
        "const p=document.getElementById('movie_player');if(p&&p.requestFullscreen){p.requestFullscreen().catch(()=>{});return true;}return false;})()";

    private VideoWindow? _window;
    private Task<CoreWebView2>? _init;
    private readonly Dictionary<int, TaskCompletionSource<string>> _pending = new();
    private int _nextId;
    private MusicState? _state;
    private bool _loggedIn;
    private bool _active;
    private bool _pageFullscreen;
    private bool _goingBack;

    // очередь из поиска: «следующее» — следующий найденный ролик, а не случайная рекомендация
    private IReadOnlyList<MusicTrack> _queue = Array.Empty<MusicTrack>();
    private int _queueIndex = -1;

    // что уже смотрели — для «предыдущее видео»
    private readonly List<string> _watched = new();

    public string Key => "ytvideo";
    public string Name => "YouTube";
    public string MixName => "Главная";
    public string? FavoritesName => null;

    /// <summary>Смотреть можно и без входа.</summary>
    public bool IsLoggedIn => true;
    public string Account => _loggedIn ? "аккаунт Google" : "";
    public MusicState? State => _active ? _state : null;

    /// <summary>Окно видео сейчас на экране.</summary>
    public bool IsShown => _window is { Mode: not VideoMode.Hidden };

    /// <summary>Что-то смотрят или стоит на паузе во встроенном плеере.</summary>
    public bool HasVideo => _active && _state != null;

    public VideoMode Mode => _window?.Mode ?? VideoMode.Hidden;

    /// <summary>Куда вернуться из полноэкранного режима: в окно или мини-плеер.</summary>
    public VideoMode BeforeFull => _window?.BeforeFull ?? VideoMode.Normal;

    public event Action? StateChanged;
    public event Action<string>? Error;

    // ---------- страница и окно ----------

    private Task<CoreWebView2> CoreAsync()
    {
        if (_init is { IsFaulted: true }) _init = null;
        return _init ??= CreateAsync();
    }

    private async Task<CoreWebView2> CreateAsync()
    {
        if (_window == null)
        {
            _window = new VideoWindow();
            _window.ClosedByUser += () =>
            {
                // крестик — пауза и убрать из пилюли
                if (_active) App.Music.StopIfActive(this);
            };
            _window.ModeChanged += mode => _ = ApplyFillAsync(mode);
        }
        _window.ShowOffscreen();

        CoreWebView2Environment env = await PanelWindow.GetEnvironmentAsync();
        await _window.Web.EnsureCoreWebView2Async(env);
        CoreWebView2 core = _window.Web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.WebMessageReceived += OnMessage;
        core.DocumentTitleChanged += (_, _) =>
        {
            if (_window != null) _window.Title = (string.IsNullOrWhiteSpace(core.DocumentTitle) ? "YouTube" : core.DocumentTitle) + " — Wikar Dynamic Capsul";
        };
        core.ContainsFullScreenElementChanged += (_, _) => OnPageFullscreen(core.ContainsFullScreenElement);
        core.SourceChanged += (_, _) => OnSourceChanged(core.Source);
        PanelWindow.HookNewWindows(core);
        if (_window.Mode == VideoMode.Hidden) _window.Hide();

        // Esc на странице, пока плеер растянут нами (не настоящий полноэкранный режим страницы)
        await core.AddScriptToExecuteOnDocumentCreatedAsync(
            "(()=>{const s=document.createElement('style');s.textContent=" + JsonSerializer.Serialize(FillCss) + ";" +
            "const add=()=>{if(document.head)document.head.appendChild(s);else setTimeout(add,50);};add();" +
            "window.addEventListener('keydown',e=>{if(e.key==='Escape'&&document.documentElement.classList.contains('wikar-fill')&&!document.fullscreenElement)" +
            "chrome.webview.postMessage(JSON.stringify({type:'esc'}));},true);})();");

        _ = AdBlock.EnsureAsync(core);
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
        try
        {
            string r = await core.ExecuteScriptAsync("!!(window.ytcfg && ytcfg.get('LOGGED_IN'))");
            _loggedIn = r == "true";
        }
        catch
        {
            _loggedIn = false;
        }
    }

    /// <summary>В Европе без входа YouTube сначала просит согласие на cookies — показываем окно, чтобы его принять.</summary>
    private void EnsureOnSite(CoreWebView2 core)
    {
        if (core.Source.StartsWith("https://www.youtube.com", StringComparison.OrdinalIgnoreCase)) return;
        _window?.SetMode(VideoMode.Normal);
        throw new MusicException("YouTube просит принять cookies или войти — сделай это в окне видео и повтори.");
    }

    private void OnSourceChanged(string url)
    {
        string? id = VideoId(url);
        if (id == null) return;
        if (_goingBack)
        {
            _goingBack = false;
            return;
        }
        if (_watched.Count == 0 || _watched[^1] != id) _watched.Add(id);
        if (_watched.Count > 50) _watched.RemoveAt(0);
        // ролик сменился не из очереди (рекомендация, клик) — очередь поиска больше не продолжаем
        if (_queueIndex >= 0 && (_queueIndex >= _queue.Count || _queue[_queueIndex].Id != id))
        {
            int at = _queue.ToList().FindIndex(t => t.Id == id);
            _queueIndex = at;
        }
    }

    private static string? VideoId(string url)
    {
        Match m = Regex.Match(url, @"youtube\.com/(?:watch\?(?:.*&)?v=|shorts/)([\w\-]{6,})");
        return m.Success ? m.Groups[1].Value : null;
    }

    private void OnPageFullscreen(bool full)
    {
        if (_window == null) return;
        _pageFullscreen = full;
        if (full) _window.SetMode(VideoMode.Full);
        else if (_window.Mode == VideoMode.Full) _window.SetMode(_window.BeforeFull);
    }

    private async Task ApplyFillAsync(VideoMode mode)
    {
        if (_window?.Web.CoreWebView2 is not { } core) return;
        bool fill = mode == VideoMode.Mini || (mode == VideoMode.Full && !_pageFullscreen);
        try
        {
            await core.ExecuteScriptAsync(
                "(()=>{document.documentElement.classList.toggle('wikar-fill'," + (fill ? "true" : "false") + ");" +
                "setTimeout(()=>window.dispatchEvent(new Event('resize')),60);})()");
        }
        catch
        {
            // страница перезагружается
        }
    }

    // ---------- вход ----------

    public async Task<string?> LoginAsync()
    {
        string start = "https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=" + Uri.EscapeDataString(Home);
        string? url = await LoginWindow.CaptureAsync(start, u => u.StartsWith(Home, StringComparison.OrdinalIgnoreCase), "Вход в YouTube");
        if (url == null) return "Вход отменён.";
        CoreWebView2 core = await CoreAsync();
        await NavigateAsync(core, Home);
        await CheckLoginAsync(core);
        return _loggedIn ? null : "Google не подтвердил вход — попробуй ещё раз.";
    }

    /// <summary>Выход из Google разлогинил бы и ИИ-вкладки — только останавливаем видео.</summary>
    public void Logout()
    {
        Stop();
        Error?.Invoke("Аккаунт Google общий с ИИ-вкладками и YouTube Music — сменить его можно на самом сайте в окне видео.");
    }

    public async Task RefreshAccountAsync()
    {
        if (_init == null) return; // страницу открываем, только когда YouTube нужен
        await CheckLoginAsync(await CoreAsync());
    }

    // ---------- поиск ----------

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken ct)
    {
        JsonNode? r = await InnertubeAsync("search", new Dictionary<string, object> { ["query"] = query, ["params"] = VideosFilter }, ct);
        var found = new List<JsonNode>();
        Walk(r, found);
        var list = new List<MusicTrack>();
        foreach (JsonNode item in found)
        {
            MusicTrack? t = ParseVideo(item);
            if (t != null && list.All(x => x.Id != t.Id)) list.Add(t);
        }
        return list;
    }

    public Task<IReadOnlyList<MusicTrack>> FavoritesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MusicTrack>>(Array.Empty<MusicTrack>());

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
            throw new MusicException("YouTube не ответил.");
        }
        return JsonNode.Parse(await tcs.Task);
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            JsonNode? n = JsonNode.Parse(e.TryGetWebMessageAsString());
            if (n?["type"]?.ToString() == "esc")
            {
                if (_window?.Mode == VideoMode.Full) _window.SetMode(_window.BeforeFull);
                return;
            }
            int id = (int?)n?["id"] ?? -1;
            if (!_pending.Remove(id, out TaskCompletionSource<string>? tcs)) return;
            if ((bool?)n?["ok"] == true) tcs.TrySetResult(n?["body"]?.ToString() ?? "");
            else tcs.TrySetException(new MusicException("YouTube: ошибка запроса."));
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
                    if (kv.Key == "videoRenderer" && kv.Value != null) found.Add(kv.Value);
                    else Walk(kv.Value, found);
                }
                break;
            case JsonArray a:
                foreach (JsonNode? x in a) Walk(x, found);
                break;
        }
    }

    private static string Runs(JsonNode? text) =>
        text?["simpleText"]?.ToString()
        ?? string.Concat(((text?["runs"]) as JsonArray ?? new JsonArray()).Select(r => r?["text"]?.ToString()));

    private static MusicTrack? ParseVideo(JsonNode item)
    {
        string? id = item["videoId"]?.ToString();
        string title = Runs(item["title"]);
        if (string.IsNullOrEmpty(id) || title.Length == 0) return null;
        string channel = Runs(item["ownerText"]);
        if (channel.Length == 0) channel = Runs(item["longBylineText"]);
        TimeSpan duration = ParseDuration(Runs(item["lengthText"]));
        string views = Runs(item["shortViewCountText"]);
        return new MusicTrack("ytvideo", id, title, channel, $"https://i.ytimg.com/vi/{id}/mqdefault.jpg", duration)
        {
            Album = views,
        };
    }

    private static TimeSpan ParseDuration(string text)
    {
        if (!Regex.IsMatch(text, @"^\d+(:\d\d){1,2}$")) return TimeSpan.Zero;
        int[] p = text.Split(':').Select(int.Parse).ToArray();
        return p.Length == 3 ? new TimeSpan(p[0], p[1], p[2]) : new TimeSpan(0, p[0], p[1]);
    }

    // ---------- воспроизведение ----------

    public async Task PlayAsync(IReadOnlyList<MusicTrack> queue, int index)
    {
        if (index < 0 || index >= queue.Count) return;
        _queue = queue;
        _queueIndex = index;
        await OpenAsync("https://www.youtube.com/watch?v=" + Uri.EscapeDataString(queue[index].Id));
    }

    /// <summary>«Главная»: открыть YouTube в окне — дальше выбирают сами.</summary>
    public async Task PlayMixAsync()
    {
        CoreWebView2 core = await CoreAsync();
        _queue = Array.Empty<MusicTrack>();
        _queueIndex = -1;
        ShowWindow();
        if (!core.Source.StartsWith(Home, StringComparison.OrdinalIgnoreCase) || VideoId(core.Source) != null) await NavigateAsync(core, Home);
        _active = true;
    }

    private void ShowWindow()
    {
        if (_window == null) return;
        if (_window.Mode == VideoMode.Hidden) _window.SetMode(App.Settings.VideoStartMini ? VideoMode.Mini : VideoMode.Normal);
        else if (_window.Mode == VideoMode.Normal) _window.Activate();
    }

    private async Task OpenAsync(string url)
    {
        CoreWebView2 core = await CoreAsync();
        _active = true;
        ShowWindow();
        await NavigateAsync(core, url);
        EnsureOnSite(core);
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(500);
            await PollAsync();
            if (_state?.IsPlaying == true) return;
        }
        await core.ExecuteScriptAsync($"(()=>{{const p={Player};if(p&&p.playVideo)p.playVideo();else{{const v=document.querySelector('video');if(v&&v.paused)v.play();}}}})()");
    }

    private async Task<string> RunAsync(string js)
    {
        if (_window?.Web.CoreWebView2 is not { } core) return "null";
        try
        {
            string r = await core.ExecuteScriptAsync(js);
            _ = Task.Delay(300).ContinueWith(_ => PollAsync(), TaskScheduler.FromCurrentSynchronizationContext());
            return r;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return "null";
        }
    }

    private void Script(string js) => _ = RunAsync(js);

    public void PlayPause() =>
        Script($"(()=>{{const p={Player};if(p&&p.getPlayerState){{p.getPlayerState()===1?p.pauseVideo():p.playVideo();return;}}" +
               "const v=document.querySelector('video');if(v){v.paused?v.play():v.pause();}})()");

    public void Pause() => Script($"(()=>{{const p={Player};if(p&&p.pauseVideo)p.pauseVideo();else document.querySelector('video')?.pause();}})()");

    public void Resume() => Script($"(()=>{{const p={Player};if(p&&p.playVideo)p.playVideo();else document.querySelector('video')?.play();}})()");

    /// <summary>Следующий ролик из поиска, а когда очередь кончилась — следующий по версии YouTube.</summary>
    public void Next()
    {
        if (_queueIndex >= 0 && _queueIndex + 1 < _queue.Count)
        {
            _ = PlayAsync(_queue, _queueIndex + 1);
            return;
        }
        Script("document.querySelector('.ytp-next-button')?.click()");
    }

    /// <summary>Предыдущий ролик: из очереди поиска или из того, что уже смотрели.</summary>
    public void Previous()
    {
        if (_queueIndex > 0)
        {
            _ = PlayAsync(_queue, _queueIndex - 1);
            return;
        }
        if (_watched.Count >= 2)
        {
            _watched.RemoveAt(_watched.Count - 1);
            string id = _watched[^1];
            _goingBack = true;
            _ = OpenAsync("https://www.youtube.com/watch?v=" + id);
            return;
        }
        Seek(TimeSpan.Zero);
    }

    public void Seek(TimeSpan position)
    {
        string s = Math.Max(0, position.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        Script($"(()=>{{const p={Player};if(p&&p.seekTo)p.seekTo({s},true);else{{const v=document.querySelector('video');if(v)v.currentTime={s};}}}})()");
    }

    public void SeekBy(double seconds)
    {
        string s = seconds.ToString(CultureInfo.InvariantCulture);
        Script($"(()=>{{const v=document.querySelector('video');if(!v)return;const t=Math.max(0,v.currentTime+({s}));const p={Player};if(p&&p.seekTo)p.seekTo(t,true);else v.currentTime=t;}})()");
    }

    /// <summary>Скорость 0,25–2. Возвращает установленную.</summary>
    public async Task<double> SetSpeedAsync(double? exact, double step)
    {
        string e = exact?.ToString(CultureInfo.InvariantCulture) ?? "null";
        string st = step.ToString(CultureInfo.InvariantCulture);
        string r = await RunAsync(
            $"(()=>{{const p={Player};const v=document.querySelector('video');let cur=p&&p.getPlaybackRate?p.getPlaybackRate():(v?v.playbackRate:1);" +
            $"let n={e}!==null?{e}:cur+({st});n=Math.min(2,Math.max(0.25,Math.round(n*4)/4));" +
            "if(p&&p.setPlaybackRate)p.setPlaybackRate(n);else if(v)v.playbackRate=n;return n;})()");
        return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : 1;
    }

    private static readonly (int height, string level)[] Qualities =
    {
        (4320, "highres"), (2160, "hd2160"), (1440, "hd1440"), (1080, "hd1080"), (720, "hd720"), (480, "large"), (360, "medium"), (240, "small"), (144, "tiny"),
    };

    /// <summary>Качество: 1080, 720… или 0 — авто. Если такого нет у ролика — ближайшее ниже.</summary>
    public async Task<string> SetQualityAsync(int height)
    {
        if (height <= 0)
        {
            await RunAsync($"(()=>{{const p={Player};if(p&&p.setPlaybackQualityRange)p.setPlaybackQualityRange('auto');}})()");
            return "Качество — автоматически.";
        }
        string raw = await RunAsync($"(()=>{{const p={Player};return p&&p.getAvailableQualityLevels?p.getAvailableQualityLevels():[];}})()");
        List<string> levels;
        try
        {
            levels = JsonNode.Parse(raw) is JsonArray a ? a.Select(x => x?.ToString() ?? "").ToList() : new List<string>();
        }
        catch
        {
            levels = new List<string>();
        }
        (int height, string level)? pick = Qualities.Where(q => q.height <= height && (levels.Count == 0 || levels.Contains(q.level)))
            .Select(q => ((int, string)?)q).FirstOrDefault();
        if (pick == null) return "У этого ролика нет такого качества.";
        string level = pick.Value.level;
        await RunAsync($"(()=>{{const p={Player};if(p&&p.setPlaybackQualityRange)p.setPlaybackQualityRange('{level}','{level}');}})()");
        return pick.Value.height == height ? $"Качество {height}." : $"Такого нет, ставлю {pick.Value.height}.";
    }

    public void SetMuted(bool muted) =>
        Script($"(()=>{{const p={Player};if(p&&p.mute){{{(muted ? "p.mute()" : "p.unMute()")};return;}}const v=document.querySelector('video');if(v)v.muted={(muted ? "true" : "false")};}})()");

    /// <summary>Громкость самого плеера (0–1) или шаг.</summary>
    public async Task SetVolumeAsync(float? level, float? delta)
    {
        string l = level is float lv ? Math.Round(lv * 100).ToString(CultureInfo.InvariantCulture) : "null";
        string d = delta is float dv ? Math.Round(dv * 100).ToString(CultureInfo.InvariantCulture) : "0";
        await RunAsync(
            $"(()=>{{const p={Player};if(!p||!p.setVolume)return;let v={l}!==null?{l}:p.getVolume()+({d});" +
            "v=Math.max(0,Math.min(100,v));p.setVolume(v);if(v>0&&p.isMuted&&p.isMuted())p.unMute();})()");
    }

    /// <summary>Субтитры: включить, выключить или переключить (null).</summary>
    public async Task<bool> SetSubtitlesAsync(bool? on)
    {
        string r = await RunAsync(
            "(()=>{const b=document.querySelector('.ytp-subtitles-button');if(!b||b.style.display==='none')return 'none';" +
            "const cur=b.getAttribute('aria-pressed')==='true';" + (on == null ? "b.click();return !cur;" : $"if(cur!=={(on.Value ? "true" : "false")})b.click();return {(on.Value ? "true" : "false")};") + "})()");
        return r == "true";
    }

    public void SetWindowMode(VideoMode mode)
    {
        if (_window == null) return;
        if (mode == VideoMode.Full)
        {
            _ = EnterFullAsync();
            return;
        }
        // из настоящего полноэкранного режима страницы выходим через саму страницу
        if (_pageFullscreen && _window.Web.CoreWebView2 is { } core) _ = core.ExecuteScriptAsync("document.exitFullscreen&&document.exitFullscreen()");
        if (mode == VideoMode.Hidden)
        {
            Stop();
            return;
        }
        _window.SetMode(mode);
    }

    /// <summary>
    /// «На весь экран» голосом: настоящий полноэкранный режим самого плеера YouTube, как по кнопке или клавише F, —
    /// только видео, без рекомендаций и страницы вокруг. Если страница его не включила, растягиваем плеер на всё окно сами.
    /// </summary>
    private async Task EnterFullAsync()
    {
        if (_window == null) return;
        if (_pageFullscreen)
        {
            _window.SetMode(VideoMode.Full);
            return;
        }
        if (_window.Web.CoreWebView2 is { } core)
        {
            try
            {
                string args = JsonSerializer.Serialize(new { expression = PageFullscreenJs, userGesture = true, returnByValue = true });
                await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
                // окно на весь экран переключит событие страницы (OnPageFullscreen)
                for (int i = 0; i < 15 && !_pageFullscreen; i++) await Task.Delay(100);
            }
            catch
            {
                // страница перезагружается — ниже запасной путь
            }
        }
        if (!_pageFullscreen && _window.Mode != VideoMode.Full) _window.SetMode(VideoMode.Full);
    }

    /// <summary>Адрес текущего ролика с текущей секундой — для «открыть в браузере».</summary>
    public string? CurrentUrl
    {
        get
        {
            if (_window?.Web.CoreWebView2 is not { } core) return null;
            string? id = VideoId(core.Source);
            if (id == null) return core.Source.StartsWith("https://", StringComparison.Ordinal) ? core.Source : null;
            int t = (int)(_state?.Position.TotalSeconds ?? 0);
            return $"https://www.youtube.com/watch?v={id}" + (t > 5 ? $"&t={t}s" : "");
        }
    }

    public string OpenInBrowser()
    {
        string? url = CurrentUrl;
        if (url == null) return "Во встроенном плеере ничего не открыто.";
        Stop();
        AppIndex.OpenUri(url);
        return "Открываю в браузере.";
    }

    public void Stop()
    {
        if (_window?.Web.CoreWebView2 is { } core)
            _ = core.ExecuteScriptAsync($"(()=>{{const p={Player};if(p&&p.pauseVideo)p.pauseVideo();else document.querySelector('video')?.pause();}})()");
        _active = false;
        _state = null;
        if (_window != null && _window.Mode != VideoMode.Hidden) _window.SetMode(VideoMode.Hidden);
        StateChanged?.Invoke();
    }

    /// <summary>Раз в секунду: что играет, и нажать «Пропустить» на рекламе, как только кнопка появится.</summary>
    public async Task PollAsync()
    {
        if (!_active || _window?.Web.CoreWebView2 is not { } core) return;
        try
        {
            string raw = await core.ExecuteScriptAsync(
                "(()=>{const sk=document.querySelector('.ytp-skip-ad-button,.ytp-ad-skip-button,.ytp-ad-skip-button-modern');if(sk)sk.click();" +
                "const p=document.getElementById('movie_player');const v=document.querySelector('#movie_player video')||document.querySelector('video');" +
                "const m=navigator.mediaSession&&navigator.mediaSession.metadata;" +
                "const t=(m&&m.title)?m.title:document.title.replace(/^\\(\\d+\\)\\s*/,'').replace(/\\s*-\\s*YouTube$/,'');" +
                "return {u:location.href,t:t,a:m?m.artist:'',ad:!!(p&&p.classList&&p.classList.contains('ad-showing'))," +
                "p:!!(v&&!v.paused&&!v.ended),pos:v?v.currentTime:0,d:v&&isFinite(v.duration)?v.duration:0};})()");
            JsonNode? n = JsonNode.Parse(raw);
            string url = n?["u"]?.ToString() ?? "";
            string? id = VideoId(url);
            bool ad = (bool?)n?["ad"] == true;
            string title = n?["t"]?.ToString() ?? "";
            MusicState? next = id == null || title.Length == 0 ? null : new MusicState
            {
                Title = ad ? "Реклама" : title,
                Artist = ad ? "YouTube" : n?["a"]?.ToString() ?? "",
                CoverUrl = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
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

    private static bool Jumped(MusicState? before, MusicState now)
    {
        if (before == null) return false;
        TimeSpan expected = before.Position + (before.IsPlaying ? now.PositionUpdated - before.PositionUpdated : TimeSpan.Zero);
        return Math.Abs((now.Position - expected).TotalSeconds) > 3;
    }

    /// <summary>Настройка блокировщика поменялась.</summary>
    public Task ApplyAdBlockAsync() => AdBlock.ApplySettingAsync(_window?.Web.CoreWebView2);

    public void Dispose()
    {
        try
        {
            _window?.Web.Dispose();
            _window?.ForceClose();
        }
        catch
        {
            // при выходе — неважно
        }
    }
}

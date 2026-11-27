using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace WikarIsland.Services;

/// <summary>Станция интернет-радио в избранном (хранится в настройках).</summary>
public sealed class RadioStation
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Favicon { get; set; } = "";
    public string Info { get; set; } = "";
}

/// <summary>
/// Интернет-радио: каталог Radio Browser (radio-browser.info — открытая база станций, без ключей и входа),
/// звук играет наш <see cref="LocalPlayer"/> (MP3, AAC и HLS через Windows). Что сейчас звучит в эфире,
/// узнаём из ICY-метаданных потока: раз в 15 секунд коротко подключаемся и читаем один блок с названием.
/// «Следующий/предыдущий» переключают станции в текущем списке.
/// </summary>
public sealed class RadioProvider : IMusicProvider
{
    private static readonly string[] FallbackServers = { "de1.api.radio-browser.info", "de2.api.radio-browser.info", "fi1.api.radio-browser.info", "nl1.api.radio-browser.info" };
    private static readonly HttpClient Http = CreateHttp();
    private static List<string>? _servers;

    private readonly LocalPlayer _player = new();
    private readonly DispatcherTimer _icyTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private List<MusicTrack> _queue = new();
    private int _index = -1;
    private MusicTrack? _current;
    private string _onAir = "";
    private CancellationTokenSource? _icyCts;
    private bool _icyUnsupported;
    private DateTime _icyRetryAt = DateTime.MaxValue;
    private int _icyFailures;
    private string? _songCover;   // обложка песни в эфире (AMG / iTunes)
    private string? _stationLogo; // логотип с amgradio.ru, если в каталоге его нет
    private bool _logoBroken;     // логотип из каталога не открылся или это не картинка
    private CancellationTokenSource? _coverCts;

    public string Key => "radio";
    public string Name => "Радио";
    public string MixName => "Популярные";
    public string? FavoritesName => "Избранное";
    public bool IsLoggedIn => true; // входа нет
    public string Account => App.Settings.RadioRussiaOnly ? "станции России" : "станции всех стран";

    public event Action? StateChanged;
    public event Action<string>? Error;

    public RadioProvider()
    {
        _player.StateChanged += () => StateChanged?.Invoke();
        _player.NextRequested += Next;
        _player.PreviousRequested += Previous;
        // поток радио не кончается; если кончился — станция оборвала эфир
        _player.Ended += () => Error?.Invoke("Станция прервала эфир.");
        _player.Failed += message => Error?.Invoke($"Станция не отвечает: {message}. Выбери другую.");
        _icyTimer.Tick += async (_, _) => await RefreshOnAirAsync();
    }

    public MusicState? State
    {
        get
        {
            if (_current == null || !_player.HasSource) return null;
            (string artist, string title) = SplitOnAir(_onAir);
            return new MusicState
            {
                // в эфире песня — показываем её, станция идёт подписью; иначе — просто станция
                Title = title.Length > 0 ? title : _current.Title,
                Artist = title.Length > 0 ? (artist.Length > 0 ? $"{artist} · {_current.Title}" : _current.Title) : "Радио в эфире",
                CoverUrl = _songCover ?? StationCover,
                IsPlaying = _player.IsPlaying,
                Position = TimeSpan.Zero,
                Duration = TimeSpan.Zero, // эфир: без перемотки и полосы прогресса
                PositionUpdated = DateTimeOffset.Now,
                StreamUrl = _player.CurrentUri,
            };
        }
    }

    private string? StationCover => string.IsNullOrWhiteSpace(_current?.CoverUrl) || _logoBroken ? _stationLogo : _current!.CoverUrl;

    /// <summary>Сейчас играет станция (для Discord-бота: эфир не перематывается).</summary>
    public static bool IsLive(IMusicProvider? p) => p is RadioProvider;

    /// <summary>Название играющей станции или null.</summary>
    public string? CurrentStation => _player.HasSource ? _current?.Title : null;

    public void SetMuted(bool muted) => _player.SetMuted(muted);

    // ---------- входа нет ----------

    public Task<string?> LoginAsync() => Task.FromResult<string?>(null);
    public void Logout() { }
    public Task RefreshAccountAsync() => Task.CompletedTask;

    // ---------- каталог ----------

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken ct)
    {
        query = Regex.Replace(query.Trim(), @"^(?:радио\s*станци\w*|радиостанци\w*|радио)\s+", "", RegexOptions.IgnoreCase);
        if (query.Length == 0) return await TopAsync(ct);
        List<MusicTrack> found = await FindAsync(query, App.Settings.RadioRussiaOnly, ct);
        // в России не нашлось — поищем по всему миру
        if (found.Count == 0 && App.Settings.RadioRussiaOnly) found = await FindAsync(query, false, ct);
        return found;
    }

    /// <summary>Самые популярные станции (по прослушиваниям в Radio Browser).</summary>
    public async Task<IReadOnlyList<MusicTrack>> TopAsync(CancellationToken ct)
    {
        string country = App.Settings.RadioRussiaOnly ? "&countrycode=RU" : "";
        JsonArray list = await ApiAsync($"/json/stations/search?order=clickcount&reverse=true&hidebroken=true&limit=60{country}", ct);
        return Parse(list);
    }

    public Task<IReadOnlyList<MusicTrack>> FavoritesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MusicTrack>>(App.Settings.RadioFavorites.Select(ToTrack).ToList());

    public static bool IsFavorite(MusicTrack t) => App.Settings.RadioFavorites.Any(s => s.Id == t.Id);

    /// <summary>Добавить в избранное или убрать; возвращает новое состояние.</summary>
    public static bool ToggleFavorite(MusicTrack t)
    {
        List<RadioStation> favs = App.Settings.RadioFavorites;
        int i = favs.FindIndex(s => s.Id == t.Id);
        if (i >= 0) favs.RemoveAt(i);
        else favs.Add(ToStation(t));
        App.Settings.Save();
        return i < 0;
    }

    /// <summary>
    /// Поиск по названию с учётом того, как говорят голосом: «европу плюс» → «Европа Плюс», «рекорд» → «Radio Record».
    /// Radio Browser ищет подстроку, поэтому спрашиваем по основе самого длинного слова, а все слова сверяем у себя.
    /// </summary>
    private static async Task<List<MusicTrack>> FindAsync(string query, bool russiaOnly, CancellationToken ct)
    {
        List<string> stems = Regex.Split(query.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+")
            .Where(w => w.Length > 0).Select(Stem).ToList();
        if (stems.Count == 0) return new List<MusicTrack>();
        string longest = stems.OrderByDescending(s => s.Length).First();
        string country = russiaOnly ? "&countrycode=RU" : "";

        var requests = new List<string> { longest };
        if (HasCyrillic(longest))
        {
            string latin = Translit(longest);
            requests.Add(latin);
            if (latin.Contains('k')) requests.Add(latin.Replace('k', 'c'));
            if (latin.Contains("ev")) requests.Add(latin.Replace("ev", "eu")); // «европа» → «Europa»
        }

        var tasks = requests.Distinct().Select(async q =>
        {
            try
            {
                return await ApiAsync($"/json/stations/search?name={Uri.EscapeDataString(q)}&order=clickcount&reverse=true&hidebroken=true&limit=100{country}", ct);
            }
            catch (MusicException) when (q != longest)
            {
                return new JsonArray(); // дополнительный вариант не важен
            }
        }).ToList();
        JsonArray[] results = await Task.WhenAll(tasks);

        List<string> keys = stems.Select(Normalize).ToList();
        List<string> latinKeys = stems.Select(s => Normalize(Translit(s))).ToList();
        var seen = new HashSet<string>();
        var matches = new List<(MusicTrack track, long clicks)>();
        foreach (JsonNode? n in results.SelectMany(r => r))
        {
            MusicTrack? t = ParseStation(n);
            if (t == null || !seen.Add(t.Id)) continue;
            string name = Normalize(t.Title);
            string nameLatin = Normalize(Translit(t.Title.ToLowerInvariant()));
            bool all = keys.All(k => name.Contains(k)) || latinKeys.All(k => name.Contains(k) || nameLatin.Contains(k));
            if (!all) continue;
            long clicks = n?["clickcount"] is JsonValue c && c.TryGetValue(out long v) ? v : 0;
            matches.Add((t, clicks));
        }

        // ничего по названию — может, это жанр: «включи радио джаз»
        if (matches.Count == 0)
        {
            try
            {
                JsonArray byTag = await ApiAsync($"/json/stations/search?tag={Uri.EscapeDataString(TagOf(query))}&order=clickcount&reverse=true&hidebroken=true&limit=40{country}", ct);
                return Parse(byTag);
            }
            catch (MusicException)
            {
                return new List<MusicTrack>();
            }
        }
        return matches.OrderByDescending(m => m.clicks).Select(m => m.track).Take(60).ToList();
    }

    /// <summary>Основа слова для поиска: «европу» → «европ», «плюс» → «плюс», «рекорда» → «рекорд».</summary>
    private static string Stem(string w)
    {
        if (w.Length <= 3 || !HasCyrillic(w)) return w;
        return Regex.Replace(w, "(?:ами|ями|ого|его|ому|ему|ой|ей|ую|юю|ая|яя|ое|ее|ые|ие|ах|ях|ом|ем|у|ю|а|я|е|ы|и|о|ь|й)$", "");
    }

    /// <summary>Жанры голосом по-русски → теги Radio Browser (они на английском).</summary>
    private static string TagOf(string query)
    {
        string q = query.ToLowerInvariant().Trim();
        (string ru, string tag)[] map =
        {
            ("джаз", "jazz"), ("рок", "rock"), ("поп", "pop"), ("класси", "classical"), ("шансон", "chanson"),
            ("рэп", "hip-hop"), ("реп", "hip-hop"), ("хип", "hip-hop"), ("электрон", "electronic"), ("транс", "trance"),
            ("хаус", "house"), ("техно", "techno"), ("метал", "metal"), ("блюз", "blues"), ("ретро", "retro"),
            ("новост", "news"), ("разговор", "talk"), ("лаунж", "lounge"), ("чилл", "chillout"), ("релакс", "relax"),
            ("детск", "kids"), ("кантри", "country"), ("регги", "reggae"), ("диско", "disco"), ("фанк", "funk"),
            ("соул", "soul"), ("инди", "indie"), ("спорт", "sport"), ("юмор", "comedy"), ("80", "80s"), ("90", "90s"),
        };
        foreach ((string ru, string tag) in map)
            if (q.StartsWith(ru) || q.Contains(" " + ru)) return tag;
        return q;
    }

    private static bool HasCyrillic(string s) => s.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё');

    /// <summary>Для сравнения названий: без пробелов и знаков, латиница сведена к одному написанию (c→k, ph→f, w→v, eu→ev).</summary>
    private static string Normalize(string s)
    {
        string t = Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{N}]+", "");
        return t.Replace("ph", "f").Replace("eu", "ev").Replace("ck", "k").Replace('c', 'k').Replace('w', 'v').Replace("x", "ks").Replace('y', 'i');
    }

    private static string Translit(string s)
    {
        var sb = new StringBuilder(s.Length + 4);
        foreach (char ch in s.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' => "e", 'ё' => "e", 'ж' => "zh", 'з' => "z",
                'и' => "i", 'й' => "i", 'к' => "k", 'л' => "l", 'м' => "m", 'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r",
                'с' => "s", 'т' => "t", 'у' => "u", 'ф' => "f", 'х' => "h", 'ц' => "ts", 'ч' => "ch", 'ш' => "sh", 'щ' => "sch",
                'ъ' => "", 'ы' => "i", 'ь' => "", 'э' => "e", 'ю' => "yu", 'я' => "ya",
                _ => ch.ToString(),
            });
        }
        return sb.ToString();
    }

    // ---------- воспроизведение ----------

    public Task PlayAsync(IReadOnlyList<MusicTrack> queue, int index)
    {
        _queue = queue.ToList();
        return PlayIndexAsync(index);
    }

    /// <summary>«Включи радио» без названия: последняя станция, иначе первая из избранного, иначе самая популярная.</summary>
    public async Task PlayMixAsync()
    {
        List<MusicTrack> favs = App.Settings.RadioFavorites.Select(ToTrack).ToList();
        RadioStation? last = App.Settings.RadioLast;
        if (last != null && last.Url.Length > 0)
        {
            MusicTrack lastTrack = ToTrack(last);
            int i = favs.FindIndex(f => f.Id == lastTrack.Id);
            if (i >= 0) await PlayAsync(favs, i);
            else await PlayAsync(new[] { lastTrack }.Concat(favs).ToList(), 0);
            return;
        }
        if (favs.Count > 0)
        {
            await PlayAsync(favs, 0);
            return;
        }
        IReadOnlyList<MusicTrack> top = await TopAsync(CancellationToken.None);
        if (top.Count == 0) throw new MusicException("Radio Browser не вернул станций.");
        await PlayAsync(top, 0);
    }

    private async Task PlayIndexAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _index = index;
        MusicTrack track = _queue[index];
        if (!Uri.TryCreate(track.Extra, UriKind.Absolute, out Uri? uri))
            throw new MusicException($"У станции «{track.Title}» нет ссылки на эфир.");
        _current = track;
        _onAir = "";
        _songCover = null;
        _stationLogo = null;
        _logoBroken = false;
        _coverCts?.Cancel();
        _icyFailures = 0;
        _icyUnsupported = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase); // в HLS названий ICY нет
        _icyRetryAt = _icyUnsupported ? DateTime.MaxValue : DateTime.UtcNow;
        _player.Play(uri, track with { Artist = "Радио" });
        App.Settings.RadioLast = ToStation(track);
        App.Settings.Save();
        StateChanged?.Invoke();
        _ = CountClickAsync(track.Id);
        _ = LoadStationLogoAsync(track);
        _icyTimer.Start();
        await RefreshOnAirAsync();
    }

    public void PlayPause() => _player.PlayPause();

    public void Next()
    {
        if (_queue.Count < 2) return;
        Run(PlayIndexAsync((_index + 1) % _queue.Count));
    }

    public void Previous()
    {
        if (_queue.Count < 2) return;
        Run(PlayIndexAsync((_index - 1 + _queue.Count) % _queue.Count));
    }

    public void Seek(TimeSpan position) { } // эфир не перематывается

    public void Stop()
    {
        _icyTimer.Stop();
        _icyCts?.Cancel();
        _coverCts?.Cancel();
        _index = -1;
        _current = null;
        _onAir = "";
        _songCover = null;
        _stationLogo = null;
        _player.Stop();
    }

    public Task PollAsync() => Task.CompletedTask; // состояние приходит событиями плеера и таймером ICY

    public void Dispose()
    {
        _icyTimer.Stop();
        _icyCts?.Cancel();
        _player.Dispose();
    }

    private void Run(Task task) => task.ContinueWith(t =>
    {
        Exception? inner = t.Exception?.InnerException;
        if (inner is not MusicException) App.LogError(t.Exception!);
        Error?.Invoke(inner is MusicException me ? me.Message : "Радио: " + inner?.Message);
    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());

    // ---------- что в эфире (ICY) ----------

    private async Task RefreshOnAirAsync()
    {
        if (_current == null || !_player.IsPlaying) return;
        MusicTrack station = _current;
        // поток названий не даёт (HLS или сервер без ICY) — у станций AMG песню знает amgradio.ru
        if (_icyUnsupported && DateTime.UtcNow < _icyRetryAt)
        {
            await RefreshFromAmgAsync(station);
            return;
        }
        _icyCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        _icyCts = cts;
        string? title;
        try
        {
            title = await ReadIcyTitleAsync(station.Extra!, cts.Token);
        }
        catch (Exception)
        {
            // сеть моргнула или запрос перебили — спросим в следующий раз; три сбоя подряд — пауза на пару минут
            if (_current == station && !cts.IsCancellationRequested && ++_icyFailures >= 3) PauseIcy();
            if (_current == station) await RefreshFromAmgAsync(station);
            return;
        }
        if (_current != station) return;
        _icyFailures = 0;
        if (title == null)
        {
            // сейчас без метаданных: не сдаёмся навсегда (многие серверы включают их не сразу), а спросим позже
            PauseIcy();
            await RefreshFromAmgAsync(station);
            return;
        }
        _icyUnsupported = false;
        SetOnAir(station, title);
    }

    private void PauseIcy()
    {
        _icyUnsupported = true;
        _icyRetryAt = DateTime.UtcNow.AddMinutes(2);
    }

    private async Task RefreshFromAmgAsync(MusicTrack station)
    {
        if (!RadioCovers.LooksLikeAmg(station.Extra)) return;
        string? track = null;
        try { track = await RadioCovers.AmgTrackAsync(Http, station.Extra!, station.Title, CancellationToken.None); }
        catch (Exception) { }
        if (track != null && _current == station) SetOnAir(station, track);
    }

    private void SetOnAir(MusicTrack station, string title)
    {
        if (title == _onAir || _current != station) return;
        _onAir = title;
        _songCover = null;
        UpdateSystemDisplay();
        StateChanged?.Invoke();
        if (title.Length > 0) _ = LoadSongCoverAsync(station, title);
    }

    // ---------- подпись в Windows и обложки (как в Wikar Radio) ----------

    /// <summary>Песня — заголовок, исполнитель — подпись, станция — «альбом»; без песни — просто станция.</summary>
    private void UpdateSystemDisplay()
    {
        if (_current == null) return;
        (string artist, string title) = SplitOnAir(_onAir);
        if (title.Length > 0)
            _player.UpdateDisplay(title, artist.Length > 0 ? artist : _current.Title, artist.Length > 0 ? _current.Title : "", _songCover ?? StationCover);
        else
            _player.UpdateDisplay(_current.Title, "Радио", "", StationCover);
    }

    /// <summary>
    /// Логотип из каталога часто битый (ссылка умерла, SVG, вместо картинки страница сайта). Проверяем его и,
    /// если он не годится, берём логотип с amgradio.ru (станции AMG).
    /// </summary>
    private async Task LoadStationLogoAsync(MusicTrack station)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(station.CoverUrl) && await IsGoodImageAsync(station.CoverUrl!)) return;
            if (_current != station) return;
            _logoBroken = !string.IsNullOrWhiteSpace(station.CoverUrl);
            string? logo = RadioCovers.LooksLikeAmg(station.Extra)
                ? await RadioCovers.AmgLogoAsync(Http, station.Extra!, station.Title, CancellationToken.None)
                : null;
            if (_current != station) return;
            _stationLogo = logo;
            UpdateSystemDisplay();
            StateChanged?.Invoke();
        }
        catch (Exception) { /* без логотипа */ }
    }

    private static async Task<bool> IsGoodImageAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using HttpResponseMessage res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode) return false;
            byte[] head = new byte[16];
            await using Stream s = await res.Content.ReadAsStreamAsync(cts.Token);
            int read = 0;
            while (read < head.Length)
            {
                int n = await s.ReadAsync(head.AsMemory(read), cts.Token);
                if (n == 0) break;
                read += n;
            }
            return RadioCovers.IsRaster(head[..read]);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Список AMG может отставать от эфира на несколько секунд — пробуем до пяти раз.</summary>
    private async Task LoadSongCoverAsync(MusicTrack station, string onAir)
    {
        _coverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _coverCts = cts;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (attempt > 0) await Task.Delay(TimeSpan.FromSeconds(6), cts.Token);
                string? cover = await RadioCovers.CoverAsync(Http, station.Extra ?? "", station.Title, onAir, cts.Token);
                if (cts.IsCancellationRequested || _current != station || _onAir != onAir) return;
                if (cover == null) continue;
                _songCover = cover;
                UpdateSystemDisplay();
                StateChanged?.Invoke();
                return;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>
    /// Подключиться с «Icy-MetaData: 1», пропустить icy-metaint байт звука и прочитать блок «StreamTitle='…';».
    /// Свой маленький HTTP/1.0-клиент: старые SHOUTcast отвечают «ICY 200 OK», и HttpClient такой ответ не принимает.
    /// Понимает переадресации и ссылки-плейлисты (.m3u/.pls). null — станция метаданные не отдаёт; "" — отдаёт,
    /// но сейчас без названия (реклама, заставка).
    /// </summary>
    private static async Task<string?> ReadIcyTitleAsync(string url, CancellationToken ct, int depth = 0)
    {
        if (depth > 4 || !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) return null;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(uri.Host, uri.Port, ct).ConfigureAwait(false);
        Stream net = tcp.GetStream();
        if (uri.Scheme == "https")
        {
            var ssl = new SslStream(net, false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = uri.IdnHost }, ct).ConfigureAwait(false);
            net = ssl;
        }
        await using Stream stream = net;
        string request = $"GET {uri.PathAndQuery} HTTP/1.0\r\nHost: {uri.Authority}\r\nUser-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) WikarIsland/{UpdateService.Current}\r\n" +
                         "Accept: */*\r\nIcy-MetaData: 1\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct).ConfigureAwait(false);

        var reader = new IcyReader(stream);
        string? head = await reader.ReadHeadAsync(ct).ConfigureAwait(false);
        if (head == null) return null;
        string[] lines = head.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Match status = Regex.Match(lines.FirstOrDefault() ?? "", @"^(?:ICY|HTTP/\d(?:\.\d)?)\s+(\d{3})", RegexOptions.IgnoreCase);
        if (!status.Success) return null;
        int code = int.Parse(status.Groups[1].Value);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        if (code is 301 or 302 or 303 or 307 or 308 && headers.TryGetValue("Location", out string? location)
            && Uri.TryCreate(uri, location, out Uri? next))
            return await ReadIcyTitleAsync(next.ToString(), ct, depth + 1).ConfigureAwait(false);
        if (code != 200) return null;

        if (!headers.TryGetValue("icy-metaint", out string? metaint) || !int.TryParse(metaint, out int interval) || interval <= 0 || interval > 1_000_000)
        {
            // ссылка на плейлист: берём первый поток из него
            string type = headers.TryGetValue("Content-Type", out string? ctype) ? ctype.ToLowerInvariant() : "";
            string path = uri.AbsolutePath.ToLowerInvariant();
            if (type.Contains("mpegurl") || type.Contains("scpls") || path.EndsWith(".m3u") || path.EndsWith(".pls"))
            {
                string body = Encoding.UTF8.GetString(await reader.ReadUpToAsync(64 * 1024, ct).ConfigureAwait(false));
                Match inner = Regex.Match(body, @"(?im)^(?:File\d+=)?\s*(https?://\S+)");
                if (inner.Success) return await ReadIcyTitleAsync(inner.Groups[1].Value, ct, depth + 1).ConfigureAwait(false);
            }
            return null;
        }

        // первый блок метаданных бывает пустым (длина 0) — смотрим до трёх блоков
        for (int block = 0; block < 3; block++)
        {
            if (!await reader.SkipAsync(interval, ct).ConfigureAwait(false)) return null;
            int len = await reader.ReadByteAsync(ct).ConfigureAwait(false);
            if (len < 0) return null;
            if (len == 0) continue;
            byte[] meta = await reader.ReadExactAsync(len * 16, ct).ConfigureAwait(false);
            string text = Decode(meta).TrimEnd('\0');
            Match m = Regex.Match(text, @"StreamTitle='(.*?)';", RegexOptions.Singleline);
            if (!m.Success) return "";
            string title = m.Groups[1].Value.Trim();
            // заглушки вместо названия
            if (title is "-" or " - " || Regex.IsMatch(title, @"^(?:unknown|n/?a|\-+)$", RegexOptions.IgnoreCase)) return "";
            return title;
        }
        return "";
    }

    /// <summary>Чтение ответа: сначала то, что пришло вместе с заголовками, потом из сети.</summary>
    private sealed class IcyReader
    {
        private readonly Stream _stream;
        private readonly byte[] _buf = new byte[16 * 1024];
        private int _pos, _len;

        public IcyReader(Stream stream) => _stream = stream;

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            if (_pos < _len) return true;
            _len = await _stream.ReadAsync(_buf, ct).ConfigureAwait(false);
            _pos = 0;
            return _len > 0;
        }

        /// <summary>Заголовки до пустой строки (не больше 16 КБ).</summary>
        public async Task<string?> ReadHeadAsync(CancellationToken ct)
        {
            var head = new StringBuilder();
            while (head.Length < 16 * 1024)
            {
                if (!await FillAsync(ct).ConfigureAwait(false)) return null;
                char c = (char)_buf[_pos++];
                if (c == '\r') continue;
                head.Append(c);
                if (head.Length >= 2 && head[^1] == '\n' && head[^2] == '\n') return head.ToString();
            }
            return null;
        }

        public async Task<int> ReadByteAsync(CancellationToken ct) =>
            await FillAsync(ct).ConfigureAwait(false) ? _buf[_pos++] : -1;

        public async Task<bool> SkipAsync(int count, CancellationToken ct)
        {
            while (count > 0)
            {
                if (!await FillAsync(ct).ConfigureAwait(false)) return false;
                int n = Math.Min(count, _len - _pos);
                _pos += n;
                count -= n;
            }
            return true;
        }

        public async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            int done = 0;
            while (done < count && await FillAsync(ct).ConfigureAwait(false))
            {
                int n = Math.Min(count - done, _len - _pos);
                Array.Copy(_buf, _pos, result, done, n);
                _pos += n;
                done += n;
            }
            return done == count ? result : result[..done];
        }

        public async Task<byte[]> ReadUpToAsync(int max, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            while (ms.Length < max && await FillAsync(ct).ConfigureAwait(false))
            {
                int n = (int)Math.Min(max - ms.Length, _len - _pos);
                ms.Write(_buf, _pos, n);
                _pos += n;
            }
            return ms.ToArray();
        }
    }

    /// <summary>Русские станции часто шлют названия в Windows-1251, остальные — в UTF-8.</summary>
    private static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    /// <summary>«Кино - Группа крови» → (Кино, Группа крови); без дефиса — всё название.</summary>
    private static (string artist, string title) SplitOnAir(string onAir)
    {
        if (onAir.Length == 0) return ("", "");
        int i = onAir.IndexOf(" - ", StringComparison.Ordinal);
        if (i <= 0 || i >= onAir.Length - 3) return ("", onAir);
        return (onAir[..i].Trim(), onAir[(i + 3)..].Trim());
    }

    // ---------- Radio Browser API ----------

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Radio Browser просит представляться
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", $"WikarIsland/{UpdateService.Current}");
        return http;
    }

    /// <summary>Серверы Radio Browser: all.api.radio-browser.info указывает на все зеркала (так советует их документация).</summary>
    private static async Task<List<string>> ServersAsync()
    {
        if (_servers != null) return _servers;
        var list = new List<string>();
        try
        {
            IPAddress[] ips = await Dns.GetHostAddressesAsync("all.api.radio-browser.info");
            foreach (IPAddress ip in ips)
            {
                try
                {
                    IPHostEntry e = await Dns.GetHostEntryAsync(ip);
                    if (e.HostName.EndsWith(".radio-browser.info", StringComparison.OrdinalIgnoreCase) && !list.Contains(e.HostName))
                        list.Add(e.HostName);
                }
                catch
                {
                    // без обратной записи — пропускаем
                }
            }
        }
        catch
        {
            // DNS не ответил — возьмём известные зеркала
        }
        foreach (string s in FallbackServers)
            if (!list.Contains(s)) list.Add(s);
        _servers = list.OrderBy(_ => Random.Shared.Next()).ToList();
        return _servers;
    }

    private static async Task<JsonArray> ApiAsync(string path, CancellationToken ct)
    {
        List<string> servers = await ServersAsync();
        string[] order;
        lock (servers) order = servers.ToArray();
        foreach (string server in order)
        {
            try
            {
                string body = await Http.GetStringAsync($"https://{server}{path}", ct);
                if (JsonSafe.Parse(body) is JsonArray arr)
                {
                    // рабочее зеркало — первым в следующий раз
                    lock (servers)
                    {
                        if (servers[0] != server)
                        {
                            servers.Remove(server);
                            servers.Insert(0, server);
                        }
                    }
                    return arr;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // зеркало не ответило — пробуем следующее
            }
        }
        throw new MusicException("Каталог радиостанций (radio-browser.info) сейчас недоступен.");
    }

    /// <summary>Сообщить каталогу, что станцию слушают (так он считает популярность). Ошибки не важны.</summary>
    private static async Task CountClickAsync(string id)
    {
        try
        {
            List<string> servers = await ServersAsync();
            string server;
            lock (servers) server = servers[0];
            using HttpResponseMessage _ = await Http.GetAsync($"https://{server}/json/url/{Uri.EscapeDataString(id)}");
        }
        catch
        {
            // не страшно
        }
    }

    private static List<MusicTrack> Parse(JsonArray? items)
    {
        var list = new List<MusicTrack>();
        var seen = new HashSet<string>();
        if (items == null) return list;
        foreach (JsonNode? n in items)
        {
            MusicTrack? t = ParseStation(n);
            if (t != null && seen.Add(t.Id)) list.Add(t);
        }
        return list;
    }

    private static MusicTrack? ParseStation(JsonNode? n)
    {
        if (n == null) return null;
        string id = n["stationuuid"]?.ToString() ?? "";
        string name = Regex.Replace(n["name"]?.ToString() ?? "", @"\s+", " ").Trim();
        string url = n["url_resolved"]?.ToString() is { Length: > 0 } r ? r : n["url"]?.ToString() ?? "";
        if (id.Length == 0 || name.Length == 0 || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        var info = new List<string>();
        string country = n["countrycode"]?.ToString() ?? "";
        if (country.Length > 0) info.Add(CountryName(country));
        string tags = string.Join(", ", (n["tags"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        if (tags.Length > 0) info.Add(tags);
        if (n["bitrate"] is JsonValue b && b.TryGetValue(out int kbps) && kbps > 0) info.Add($"{kbps} кбит/с");
        string favicon = n["favicon"]?.ToString() ?? "";

        return ToTrack(new RadioStation
        {
            Id = id,
            Name = name,
            Url = url,
            Favicon = favicon.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? favicon : "",
            Info = string.Join(" · ", info),
        });
    }

    private static string CountryName(string code) => code.ToUpperInvariant() switch
    {
        "RU" => "Россия", "DE" => "Германия", "UA" => "Украина", "BY" => "Беларусь", "KZ" => "Казахстан", "US" => "США",
        "GB" => "Великобритания", "FR" => "Франция", "IT" => "Италия", "ES" => "Испания", "NL" => "Нидерланды", "PL" => "Польша",
        "AT" => "Австрия", "CH" => "Швейцария", "LV" => "Латвия", "LT" => "Литва", "EE" => "Эстония", "CZ" => "Чехия",
        "TR" => "Турция", "CA" => "Канада", "JP" => "Япония", "GR" => "Греция", "SE" => "Швеция", "NO" => "Норвегия",
        string other => other,
    };

    private static MusicTrack ToTrack(RadioStation s) =>
        new("radio", s.Id, s.Name, s.Info, s.Favicon.Length > 0 ? s.Favicon : null, TimeSpan.Zero) { Extra = s.Url };

    private static RadioStation ToStation(MusicTrack t) =>
        new() { Id = t.Id, Name = t.Title, Url = t.Extra ?? "", Favicon = t.CoverUrl ?? "", Info = t.Artist };
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
    private int _icyFailures;
    private string? _songCover;   // обложка песни в эфире (AMG / iTunes)
    private string? _stationLogo; // логотип с amgradio.ru, если в каталоге его нет
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

    private string? StationCover => string.IsNullOrWhiteSpace(_current?.CoverUrl) ? _stationLogo : _current!.CoverUrl;

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
        _coverCts?.Cancel();
        _icyFailures = 0;
        _icyUnsupported = uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase); // в HLS названий ICY нет
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
        if (_current == null || _icyUnsupported || !_player.IsPlaying) return;
        _icyCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _icyCts = cts;
        MusicTrack station = _current;
        string? title;
        try
        {
            title = await ReadIcyTitleAsync(station.Extra!, cts.Token);
        }
        catch (Exception)
        {
            // сеть моргнула или запрос перебили — спросим в следующий раз; три сбоя подряд — станция названий не даст
            if (_current == station && !cts.IsCancellationRequested && ++_icyFailures >= 3) _icyUnsupported = true;
            return;
        }
        if (_current != station) return;
        _icyFailures = 0;
        if (title == null)
        {
            _icyUnsupported = true; // у этой станции названий нет — больше не спрашиваем
            return;
        }
        if (title == _onAir) return;
        _onAir = title;
        _songCover = null;
        UpdateSystemDisplay();
        StateChanged?.Invoke();
        _ = LoadSongCoverAsync(station, title);
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

    private async Task LoadStationLogoAsync(MusicTrack station)
    {
        if (!string.IsNullOrWhiteSpace(station.CoverUrl) || !RadioCovers.LooksLikeAmg(station.Extra)) return;
        try
        {
            string? logo = await RadioCovers.AmgLogoAsync(Http, station.Extra!, station.Title, CancellationToken.None);
            if (logo == null || _current != station) return;
            _stationLogo = logo;
            UpdateSystemDisplay();
            StateChanged?.Invoke();
        }
        catch (Exception) { /* без логотипа */ }
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
    /// null — станция метаданные не отдаёт; "" — отдаёт, но сейчас без названия (реклама, заставка).
    /// </summary>
    private static async Task<string?> ReadIcyTitleAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        using HttpResponseMessage res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;
        string? metaint = res.Headers.TryGetValues("icy-metaint", out IEnumerable<string>? v) ? v.FirstOrDefault()
            : res.Content.Headers.TryGetValues("icy-metaint", out IEnumerable<string>? cv) ? cv.FirstOrDefault() : null;
        if (!int.TryParse(metaint, out int interval) || interval <= 0 || interval > 1_000_000) return null;

        await using Stream stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        byte[] skip = new byte[8192];
        int left = interval;
        while (left > 0)
        {
            int n = await stream.ReadAsync(skip.AsMemory(0, Math.Min(left, skip.Length)), ct).ConfigureAwait(false);
            if (n == 0) return null;
            left -= n;
        }
        byte[] one = new byte[1];
        if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
        int len = one[0];
        if (len == 0) return "";
        byte[] meta = new byte[len * 16];
        int read = 0;
        while (read < meta.Length)
        {
            int n = await stream.ReadAsync(meta.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) break;
            read += n;
        }
        string text = Decode(meta.AsSpan(0, read).ToArray()).TrimEnd('\0');
        Match m = Regex.Match(text, @"StreamTitle='(.*?)';", RegexOptions.Singleline);
        if (!m.Success) return "";
        string title = m.Groups[1].Value.Trim();
        // заглушки вместо названия
        if (title is "-" or " - " || Regex.IsMatch(title, @"^(?:unknown|n/?a|\-+)$", RegexOptions.IgnoreCase)) return "";
        return title;
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

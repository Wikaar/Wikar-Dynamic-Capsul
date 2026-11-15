using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Яндекс Музыка через её API (тот же, что у мобильного приложения; неофициальный — так работают
/// библиотеки yandex-music-api). Вход — OAuth-токен Яндекса, звук играет наш <see cref="LocalPlayer"/>.
/// Полные треки — с подпиской Плюс, без неё сервис отдаёт только 30-секундные отрывки.
/// </summary>
public sealed class YandexMusicProvider : IMusicProvider
{
    private const string Api = "https://api.music.yandex.net";

    // client_id официального приложения Яндекс Музыки — так получают токен все сторонние клиенты
    private const string ClientId = "23cabbbdc6cd418abb4b39c32c41195d";
    private const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";
    private const string Wave = "user:onyourwave";

    private static readonly HttpClient Http = MusicHttp.Create();
    private readonly LocalPlayer _player = new();
    private List<MusicTrack> _queue = new();
    private int _index = -1;
    private MusicTrack? _current;
    private bool _wave;
    private string? _batchId;
    private string _uid = "";
    private int _failures;
    private bool _loadingMore;

    public string Key => "yandex";
    public string Name => "Яндекс Музыка";
    public string MixName => "Моя волна";
    public string? FavoritesName => "Мне нравится";
    public bool IsLoggedIn => Token.Length > 0;
    public string Account { get; private set; } = "";

    public event Action? StateChanged;
    public event Action<string>? Error;

    private static string Token => App.Settings.YandexToken;

    public YandexMusicProvider()
    {
        _player.StateChanged += () => StateChanged?.Invoke();
        _player.Ended += () => Run(NextCoreAsync());
        _player.NextRequested += Next;
        _player.PreviousRequested += Previous;
        _player.Failed += message =>
        {
            Error?.Invoke($"Трек не воспроизводится: {message}");
            // недоступный трек пропускаем, но не по кругу
            if (++_failures <= 3) Run(NextCoreAsync());
        };
    }

    public MusicState? State => _current == null || !_player.HasSource ? null : new MusicState
    {
        Title = _current.Title,
        Artist = _current.Artist,
        CoverUrl = _current.CoverUrl,
        IsPlaying = _player.IsPlaying,
        Position = _player.Position,
        Duration = _player.Duration > TimeSpan.Zero ? _player.Duration : _current.Duration,
        PositionUpdated = DateTimeOffset.Now,
    };

    // ---------- вход ----------

    public async Task<string?> LoginAsync()
    {
        string? url = await LoginWindow.CaptureAsync(
            $"https://oauth.yandex.ru/authorize?response_type=token&client_id={ClientId}",
            u => u.Contains("access_token=", StringComparison.Ordinal), "Вход в Яндекс Музыку");
        if (url == null) return "Вход отменён.";
        string token = Regex.Match(url, "access_token=([^&#]+)").Groups[1].Value;
        if (token.Length == 0) return "Яндекс не вернул токен.";
        App.Settings.YandexToken = Uri.UnescapeDataString(token);
        App.Settings.Save();
        await RefreshAccountAsync();
        return null;
    }

    public void Logout()
    {
        Stop();
        App.Settings.YandexToken = "";
        App.Settings.Save();
        Account = "";
        _uid = "";
    }

    public async Task RefreshAccountAsync()
    {
        if (!IsLoggedIn) return;
        JsonNode? r = await GetAsync("/account/status", CancellationToken.None);
        JsonNode? account = r?["account"];
        _uid = account?["uid"]?.ToString() ?? "";
        if (_uid.Length == 0) throw new MusicException("Яндекс не принял вход — войди заново.");
        string name = account?["displayName"]?.ToString() ?? account?["login"]?.ToString() ?? "аккаунт Яндекса";
        bool plus = r?["plus"]?["hasPlus"] is JsonValue p && p.TryGetValue(out bool has) && has;
        Account = plus ? name : name + " · без Плюса (только отрывки)";
    }

    // ---------- поиск и подборки ----------

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken ct)
    {
        JsonNode? r = await GetAsync($"/search?text={Uri.EscapeDataString(query)}&type=track&page=0&nocorrect=false", ct);
        return Parse(r?["tracks"]?["results"] as JsonArray);
    }

    public async Task<IReadOnlyList<MusicTrack>> FavoritesAsync(CancellationToken ct)
    {
        if (_uid.Length == 0) await RefreshAccountAsync();
        JsonNode? r = await GetAsync($"/users/{_uid}/likes/tracks", ct);
        List<string> ids = ((r?["library"]?["tracks"] as JsonArray) ?? new JsonArray())
            .Select(t => t?["id"]?.ToString() + (t?["albumId"] != null ? ":" + t["albumId"] : ""))
            .Where(id => id.Length > 0 && !id.StartsWith(':'))
            .Take(200).ToList();
        if (ids.Count == 0) return Array.Empty<MusicTrack>();
        JsonNode? tracks = await PostFormAsync("/tracks", new Dictionary<string, string> { ["track-ids"] = string.Join(",", ids) }, ct);
        return Parse(tracks as JsonArray);
    }

    // ---------- воспроизведение ----------

    public Task PlayAsync(IReadOnlyList<MusicTrack> queue, int index)
    {
        _wave = false;
        _queue = queue.ToList();
        _failures = 0;
        return PlayIndexAsync(index);
    }

    public async Task PlayMixAsync()
    {
        List<MusicTrack> batch = await WaveBatchAsync(null);
        if (batch.Count == 0) throw new MusicException("«Моя волна» не вернула треков.");
        _wave = true;
        _queue = batch;
        _failures = 0;
        _ = FeedbackAsync(new { type = "radioStarted", from = "wikar-island", timestamp = Now() });
        await PlayIndexAsync(0);
    }

    private async Task PlayIndexAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        _index = index;
        MusicTrack track = _queue[index];
        Uri uri = await StreamUrlAsync(track);
        if (_index != index) return; // пока ждали ссылку, переключили дальше
        _current = track;
        _player.Play(uri, track);
        StateChanged?.Invoke();
        if (_wave)
        {
            _ = FeedbackAsync(new { type = "trackStarted", trackId = TrackKey(track), timestamp = Now() });
            if (_index >= _queue.Count - 2) _ = LoadMoreWaveAsync();
        }
    }

    private async Task LoadMoreWaveAsync()
    {
        if (_loadingMore) return;
        _loadingMore = true;
        try
        {
            string? last = _queue.Count > 0 ? TrackKey(_queue[^1]) : null;
            List<MusicTrack> more = await WaveBatchAsync(last);
            _queue.AddRange(more.Where(m => _queue.All(q => q.Id != m.Id)));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private async Task NextCoreAsync()
    {
        if (_index + 1 >= _queue.Count && _wave) await LoadMoreWaveAsync();
        if (_index + 1 < _queue.Count) await PlayIndexAsync(_index + 1);
        else _player.Stop();
    }

    public void PlayPause() => _player.PlayPause();

    public void Next()
    {
        if (_wave && _current != null)
            _ = FeedbackAsync(new { type = "skip", trackId = TrackKey(_current), totalPlayedSeconds = _player.Position.TotalSeconds, timestamp = Now() });
        _failures = 0;
        Run(NextCoreAsync());
    }

    public void Previous()
    {
        if (_player.Position > TimeSpan.FromSeconds(5) || _index <= 0) _player.Seek(TimeSpan.Zero);
        else Run(PlayIndexAsync(_index - 1));
    }

    public void Seek(TimeSpan position) => _player.Seek(position);

    public void Stop()
    {
        _index = -1;
        _current = null;
        _player.Stop();
    }

    public Task PollAsync() => Task.CompletedTask; // состояние приходит событиями плеера

    public void Dispose() => _player.Dispose();

    private void Run(Task task) => task.ContinueWith(t =>
    {
        string message = t.Exception?.InnerException is MusicException me ? me.Message : "Яндекс Музыка: " + t.Exception?.InnerException?.Message;
        if (t.Exception?.InnerException is not MusicException) App.LogError(t.Exception!);
        Error?.Invoke(message);
    }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());

    // ---------- ссылка на поток ----------

    /// <summary>download-info → лучший mp3 → подписанная ссылка на файл (как в yandex-music-api).</summary>
    private async Task<Uri> StreamUrlAsync(MusicTrack track)
    {
        JsonArray infos = await GetAsync($"/tracks/{TrackKey(track)}/download-info", CancellationToken.None) as JsonArray ?? new JsonArray();
        JsonNode? best = infos
            .Where(i => i != null && i["codec"]?.ToString() == "mp3")
            .OrderBy(i => i!["preview"] is JsonValue p && p.TryGetValue(out bool pv) && pv ? 1 : 0)
            .ThenByDescending(i => (int?)i!["bitrateInKbps"] ?? 0)
            .FirstOrDefault();
        string? infoUrl = best?["downloadInfoUrl"]?.ToString();
        if (infoUrl == null) throw new MusicException($"«{track.Title}» недоступен для прослушивания.");

        using var req = new HttpRequestMessage(HttpMethod.Get, infoUrl);
        Authorize(req);
        using HttpResponseMessage resp = await Http.SendAsync(req);
        string body = await resp.Content.ReadAsStringAsync();

        string host, path, ts, s;
        if (body.TrimStart().StartsWith('{'))
        {
            JsonNode? j = JsonNode.Parse(body);
            host = j?["host"]?.ToString() ?? "";
            path = j?["path"]?.ToString() ?? "";
            ts = j?["ts"]?.ToString() ?? "";
            s = j?["s"]?.ToString() ?? "";
        }
        else
        {
            host = XmlTag(body, "host");
            path = XmlTag(body, "path");
            ts = XmlTag(body, "ts");
            s = XmlTag(body, "s");
        }
        if (host.Length == 0 || path.Length < 2) throw new MusicException("Яндекс не дал ссылку на трек.");
        string sign = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(SignSalt + path[1..] + s))).ToLowerInvariant();
        return new Uri($"https://{host}/get-mp3/{sign}/{ts}{path}");
    }

    private static string XmlTag(string xml, string name) =>
        Regex.Match(xml, $"<{name}>([^<]*)</{name}>").Groups[1].Value;

    // ---------- «Моя волна» ----------

    private async Task<List<MusicTrack>> WaveBatchAsync(string? lastTrack)
    {
        JsonNode? r = await GetAsync($"/rotor/station/{Wave}/tracks?settings2=true" + (lastTrack != null ? "&queue=" + lastTrack : ""), CancellationToken.None);
        _batchId = r?["batchId"]?.ToString();
        return Parse((r?["sequence"] as JsonArray)?.Select(x => x?["track"]));
    }

    /// <summary>Отзывы для волны (начал, пропустил) — чтобы она подстраивалась. Ошибки не важны.</summary>
    private async Task FeedbackAsync(object body)
    {
        try
        {
            string url = $"{Api}/rotor/station/{Wave}/feedback" + (_batchId != null ? "?batch-id=" + Uri.EscapeDataString(_batchId) : "");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            Authorize(req);
            using HttpResponseMessage _ = await Http.SendAsync(req);
        }
        catch
        {
            // не страшно
        }
    }

    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string TrackKey(MusicTrack t) => t.Extra is { Length: > 0 } album ? $"{t.Id}:{album}" : t.Id;

    // ---------- HTTP ----------

    private static void Authorize(HttpRequestMessage req)
    {
        req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + Token);
        req.Headers.TryAddWithoutValidation("X-Yandex-Music-Client", "YandexMusicAndroid/24023621");
        req.Headers.TryAddWithoutValidation("User-Agent", "Yandex-Music-API");
    }

    private static async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Api + path);
        return await SendAsync(req, ct);
    }

    private static async Task<JsonNode?> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Api + path) { Content = new FormUrlEncodedContent(form) };
        return await SendAsync(req, ct);
    }

    private static async Task<JsonNode?> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        if (Token.Length == 0) throw new MusicException("Сначала войди в Яндекс Музыку.");
        Authorize(req);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MusicException("Нет связи с Яндекс Музыкой: " + ex.Message);
        }
        using (resp)
        {
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new MusicException("Вход в Яндекс Музыку устарел — войди заново.");
            if (!resp.IsSuccessStatusCode) throw new MusicException($"Яндекс Музыка ответила ошибкой {(int)resp.StatusCode}.");
            return JsonNode.Parse(body)?["result"];
        }
    }

    // ---------- разбор треков ----------

    private static List<MusicTrack> Parse(IEnumerable<JsonNode?>? items)
    {
        var list = new List<MusicTrack>();
        if (items == null) return list;
        foreach (JsonNode? t in items)
        {
            MusicTrack? track = ParseTrack(t);
            if (track != null) list.Add(track);
        }
        return list;
    }

    private static MusicTrack? ParseTrack(JsonNode? t)
    {
        if (t == null) return null;
        if (t["available"] is JsonValue av && av.TryGetValue(out bool available) && !available) return null;
        string id = t["id"]?.ToString() ?? "";
        if (id.Length == 0) return null;
        string title = t["title"]?.ToString() ?? "";
        string? version = t["version"]?.ToString();
        if (!string.IsNullOrWhiteSpace(version)) title += $" ({version})";
        string artist = string.Join(", ", ((t["artists"] as JsonArray) ?? new JsonArray())
            .Select(a => a?["name"]?.ToString() ?? "").Where(n => n.Length > 0));
        JsonNode? album = (t["albums"] as JsonArray)?.FirstOrDefault();
        string? cover = t["coverUri"]?.ToString() ?? album?["coverUri"]?.ToString();
        long ms = t["durationMs"] is JsonValue d && d.TryGetValue(out long v) ? v : 0;
        return new MusicTrack("yandex", id, title, artist,
            string.IsNullOrEmpty(cover) ? null : "https://" + cover.Replace("%%", "400x400"), TimeSpan.FromMilliseconds(ms))
        {
            Album = album?["title"]?.ToString() ?? "",
            Extra = album?["id"]?.ToString(),
        };
    }
}

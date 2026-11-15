using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Spotify через официальный Web API. Звук Spotify защищён DRM, поэтому играет не остров, а Spotify Connect —
/// приложение Spotify на этом ПК (или любое другое устройство); остров ищет, запускает и управляет.
/// Нужен свой Client ID (бесплатное приложение на developer.spotify.com), управление воспроизведением — только с Premium.
/// </summary>
public sealed class SpotifyProvider : IMusicProvider
{
    public const string RedirectUri = "http://127.0.0.1:43821/callback";
    private const string Api = "https://api.spotify.com/v1";
    private const string Scopes = "user-read-playback-state user-modify-playback-state user-read-currently-playing user-library-read user-read-private";

    private static readonly HttpClient Http = MusicHttp.Create();
    private string? _access;
    private DateTime _accessUntil;
    private MusicState? _state;
    private bool _active;
    private string? _deviceId;

    public string Key => "spotify";
    public string Name => "Spotify";
    public string MixName => "Перемешать любимые";
    public string? FavoritesName => "Любимые треки";
    public bool IsLoggedIn => App.Settings.SpotifyClientId.Length > 0 && App.Settings.SpotifyRefresh.Length > 0;
    public string Account { get; private set; } = "";
    public MusicState? State => _active ? _state : null;

    public event Action? StateChanged;
    public event Action<string>? Error;

    // ---------- вход (OAuth с PKCE, без секрета) ----------

    public async Task<string?> LoginAsync()
    {
        string clientId = App.Settings.SpotifyClientId;
        if (clientId.Length == 0) return "Сначала вставь Client ID своего приложения Spotify.";
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string url = "https://accounts.spotify.com/authorize?response_type=code" +
                     "&client_id=" + Uri.EscapeDataString(clientId) +
                     "&scope=" + Uri.EscapeDataString(Scopes) +
                     "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
                     "&code_challenge_method=S256&code_challenge=" + challenge;
        string? back = await LoginWindow.CaptureAsync(url, u => u.StartsWith(RedirectUri, StringComparison.OrdinalIgnoreCase), "Вход в Spotify");
        if (back == null) return "Вход отменён.";
        string error = Regex.Match(back, "[?&]error=([^&]+)").Groups[1].Value;
        if (error.Length > 0) return "Spotify отказал во входе: " + Uri.UnescapeDataString(error);
        string code = Regex.Match(back, "[?&]code=([^&]+)").Groups[1].Value;
        if (code.Length == 0) return "Spotify не вернул код входа.";

        await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = Uri.UnescapeDataString(code),
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        });
        await RefreshAccountAsync();
        return null;
    }

    public void Logout()
    {
        Stop();
        App.Settings.SpotifyRefresh = "";
        App.Settings.Save();
        _access = null;
        Account = "";
    }

    public async Task RefreshAccountAsync()
    {
        if (!IsLoggedIn) return;
        JsonNode? me = await SendAsync(HttpMethod.Get, "/me");
        string name = me?["display_name"]?.ToString() ?? me?["id"]?.ToString() ?? "аккаунт Spotify";
        Account = me?["product"]?.ToString() == "premium" ? name : name + " · без Premium (только поиск)";
    }

    private async Task TokenRequestAsync(Dictionary<string, string> form)
    {
        using HttpResponseMessage resp = await Http.PostAsync("https://accounts.spotify.com/api/token", new FormUrlEncodedContent(form));
        string body = await resp.Content.ReadAsStringAsync();
        JsonNode? j = JsonNode.Parse(body);
        if (!resp.IsSuccessStatusCode)
        {
            if (form["grant_type"] == "refresh_token")
            {
                App.Settings.SpotifyRefresh = "";
                App.Settings.Save();
            }
            throw new MusicException("Spotify не выдал доступ: " + (j?["error_description"]?.ToString() ?? j?["error"]?.ToString() ?? ((int)resp.StatusCode).ToString()));
        }
        _access = j?["access_token"]?.ToString();
        _accessUntil = DateTime.UtcNow.AddSeconds(((int?)j?["expires_in"] ?? 3600) - 60);
        if (j?["refresh_token"]?.ToString() is { Length: > 0 } refresh)
        {
            App.Settings.SpotifyRefresh = refresh;
            App.Settings.Save();
        }
    }

    private async Task<string> AccessAsync(bool force = false)
    {
        if (!force && _access != null && DateTime.UtcNow < _accessUntil) return _access;
        if (!IsLoggedIn) throw new MusicException("Сначала войди в Spotify.");
        await TokenRequestAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = App.Settings.SpotifyRefresh,
            ["client_id"] = App.Settings.SpotifyClientId,
        });
        return _access ?? throw new MusicException("Spotify не выдал доступ.");
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, Api + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAsync(force: attempt > 0));
            if (body != null) req.Content = JsonContent.Create(body);
            else if (method != HttpMethod.Get) req.Content = new StringContent(""); // Spotify требует Content-Length у PUT/POST

            HttpResponseMessage resp;
            try
            {
                resp = await Http.SendAsync(req, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new MusicException("Нет связи со Spotify: " + ex.Message);
            }
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                string text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);
                string reason = "";
                try
                {
                    JsonNode? err = JsonNode.Parse(text)?["error"];
                    reason = err?["reason"]?.ToString() ?? err?["message"]?.ToString() ?? "";
                }
                catch
                {
                    // не JSON
                }
                if (reason == "PREMIUM_REQUIRED" || (resp.StatusCode == HttpStatusCode.Forbidden && path.StartsWith("/me/player")))
                    throw new MusicException("Управлять воспроизведением Spotify разрешает только с Premium.");
                if (reason == "NO_ACTIVE_DEVICE") throw new MusicException("Нет активного устройства Spotify — открой приложение Spotify.");
                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    throw new MusicException("Spotify не пускает: добавь свой аккаунт в User Management приложения на developer.spotify.com.");
                throw new MusicException($"Spotify ответил ошибкой {(int)resp.StatusCode}{(reason.Length > 0 ? ": " + reason : "")}.");
            }
        }
    }

    // ---------- поиск и подборки ----------

    public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken ct)
    {
        JsonNode? r = await SendAsync(HttpMethod.Get, "/search?type=track&limit=30&q=" + Uri.EscapeDataString(query), ct: ct);
        return Parse(r?["tracks"]?["items"] as JsonArray);
    }

    public async Task<IReadOnlyList<MusicTrack>> FavoritesAsync(CancellationToken ct)
    {
        JsonNode? r = await SendAsync(HttpMethod.Get, "/me/tracks?limit=50", ct: ct);
        return Parse((r?["items"] as JsonArray)?.Select(i => i?["track"]));
    }

    private static List<MusicTrack> Parse(IEnumerable<JsonNode?>? items)
    {
        var list = new List<MusicTrack>();
        if (items == null) return list;
        foreach (JsonNode? t in items)
        {
            string? id = t?["id"]?.ToString();
            string? uri = t?["uri"]?.ToString();
            if (t == null || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(uri)) continue;
            string artist = string.Join(", ", ((t["artists"] as JsonArray) ?? new JsonArray()).Select(a => a?["name"]?.ToString() ?? "").Where(n => n.Length > 0));
            JsonArray? images = t["album"]?["images"] as JsonArray;
            string? cover = (images?.Count > 1 ? images[1] : images?.FirstOrDefault())?["url"]?.ToString();
            list.Add(new MusicTrack("spotify", id, t["name"]?.ToString() ?? "", artist, cover,
                TimeSpan.FromMilliseconds((long?)t["duration_ms"] ?? 0))
            {
                Album = t["album"]?["name"]?.ToString() ?? "",
                Extra = uri,
            });
        }
        return list;
    }

    // ---------- воспроизведение через Spotify Connect ----------

    /// <summary>Устройство для воспроизведения: активное, иначе этот ПК; если Spotify не запущен — запускаем и ждём.</summary>
    private async Task<string> DeviceAsync()
    {
        for (int i = 0; i < 16; i++)
        {
            JsonArray devices = (await SendAsync(HttpMethod.Get, "/me/player/devices"))?["devices"] as JsonArray ?? new JsonArray();
            JsonNode? pick = devices.FirstOrDefault(d => (bool?)d?["is_active"] == true)
                             ?? devices.FirstOrDefault(d => d?["name"]?.ToString()?.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) == true)
                             ?? devices.FirstOrDefault(d => d?["type"]?.ToString() == "Computer")
                             ?? devices.FirstOrDefault();
            if (pick?["id"]?.ToString() is { Length: > 0 } id) return id;
            if (i == 0) AppIndex.OpenUri("spotify:");
            await Task.Delay(1000);
        }
        throw new MusicException("Spotify не появился среди устройств — открой приложение Spotify и войди в тот же аккаунт.");
    }

    public async Task PlayAsync(IReadOnlyList<MusicTrack> queue, int index)
    {
        if (index < 0 || index >= queue.Count) return;
        List<string> uris = queue.Skip(index).Take(100).Select(t => t.Extra ?? "").Where(u => u.Length > 0).ToList();
        await StartAsync(uris);
    }

    public async Task PlayMixAsync()
    {
        IReadOnlyList<MusicTrack> liked = await FavoritesAsync(CancellationToken.None);
        if (liked.Count == 0) throw new MusicException("В «Любимых треках» Spotify пусто.");
        List<string> uris = liked.Select(t => t.Extra ?? "").Where(u => u.Length > 0).OrderBy(_ => Random.Shared.Next()).ToList();
        await StartAsync(uris);
    }

    private async Task StartAsync(List<string> uris)
    {
        _deviceId = await DeviceAsync();
        await SendAsync(HttpMethod.Put, "/me/player/play?device_id=" + Uri.EscapeDataString(_deviceId), new { uris });
        _active = true;
        await Task.Delay(700);
        await PollAsync();
    }

    public void PlayPause() => Command(_state?.IsPlaying == true ? (HttpMethod.Put, "/me/player/pause") : (HttpMethod.Put, "/me/player/play"));
    public void Next() => Command((HttpMethod.Post, "/me/player/next"));
    public void Previous() => Command((HttpMethod.Post, "/me/player/previous"));
    public void Seek(TimeSpan position) => Command((HttpMethod.Put, "/me/player/seek?position_ms=" + (long)position.TotalMilliseconds));

    public void Stop()
    {
        if (_active && _state?.IsPlaying == true) Command((HttpMethod.Put, "/me/player/pause"));
        _active = false;
        _state = null;
        StateChanged?.Invoke();
    }

    private async void Command((HttpMethod method, string path) cmd)
    {
        try
        {
            await SendAsync(cmd.method, cmd.path);
            await Task.Delay(400);
            await PollAsync();
        }
        catch (MusicException ex)
        {
            Error?.Invoke(ex.Message);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private DateTime _lastPoll;

    public async Task PollAsync()
    {
        if (!_active || !IsLoggedIn) return;
        // Spotify не любит частые запросы: не чаще раза в 1,5 секунды
        if (DateTime.Now - _lastPoll < TimeSpan.FromMilliseconds(1400)) return;
        _lastPoll = DateTime.Now;
        try
        {
            JsonNode? p = await SendAsync(HttpMethod.Get, "/me/player");
            JsonNode? item = p?["item"];
            MusicState? next = item == null ? null : new MusicState
            {
                Title = item["name"]?.ToString() ?? "",
                Artist = string.Join(", ", ((item["artists"] as JsonArray) ?? new JsonArray()).Select(a => a?["name"]?.ToString() ?? "").Where(n => n.Length > 0)),
                CoverUrl = ((item["album"]?["images"] as JsonArray)?.FirstOrDefault())?["url"]?.ToString(),
                IsPlaying = (bool?)p?["is_playing"] == true,
                Position = TimeSpan.FromMilliseconds((long?)p?["progress_ms"] ?? 0),
                Duration = TimeSpan.FromMilliseconds((long?)item["duration_ms"] ?? 0),
                PositionUpdated = DateTimeOffset.Now,
            };
            bool changed = next == null ? _state != null : !next.SameAs(_state) || Math.Abs((next.Position - Expected(_state, next)).TotalSeconds) > 3;
            _state = next;
            if (changed) StateChanged?.Invoke();
        }
        catch (MusicException)
        {
            // временная ошибка опроса — покажем при следующей команде
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static TimeSpan Expected(MusicState? before, MusicState now) =>
        before == null ? now.Position : before.Position + (before.IsPlaying ? now.PositionUpdated - before.PositionUpdated : TimeSpan.Zero);

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
    }
}

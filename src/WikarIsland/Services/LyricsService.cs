using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>Текст песни: строки с отметками времени (синхронный) или просто текст.</summary>
public sealed class Lyrics
{
    public List<(TimeSpan at, string line)> Synced { get; } = new();
    public string Plain { get; init; } = "";
    public bool IsSynced => Synced.Count > 0;

    /// <summary>Номер текущей строки для позиции трека (-1 — ещё до первой строки).</summary>
    public int IndexAt(TimeSpan pos)
    {
        int i = -1;
        for (int k = 0; k < Synced.Count; k++)
        {
            if (Synced[k].at <= pos) i = k;
            else break;
        }
        return i;
    }
}

/// <summary>
/// Тексты песен с LRCLIB (lrclib.net — открытая база, без ключа). Синхронные строки подсвечиваются по ходу трека.
/// Результат кэшируется на время работы программы, «не нашлось» тоже.
/// </summary>
public static class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ConcurrentDictionary<string, Task<Lyrics?>> Cache = new();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WikarIsland/1.0 (https://github.com/Wikaar/wikar-dynamicisland)");
        return c;
    }

    public static string Key(string title, string artist) => (Clean(artist) + " — " + Clean(title)).ToLowerInvariant();

    public static Task<Lyrics?> GetAsync(string title, string artist, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(title)) return Task.FromResult<Lyrics?>(null);
        // YouTube и браузеры: «Исполнитель - Название» в заголовке, исполнитель пустой или это канал
        Match dash = Regex.Match(title, @"^(.+?)\s+[-–—]\s+(.+)$");
        if (dash.Success && (string.IsNullOrWhiteSpace(artist) || artist.EndsWith("Topic") || title.StartsWith(artist, StringComparison.OrdinalIgnoreCase)))
        {
            artist = dash.Groups[1].Value;
            title = dash.Groups[2].Value;
        }
        return Cache.GetOrAdd(Key(title, artist), _ => FetchAsync(title, artist, duration));
    }

    private static async Task<Lyrics?> FetchAsync(string title, string artist, TimeSpan duration)
    {
        string t = Clean(title), a = Clean(artist);
        try
        {
            // точное совпадение по названию, исполнителю и длительности
            if (a.Length > 0)
            {
                string url = "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(t) + "&artist_name=" + Uri.EscapeDataString(a);
                if (duration > TimeSpan.FromSeconds(10)) url += "&duration=" + (int)Math.Round(duration.TotalSeconds);
                using HttpResponseMessage r = await Http.GetAsync(url);
                if (r.IsSuccessStatusCode)
                {
                    using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                    if (Parse(doc.RootElement) is { } exact) return exact;
                }
            }

            // поиск: берём ближайший по длительности, синхронный в приоритете
            string q = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(t) + (a.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(a) : "");
            using HttpResponseMessage s = await Http.GetAsync(q);
            if (!s.IsSuccessStatusCode) return null;
            using JsonDocument list = JsonDocument.Parse(await s.Content.ReadAsStringAsync());
            if (list.RootElement.ValueKind != JsonValueKind.Array) return null;
            JsonElement? best = null;
            double bestScore = double.MaxValue;
            foreach (JsonElement e in list.RootElement.EnumerateArray())
            {
                bool synced = e.TryGetProperty("syncedLyrics", out JsonElement sl) && sl.ValueKind == JsonValueKind.String;
                double d = e.TryGetProperty("duration", out JsonElement de) && de.TryGetDouble(out double dv) ? dv : 0;
                double score = (duration > TimeSpan.Zero && d > 0 ? Math.Abs(d - duration.TotalSeconds) : 30) + (synced ? 0 : 20);
                if (score < bestScore) { bestScore = score; best = e; }
            }
            return best is { } b && bestScore < 60 ? Parse(b) : null;
        }
        catch (Exception ex)
        {
            App.LogError(new InvalidOperationException("Текст песни: LRCLIB не ответил", ex));
            Cache.TryRemove(Key(title, artist), out _); // сеть подвела — попробуем в следующий раз
            return null;
        }
    }

    private static Lyrics? Parse(JsonElement e)
    {
        if (e.TryGetProperty("instrumental", out JsonElement ins) && ins.ValueKind == JsonValueKind.True)
            return new Lyrics { Plain = "♪ Инструментал" };
        string synced = e.TryGetProperty("syncedLyrics", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        string plain = e.TryGetProperty("plainLyrics", out JsonElement p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        if (synced.Length == 0 && plain.Length == 0) return null;
        var lyrics = new Lyrics { Plain = plain };
        foreach (string raw in synced.Split('\n'))
        {
            MatchCollection marks = Regex.Matches(raw, @"\[(\d{1,3}):(\d{2})(?:[.:](\d{1,3}))?\]");
            if (marks.Count == 0) continue;
            string text = Regex.Replace(raw, @"\[[^\]]*\]", "").Trim();
            foreach (Match m in marks)
            {
                double frac = m.Groups[3].Success ? double.Parse("0." + m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                var at = TimeSpan.FromSeconds(int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value) + frac);
                lyrics.Synced.Add((at, text.Length > 0 ? text : "♪"));
            }
        }
        lyrics.Synced.Sort((x, y) => x.at.CompareTo(y.at));
        return lyrics;
    }

    /// <summary>Убираем «(Official Video)», «[Remastered 2011]», «feat. …» и т. п. — так LRCLIB находит чаще.</summary>
    private static string Clean(string s)
    {
        s = Regex.Replace(s ?? "", @"\s*[\(\[][^\)\]]*(official|video|audio|lyric|remaster|live|clip|клип|версия|version|hd|4k|mv)[^\)\]]*[\)\]]", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+(feat\.?|ft\.?|при уч\.)\s+.*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }
}

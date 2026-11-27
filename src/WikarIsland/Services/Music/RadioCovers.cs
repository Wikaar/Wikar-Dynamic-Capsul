using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Обложки песен и логотипы для радио — как в Wikar Radio: станции холдинга AMG Radio (Ретро Хит, Русский Рок,
/// Русская Волна…) отдают всё одним JSON на amgradio.ru (<c>/?live=1</c>), для остальных обложку ищем в
/// каталоге iTunes и берём, только если исполнитель совпал.
/// </summary>
public static class RadioCovers
{
    public sealed record AmgStation(string Key, string Name, string Logo, string Track, string Cover);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyList<AmgStation> _amg = Array.Empty<AmgStation>();
    private static long _amgAt;

    /// <summary>Станция AMG Radio по адресу потока (volna.top, amgradio).</summary>
    public static bool LooksLikeAmg(string? streamUrl)
    {
        string u = (streamUrl ?? "").ToLowerInvariant();
        return u.Contains("volna.top") || u.Contains("amgradio") || u.Contains("amg-radio");
    }

    /// <summary>Логотип станции AMG или null.</summary>
    public static async Task<string?> AmgLogoAsync(HttpClient http, string streamUrl, string name, CancellationToken ct)
    {
        if (!LooksLikeAmg(streamUrl)) return null;
        AmgStation? s = Match(streamUrl, name, await AmgListAsync(http, 6 * 3600_000, ct).ConfigureAwait(false));
        return s != null && s.Logo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s.Logo : null;
    }

    /// <summary>Обложка песни <paramref name="track"/> («Исполнитель - Название»): AMG, иначе iTunes. null — не нашли.</summary>
    public static async Task<string?> CoverAsync(HttpClient http, string streamUrl, string stationName, string track, CancellationToken ct)
    {
        if (LooksLikeAmg(streamUrl))
        {
            AmgStation? s = Match(streamUrl, stationName, await AmgListAsync(http, 10_000, ct).ConfigureAwait(false));
            if (s != null && Same(s.Track, track) && s.Cover.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return s.Cover;
        }
        return await ITunesAsync(http, track, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<AmgStation>> AmgListAsync(HttpClient http, long maxAgeMs, CancellationToken ct)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = Environment.TickCount64;
            if (_amg.Count > 0 && now - _amgAt < maxAgeMs) return _amg;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                string json = await http.GetStringAsync("https://amgradio.ru/?live=1", cts.Token).ConfigureAwait(false);
                IReadOnlyList<AmgStation> fresh = ParseAmg(json);
                if (fresh.Count > 0) { _amg = fresh; _amgAt = now; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* сайт недоступен — без обложки */ }
            return _amg;
        }
        finally { Gate.Release(); }
    }

    public static IReadOnlyList<AmgStation> ParseAmg(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("stations", out JsonElement stations) || stations.ValueKind != JsonValueKind.Object)
            return Array.Empty<AmgStation>();
        static string Str(JsonElement o, string n) => o.TryGetProperty(n, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return stations.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .Select(p => new AmgStation(p.Name, Str(p.Value, "name"), Str(p.Value, "logo"), Str(p.Value, "track"), Str(p.Value, "cover")))
            .ToArray();
    }

    /// <summary>Сначала по адресу потока (retro.volna.top/Retro → retro), потом по самому длинному названию AMG в имени станции.</summary>
    public static AmgStation? Match(string streamUrl, string stationName, IReadOnlyList<AmgStation> list)
    {
        if (list.Count == 0) return null;
        var words = new List<string>();
        if (Uri.TryCreate(streamUrl?.Trim(), UriKind.Absolute, out Uri? uri))
        {
            words.Add(uri.Host.Split('.')[0].ToLowerInvariant());
            string? last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (last != null) words.Add(last.Split('.')[0].ToLowerInvariant());
        }
        AmgStation? byStream = list.FirstOrDefault(s => words.Contains(s.Key.ToLowerInvariant()));
        if (byStream != null) return byStream;
        string name = Norm(stationName);
        return list.Where(s => Norm(s.Name).Length >= 3 && name.Contains(Norm(s.Name), StringComparison.Ordinal))
            .OrderByDescending(s => Norm(s.Name).Length).FirstOrDefault();
    }

    private static async Task<string?> ITunesAsync(HttpClient http, string track, CancellationToken ct)
    {
        (string? artist, string? title) = Split(track);
        if (artist == null || title == null) return null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            string term = Uri.EscapeDataString($"{artist} {title}");
            string json = await http.GetStringAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=5", cts.Token).ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out JsonElement results)) return null;
            foreach (JsonElement r in results.EnumerateArray())
            {
                string? found = r.TryGetProperty("artistName", out JsonElement a) ? a.GetString() : null;
                string? art = r.TryGetProperty("artworkUrl100", out JsonElement u) ? u.GetString() : null;
                if (art == null || !SameArtist(found, artist)) continue;
                return art.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { }
        return null;
    }

    public static (string? Artist, string? Title) Split(string track)
    {
        foreach (string sep in new[] { " - ", " – ", " — " })
        {
            int i = track.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0 && i + sep.Length < track.Length) return (track[..i].Trim(), track[(i + sep.Length)..].Trim());
        }
        return (null, null);
    }

    public static string Norm(string? s) => new((s ?? "").ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());

    public static bool Same(string? a, string? b)
    {
        string x = Norm(a), y = Norm(b);
        if (x.Length < 4 || y.Length < 4) return false;
        return x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }

    private static bool SameArtist(string? found, string? wanted)
    {
        string x = Norm(found), y = Norm(wanted);
        if (x.Length < 2 || y.Length < 2) return false;
        return x == y || x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);
    }
}

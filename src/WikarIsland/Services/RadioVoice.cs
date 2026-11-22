using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Радио голосом: «включи радио», «включи радио Европа Плюс», «включи радио джаз», «включи Шансон по радио»,
/// «следующая станция», «выключи радио», «добавь станцию в избранное», «что играет на радио».
/// </summary>
public static class RadioVoice
{
    private static RadioProvider R => App.Music.Radio;

    /// <summary>Ответ Капсулки или null, если фраза не про радио.</summary>
    public static async Task<string?> TryHandleAsync(string text)
    {
        Match m;
        const string Play = @"^(?:включи|поставь|запусти|давай|врубай|вруби|сыграй)\s+(?:мне\s+)?";

        if (Regex.IsMatch(text, Play + @"(?:радио|радиостанцию|радио\s+станцию)$"))
            return await PlayAsync(null);
        if ((m = Regex.Match(text, Play + @"(?:радиостанцию|радио\s*станцию|радио)\s+(.+)$")).Success
            || (m = Regex.Match(text, Play + @"(.+?)\s+(?:по|на)\s+радио$")).Success)
            return await PlayAsync(m.Groups[1].Value.Trim());

        if (Regex.IsMatch(text, @"^(?:выключи|останови|отключи|убери|стоп)\s+радио$"))
        {
            if (App.Music.Active != R) return "Радио и так не играет, сэр.";
            App.Music.StopIfActive(R);
            return "";
        }

        bool radioOn = App.Music.Active == R;
        if (radioOn && Regex.IsMatch(text, @"^(?:включи\s+|давай\s+)?(?:следующ\w+|друг\w+|дальше|переключи)\s+(?:радио\s*)?(?:станци\w*|радио)$|^переключи\s+(?:станцию|радио)$"))
        {
            R.Next();
            return "";
        }
        if (radioOn && Regex.IsMatch(text, @"^(?:включи\s+|верни\s+)?(?:предыдущ\w+|прошл\w+)\s+(?:радио\s*)?станци\w*$"))
        {
            R.Previous();
            return "";
        }
        if (radioOn && Regex.IsMatch(text, @"^(?:добавь|сохрани|запомни)\s+(?:эту\s+)?(?:станцию|радио)(?:\s+в\s+избранное)?$"))
            return Favorite(add: true);
        if (radioOn && Regex.IsMatch(text, @"^(?:убери|удали)\s+(?:эту\s+)?(?:станцию|радио)\s+из\s+избранного$"))
            return Favorite(add: false);
        if (radioOn && Regex.IsMatch(text, @"^(?:что|какая песня|какой трек)\s+(?:сейчас\s+)?(?:играет|звучит|идет)\s+(?:на|по)\s+радио$"))
            return NowPlaying();

        return null;
    }

    /// <summary>Включить станцию по названию или жанру; без запроса — последнюю/избранную/популярную.</summary>
    public static async Task<string> PlayAsync(string? query)
    {
        if (query == null)
        {
            if (!await App.Music.PlayMixAsync(R)) return "Не получилось, сэр: " + App.Music.Status;
            return R.CurrentStation is { } last ? $"Включаю радио «{last}»." : "Включаю радио.";
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        IReadOnlyList<MusicTrack>? found = await App.Music.SearchAsync(R, query, cts.Token);
        if (found == null) return "Радио: " + App.Music.Status;
        if (found.Count == 0) return $"Не нашёл радиостанцию «{query}», сэр.";
        App.Panel.ShowMusicResults(R, found, query);
        bool ok = await App.Music.PlayAsync(R, found, 0);
        return ok ? $"Включаю радио «{found[0].Title}»." : "Не получилось, сэр: " + App.Music.Status;
    }

    private static string Favorite(bool add)
    {
        if (App.Music.Radio.CurrentStation is not { } name) return "Сейчас ничего не играет, сэр.";
        MusicTrack? t = CurrentTrack();
        if (t == null) return "Не понял, какая станция играет, сэр.";
        if (RadioProvider.IsFavorite(t) == add) return add ? $"«{name}» уже в избранном." : $"«{name}» и так не в избранном.";
        RadioProvider.ToggleFavorite(t);
        App.Music.RaiseChanged();
        return add ? $"Добавил «{name}» в избранное." : $"Убрал «{name}» из избранного.";
    }

    private static MusicTrack? CurrentTrack() => App.Settings.RadioLast is { } s && s.Name == R.CurrentStation
        ? new MusicTrack("radio", s.Id, s.Name, s.Info, s.Favicon.Length > 0 ? s.Favicon : null, TimeSpan.Zero) { Extra = s.Url }
        : null;

    private static string NowPlaying()
    {
        MusicState? s = R.State;
        if (s == null) return "Радио сейчас не играет, сэр.";
        return s.Artist == "Радио в эфире"
            ? $"Играет станция «{s.Title}», но она не сообщает, что звучит."
            : $"На радио: {s.Artist.Replace(" · ", ", станция ")} — «{s.Title}».";
    }

    /// <summary>Инструмент radio для ИИ и Gemini Live.</summary>
    public static async Task<string> ToolAsync(string action, string? query)
    {
        switch (action)
        {
            case "stop":
                if (App.Music.Active != R) return "Радио и так не играло";
                App.Music.StopIfActive(R);
                return "Радио выключено";
            case "next":
                if (App.Music.Active != R) return "Радио не играет";
                R.Next();
                return "Переключил на следующую станцию";
            case "favorite":
                return App.Music.Active == R ? Favorite(add: true) : "Радио не играет";
            default:
                string reply = await PlayAsync(string.IsNullOrWhiteSpace(query) ? null : query);
                return reply.StartsWith("Включаю", StringComparison.Ordinal) ? reply : "НЕ получилось: " + reply;
        }
    }
}

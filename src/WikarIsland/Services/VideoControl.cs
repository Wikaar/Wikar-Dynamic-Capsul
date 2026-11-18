using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WikarIsland.Views;

namespace WikarIsland.Services;

/// <summary>
/// Голосом про видео YouTube: включить по названию, следующее/предыдущее, перемотка, скорость, качество,
/// субтитры, звук, весь экран, мини-плеер, закрыть, открыть в браузере.
/// Команды идут во встроенный плеер острова, если в нём что-то есть, иначе — в YouTube в браузере.
/// </summary>
public static class VideoControl
{
    private static YouTubeVideoProvider V => App.Music.Video;

    /// <summary>Где сейчас видео: во встроенном плеере, в браузере или нигде.</summary>
    public enum Target
    {
        None,
        Island,
        Browser,
    }

    public static Target Current =>
        V.HasVideo ? Target.Island
        : BrowserYouTube.HasActiveTab ? Target.Browser
        : Target.None;

    private const string Yt = @"(?:ютуб\w*|ютюб\w*|youtube|you tube)";

    // ---------- быстрые голосовые команды ----------

    /// <summary>
    /// Разобрать фразу. null — это не про видео (пусть разбирают другие команды).
    /// Пустая строка — сделано молча.
    /// </summary>
    public static async Task<string?> TryHandleAsync(string text)
    {
        string t = RuNumbers.Digitize(text);
        Match m;
        Target? cached = null;
        Target Tgt() => cached ??= Current; // перебор окон браузеров — только если фраза похожа на команду

        if (Regex.IsMatch(t, @"^(?:открой|досмотр\w+|перенеси|продолжи)\s+(?:это\s+|видео\s+|ролик\s+)?в\s+браузере$|^досмотрю\s+в\s+браузере$"))
            return Command(Tgt(), "open_in_browser");

        // --- включить по названию ---
        if ((m = Regex.Match(t, @"^(?:найди|поищи|ищи)\s+(?:мне\s+)?(?:на|в)\s+" + Yt + @"\s+(.+)$")).Success
            || (m = Regex.Match(t, @"^(?:найди|поищи)\s+(?:мне\s+)?(?:видео|ролик\w*|видос\w*)\s+(.+?)(?:\s+(?:на|в)\s+" + Yt + ")?$")).Success)
            return await FindAsync(m.Groups[1].Value.Trim());
        if ((m = Regex.Match(t, @"^(?:включи|поставь|открой|покажи|запусти|давай)\s+(?:мне\s+)?(?:на|в)\s+" + Yt + @"\s+(.+)$")).Success
            || (m = Regex.Match(t, @"^(?:включи|поставь|открой|покажи|запусти)\s+(?:мне\s+)?(?:видео|ролик|видос)\s+(?!на паузу|на весь|на полный|во весь|в браузере|сначала|с начала|в угол|в окно|дальше)(.+?)(?:\s+(?:на|в)\s+" + Yt + ")?$")).Success
            || (m = Regex.Match(t, @"^(?:включи|поставь|покажи|запусти)\s+(?:мне\s+)?(.+?)\s+(?:на|в|из)\s+" + Yt + "$")).Success)
            return await PlayAsync(m.Groups[1].Value.Trim(), null);
        if (Regex.IsMatch(t, @"^(?:открой|запусти|включи)\s+" + Yt + "$") && App.Settings.VideoInIsland)
        {
            await App.Music.PlayMixAsync(V);
            return "";
        }

        // --- ниже — управление тем, что уже открыто ---
        bool videoWord = Regex.IsMatch(t, @"\b(видео|ролик\w*|видос\w*|ютуб\w*|youtube|плеер\w*)\b");

        if (Regex.IsMatch(t, @"^(?:включи\s+|давай\s+)?(?:следующ\w+|другое|другой|дальше|переключи)\s+(?:видео|ролик\w*|видос\w*)$|^(?:видео|ролик)\s+дальше$"))
            return Command(Tgt(), "next");
        if (Regex.IsMatch(t, @"^(?:включи\s+|верни\s+)?(?:предыдущ\w+|прошл\w+)\s+(?:видео|ролик\w*|видос\w*)$"))
            return Command(Tgt(), "previous");
        if (Regex.IsMatch(t, @"^(?:поставь\s+)?(?:видео|ролик)\s+на\s+паузу$|^(?:останови|пауза|стоп)\s+(?:видео|ролик)$"))
            return Command(Tgt(), "pause");
        if (Regex.IsMatch(t, @"^(?:продолжи|запусти|включи|играй)\s+(?:видео|ролик)$|^(?:видео|ролик)\s+дальше играй$"))
            return Command(Tgt(), "resume");

        // --- перемотка: работает и для музыки во встроенном плеере ---
        if (Regex.IsMatch(t, @"^(?:перемотай|промотай|отмотай|мотни|перемотка)\b") && ParseSeek(t) is { } seek)
        {
            if (Tgt() == Target.None) return SeekMusic(seek.absolute, seek.seconds);
            return Command(Tgt(), seek.absolute ? "seek_to" : "seek", seek.seconds);
        }
        if (Regex.IsMatch(t, @"^(?:начни|включи|запусти)\s+(?:видео\s+|ролик\s+)?(?:сначала|с начала)$|^(?:видео|ролик)\s+(?:сначала|с начала)$"))
            return Tgt() == Target.None ? SeekMusic(true, 0) : Command(Tgt(), "seek_to", 0);
        if (Regex.IsMatch(t, @"^пропусти\s+реклам\w*$"))
            return Tgt() == Target.Browser ? Command(Tgt(), "skip_ad") : "";

        // дальше — команды без слова «видео» («быстрее», «субтитры») только когда видео открыто
        if (!videoWord && !Regex.IsMatch(t, @"(скорост|быстрее|медленнее|качеств|субтитр|полноэкран|весь экран|полный экран|мини)")) return null;
        if (!videoWord && Tgt() == Target.None) return null;

        // --- скорость ---
        if (Regex.IsMatch(t, @"^(?:сделай\s+|включи\s+)?(?:нормальн\w+|обычн\w+)\s+скорость$|^скорость\s+(?:нормальн\w+|обычн\w+|1)$"))
            return Command(Tgt(), "speed", 1);
        if ((m = Regex.Match(t, @"^(?:сделай\s+|поставь\s+)?скорость\s+(?:на\s+)?(?:х|x)?\s*(\d+(?:[.,]\d+)?)(?:\s+(\d{1,2}))?$")).Success)
        {
            double v = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (m.Groups[2].Success) v += double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / (m.Groups[2].Value.Length == 1 ? 10 : 100); // «один двадцать пять»
            if (v > 4) v /= 100; // «скорость 150» — проценты
            return Command(Tgt(), "speed", v);
        }
        if ((m = Regex.Match(t, @"^(?:сделай\s+|включи\s+)?(?:(?:видео|ролик|воспроизведение)\s+)?(побыстрее|быстрее|помедленнее|медленнее)(?:\s+(?:видео|ролик))?$")).Success
        )
        {
            return Command(Tgt(), m.Groups[1].Value.Contains("быстр") ? "faster" : "slower");
        }

        // --- качество («тысяча восемьдесят» распознаётся словами) ---
        foreach ((string words, string digits) in QualityWords) t = t.Replace(words, digits);
        if ((m = Regex.Match(t, @"^(?:поставь\s+|сделай\s+|включи\s+)?качество\s+(?:на\s+)?(\d{3,4}|4\s?к\w*|авто\w*|максимальн\w*|лучше\w*|высок\w*|низк\w*|минимальн\w*)\s*p?$")).Success)
        {
            string q = m.Groups[1].Value;
            int height = Regex.IsMatch(q, @"^4\s?к") ? 2160
                : q.StartsWith("авто") ? 0
                : Regex.IsMatch(q, "^(максимальн|лучше|высок)") ? 4320
                : Regex.IsMatch(q, "^(низк|минимальн)") ? 144
                : int.Parse(q);
            return Command(Tgt(), "quality", height);
        }

        // --- субтитры ---
        if ((m = Regex.Match(t, @"^(включи|покажи|выключи|убери|отключи|скрой)\s+субтитры$")).Success)
            return Command(Tgt(), m.Groups[1].Value is "включи" or "покажи" ? "subtitles_on" : "subtitles_off");

        // --- звук видео ---
        if ((m = Regex.Match(t, @"^(выключи|отключи|убери|верни|включи)\s+звук\s+(?:в|у)\s+(?:видео|ролик\w*|ютуб\w*|youtube|плеер\w*)$")).Success)
            return Command(Tgt(), m.Groups[1].Value is "верни" or "включи" ? "unmute" : "mute");

        // --- окно: весь экран, мини-плеер, обычное окно ---
        if (Regex.IsMatch(t, @"^(?:разверни\s+|сделай\s+|открой\s+|включи\s+)?(?:видео\s+|ролик\s+|плеер\s+)?(?:на|во)\s+(?:весь|полный)\s+экран$|^полноэкранн\w+(?:\s+режим)?$"))
            return Command(Tgt(), "fullscreen");
        if (Regex.IsMatch(t, @"^(?:выйди|выход)\s+из\s+полноэкранного(?:\s+режима)?$|^(?:выключи|убери)\s+(?:полный|весь)\s+экран$"))
            return Command(Tgt(), "exit_fullscreen");
        if (Regex.IsMatch(t, @"^(?:включи\s+|сделай\s+)?мини\s?-?\s?плеер$|^(?:видео|ролик|плеер)\s+в\s+угол$|^(?:сделай\s+)?(?:видео|плеер)\s+(?:поменьше|маленьким)$|^маленьк\w+\s+(?:окно|плеер)$"))
            return Command(Tgt(), "mini");
        if (Regex.IsMatch(t, @"^(?:обычн\w+|нормальн\w+)\s+(?:окно|режим)(?:\s+видео)?$|^(?:верни|разверни)\s+(?:видео|плеер)(?:\s+в\s+окно)?$"))
            return Command(Tgt(), "window");
        if (Regex.IsMatch(t, @"^сверни\s+(?:видео|ролик|плеер|ютуб\w*)$"))
            return Command(Tgt(), V.Mode == VideoMode.Full || Tgt() == Target.Browser ? "exit_fullscreen" : "mini");
        if (Regex.IsMatch(t, @"^(?:закрой|выключи|убери|останови)\s+(?:видео|ролик|плеер|ютуб\w*|youtube)$"))
            return Command(Tgt(), "close");
        return null;
    }

    private static readonly (string, string)[] QualityWords =
    {
        ("ультра эйч ди", "2160"), ("тысяча восемьдесят", "1080"), ("тысяча четыреста сорок", "1440"), ("2 тысячи сто шестьдесят", "2160"),
        ("семьсот двадцать", "720"), ("четыреста восемьдесят", "480"), ("триста шестьдесят", "360"),
        ("фулл эйч ди", "1080"), ("full hd", "1080"), ("фулл hd", "1080"), ("эйч ди", "720"),
    };

    /// <summary>«перемотай на 2 минуты вперёд», «отмотай 30 секунд», «перемотай на 5 30» (на отметку 5:30).</summary>
    private static (bool absolute, double seconds)? ParseSeek(string t)
    {
        if (Regex.IsMatch(t, @"(?:в|на)\s+(?:самое\s+)?начало|сначала|с начала")) return (true, 0);
        bool back = t.StartsWith("отмотай") || Regex.IsMatch(t, @"\bназад\b");
        bool forward = Regex.IsMatch(t, @"\bвперед\b") || t.StartsWith("пропусти");

        // отметка: «на 5 30», «на 5:30», «на 1 час 20 минут», «на 5 минут 30 секунд», «к 2 минуте»
        Match mark = Regex.Match(t, @"(?:на|к|до)\s+(?:отметку\s+)?(\d{1,2})\s*(?:час\w*\s+|:)?(\d{1,2})\s*(?:минут\w*\s*|:)?(\d{1,2})?\s*(?:секунд\w*)?\b");
        if (!back && !forward)
        {
            Match hms = Regex.Match(t, @"(\d{1,2})\s*час\w*\s+(\d{1,2})\s*минут\w*(?:\s+(\d{1,2})\s*секунд\w*)?");
            if (hms.Success)
                return (true, int.Parse(hms.Groups[1].Value) * 3600 + int.Parse(hms.Groups[2].Value) * 60 + (hms.Groups[3].Success ? int.Parse(hms.Groups[3].Value) : 0));
            Match ms = Regex.Match(t, @"(\d{1,3})\s*(?:минут\w*|мин)?\s*[: ]\s*(\d{1,2})(?:\s*секунд\w*)?\s*$");
            if (ms.Success && (t.Contains(':') || Regex.IsMatch(t, @"\d+\s+(?:минут\w*\s+)?\d+")))
                return (true, int.Parse(ms.Groups[1].Value) * 60 + int.Parse(ms.Groups[2].Value));
            if (Regex.IsMatch(t, @"\bк\s+\d") && mark.Success)
                return (true, int.Parse(mark.Groups[1].Value) * 60);
        }

        // сдвиг: число и единица, «на минуту», «на полминуты»
        Match amount = Regex.Match(t, @"(\d+(?:[.,]\d+)?)?\s*(секунд\w*|сек|минут\w*|мин|час\w*)");
        double seconds = 10;
        if (amount.Success)
        {
            double n = amount.Groups[1].Success ? double.Parse(amount.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 1;
            string unit = amount.Groups[2].Value;
            seconds = unit.StartsWith("сек") ? n : unit.StartsWith("час") ? n * 3600 : n * 60;
        }
        else if (Regex.IsMatch(t, @"полминут")) seconds = 30;
        else if (Regex.Match(t, @"\b(\d{1,4})\b") is { Success: true } bare) seconds = int.Parse(bare.Groups[1].Value);
        return (false, back ? -seconds : seconds);
    }

    /// <summary>Перемотка музыки (встроенный плеер или медиапульт Windows), если видео не открыто.</summary>
    private static string? SeekMusic(bool absolute, double seconds)
    {
        MediaInfo? mi = App.Media.Current;
        if (mi == null) return null;
        TimeSpan to = absolute ? TimeSpan.FromSeconds(seconds) : mi.LivePosition + TimeSpan.FromSeconds(seconds);
        if (to < TimeSpan.Zero) to = TimeSpan.Zero;
        if (mi.Duration > TimeSpan.Zero && to > mi.Duration) to = mi.Duration - TimeSpan.FromSeconds(1);
        App.Media.Seek(to);
        return "";
    }

    // ---------- включить ----------

    /// <summary>Найти и сразу включить первый ролик: во встроенном плеере или в браузере (настройка или where).</summary>
    public static async Task<string> PlayAsync(string query, string? where)
    {
        query = Regex.Replace(query, @"^(?:видео|ролик|видос)\s+", "").Trim();
        if (query.Length == 0) return "Что включить, сэр?";
        bool island = where switch { "island" => true, "browser" => false, _ => App.Settings.VideoInIsland };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        if (island)
        {
            IReadOnlyList<MusicTrack>? found = await App.Music.SearchAsync(V, query, cts.Token);
            if (found == null) return "YouTube: " + App.Music.Status;
            if (found.Count == 0) return $"На ютубе ничего не нашлось по запросу «{query}».";
            App.Panel.ShowMusicResults(V, found, query);
            bool ok = await App.Music.PlayAsync(V, found, 0);
            return ok ? $"Включаю: {Short(found[0].Title)}." : "Не получилось: " + App.Music.Status;
        }

        string url;
        string title = "";
        try
        {
            IReadOnlyList<MusicTrack> found = await V.SearchAsync(query, cts.Token);
            url = found.Count > 0 ? "https://www.youtube.com/watch?v=" + found[0].Id : "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
            if (found.Count > 0) title = found[0].Title;
        }
        catch (Exception ex) when (ex is MusicException or OperationCanceledException)
        {
            url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query);
        }
        BrowserYouTube.Open(url);
        return title.Length > 0 ? $"Включаю в браузере: {Short(title)}." : "Открыл поиск на ютубе.";
    }

    /// <summary>«Найди на ютубе …» — показать найденное в капсуле, ничего не включая.</summary>
    private static async Task<string> FindAsync(string query)
    {
        if (!App.Settings.VideoInIsland)
        {
            AppIndex.OpenUri("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(query));
            return "Ищу на ютубе.";
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        IReadOnlyList<MusicTrack>? found = await App.Music.SearchAsync(V, query, cts.Token);
        if (found == null) return "YouTube: " + App.Music.Status;
        if (found.Count == 0) return $"На ютубе ничего не нашлось по запросу «{query}».";
        App.Panel.ShowMusicResults(V, found, query);
        App.Panel.ShowTab("music", activate: true);
        return $"Нашёл {found.Count}. Первый — {Short(found[0].Title)}. Скажите «включи на ютубе …» или выберите в капсуле.";
    }

    private static string Short(string title) => title.Length > 70 ? title[..70].TrimEnd() + "…" : title;

    // ---------- управление ----------

    /// <summary>Для ИИ и Gemini Live: действие с видео. Пустой ответ — «Готово».</summary>
    public static async Task<string> ToolAsync(string action, string query, double? number, string where)
    {
        if (action == "play") return await PlayAsync(query, where.Length > 0 ? where : null);
        if (action == "search") return await FindAsync(query);
        if (action == "home")
        {
            await App.Music.PlayMixAsync(V);
            return "YouTube открыт в окне острова.";
        }
        string? r = Command(Current, action, number);
        return r == null ? "Не знаю такого действия." : r.Length == 0 ? "Готово" : r;
    }

    /// <summary>Выполнить действие там, где сейчас видео. null — неизвестное действие.</summary>
    public static string? Command(Target target, string action, double? number = null)
    {
        if (target == Target.None)
            return action is "close" or "pause" ? "" : "Сейчас никакое видео не открыто, сэр. Скажите, например: включи на ютубе обзор Bigster.";
        return target == Target.Island ? Island(action, number) : Browser(action, number);
    }

    private static string? Island(string action, double? n)
    {
        switch (action)
        {
            case "pause": V.Pause(); return "";
            case "resume": V.Resume(); return "";
            case "toggle": V.PlayPause(); return "";
            case "next": V.Next(); return "";
            case "previous": V.Previous(); return "";
            case "seek": V.SeekBy(n ?? 10); return "";
            case "seek_to": V.Seek(TimeSpan.FromSeconds(n ?? 0)); return "";
            case "speed": return Speed(V.SetSpeedAsync(n ?? 1, 0));
            case "faster": return Speed(V.SetSpeedAsync(null, 0.25));
            case "slower": return Speed(V.SetSpeedAsync(null, -0.25));
            case "quality":
                _ = Report(V.SetQualityAsync((int)(n ?? 0)));
                return "";
            case "subtitles_on": _ = V.SetSubtitlesAsync(true); return "";
            case "subtitles_off": _ = V.SetSubtitlesAsync(false); return "";
            case "mute": V.SetMuted(true); return "";
            case "unmute": V.SetMuted(false); return "";
            case "fullscreen": V.SetWindowMode(VideoMode.Full); return "";
            case "exit_fullscreen": V.SetWindowMode(V.Mode == VideoMode.Full ? V.BeforeFull : VideoMode.Normal); return "";
            case "mini": V.SetWindowMode(VideoMode.Mini); return "";
            case "window": V.SetWindowMode(VideoMode.Normal); return "";
            case "close": App.Music.StopIfActive(V); return "";
            case "open_in_browser": return V.OpenInBrowser();
            case "skip_ad": return ""; // встроенный плеер жмёт «Пропустить» сам
        }
        return null;
    }

    /// <summary>Скорость меняется асинхронно: команда выполняется молча, итог — в журнал.</summary>
    private static string Speed(Task<double> task)
    {
        _ = Report(task.ContinueWith(t => t.IsCompletedSuccessfully ? $"Скорость {t.Result.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"))}." : "",
            TaskScheduler.FromCurrentSynchronizationContext()));
        return "";
    }

    /// <summary>Результат, который приходит позже (скорость, качество), — в журнал Капсулки.</summary>
    private static async Task Report(Task<string> task)
    {
        try
        {
            string text = await task;
            if (text.Length > 0) App.LogVoice("  видео: " + text);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static string? Browser(string action, double? n)
    {
        string? done;
        switch (action)
        {
            case "pause":
            case "resume":
            case "toggle":
                done = BrowserYouTube.Keys("k");
                break;
            case "next": done = BrowserYouTube.Keys("shift+n"); break;
            case "previous": done = BrowserYouTube.Keys("alt+left"); break;
            case "seek":
            {
                double s = n ?? 10;
                int presses = Math.Clamp((int)Math.Round(Math.Abs(s) / 10), 1, 60);
                done = BrowserYouTube.Keys(Enumerable.Repeat(s < 0 ? "j" : "l", presses).ToArray());
                break;
            }
            case "seek_to":
                if ((n ?? 0) <= 0)
                {
                    done = BrowserYouTube.Keys("0"); // 0 — в начало ролика
                    break;
                }
                return "В браузере точно на отметку не перемотать, сэр. Скажите «перемотай вперёд на 2 минуты» или откройте видео в острове.";
            case "faster": done = BrowserYouTube.Keys("shift+period"); break;
            case "slower": done = BrowserYouTube.Keys("shift+comma"); break;
            case "speed":
            {
                // в браузере — шагами по 0,25 от обычной скорости не узнать; выставляем от 1 вниз до минимума и вверх
                double v = Math.Clamp(n ?? 1, 0.25, 2);
                int up = (int)Math.Round((v - 0.25) / 0.25);
                var keys = Enumerable.Repeat("shift+comma", 8).Concat(Enumerable.Repeat("shift+period", up)).ToArray();
                done = BrowserYouTube.Keys(keys);
                break;
            }
            case "quality":
                return "Качество в браузере выбирается в настройках плеера, сэр. Во встроенном плеере острова — голосом.";
            case "subtitles_on":
            case "subtitles_off":
                done = BrowserYouTube.Keys("c");
                break;
            case "mute":
            case "unmute":
                done = BrowserYouTube.Keys("m");
                break;
            case "fullscreen":
            case "exit_fullscreen":
                done = BrowserYouTube.Keys(action == "fullscreen" ? "f" : "esc");
                break;
            case "mini":
                done = BrowserYouTube.Keys("i"); // мини-проигрыватель YouTube в браузере
                break;
            case "window":
                done = BrowserYouTube.Keys("esc");
                break;
            case "close":
                return BrowserYouTube.CloseTab() ? "" : "Не нашёл вкладку YouTube.";
            case "open_in_browser":
                return "Видео и так в браузере, сэр.";
            case "skip_ad":
                return "В браузере рекламу пропускает кнопка «Пропустить» на самом видео, сэр.";
            default:
                return null;
        }
        return done == null ? "Не нашёл YouTube в браузере, сэр." : "";
    }
}

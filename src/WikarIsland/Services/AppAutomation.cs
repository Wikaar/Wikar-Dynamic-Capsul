using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace WikarIsland.Services;

/// <summary>
/// Управление внутри программ голосом: «нажми Играть в Battle.net», «включи Мою волну в Яндекс Музыке».
/// Кнопки ищутся через Windows UI Automation — тот же механизм, которым пользуются экранные дикторы,
/// поэтому работает в большинстве программ (обычные Windows-приложения, Electron, Chromium, Qt).
/// Если у кнопки нет «нажатия» через UI Automation — щёлкаем мышью по её центру и возвращаем курсор.
/// </summary>
public static class AppAutomation
{
    private static readonly ControlType[] Clickable =
    {
        ControlType.Button, ControlType.Hyperlink, ControlType.MenuItem, ControlType.ListItem, ControlType.TabItem,
        ControlType.CheckBox, ControlType.RadioButton, ControlType.SplitButton, ControlType.TreeItem, ControlType.DataItem,
        ControlType.Image, ControlType.Text, ControlType.Group, ControlType.Custom,
    };

    /// <summary>Нажать в программе элемент с таким названием. Возвращает, что получилось, — это читает ИИ.</summary>
    public static string Click(string app, string target)
    {
        List<OpenWindow> windows = WindowControl.Find(AssistantService.NameVariants(app), out string shown);
        if (windows.Count == 0) return $"Программа «{app}» не открыта. Сначала запусти её (open_app) и попробуй снова.";
        WindowControl.Restore(windows);
        Thread.Sleep(350); // окно должно успеть развернуться и отрисоваться

        string? result = RunWithTimeout(() =>
        {
            foreach (OpenWindow w in windows)
            {
                AutomationElement root = AutomationElement.FromHandle(w.Handle);
                List<(AutomationElement el, string name)> items = Collect(root);
                if (items.Count == 0)
                {
                    // Chromium/Electron строит дерево для UI Automation только после первого запроса — даём ему мгновение
                    Thread.Sleep(700);
                    items = Collect(root);
                }
                (AutomationElement el, string name)? best = Best(items, target);
                if (best is not { } hit) continue;
                return Press(hit.el, hit.name, shown);
            }
            string names = string.Join(", ", windows.SelectMany(w => Collect(AutomationElement.FromHandle(w.Handle)))
                .Select(i => i.name).Distinct().Take(40));
            return names.Length == 0
                ? $"В окне {shown} не видно кнопок (программа не открывает их для UI Automation). Попробуй горячие клавиши (app_keys)."
                : $"Не нашёл «{target}» в {shown}. Видимые элементы: {names}";
        });
        return result ?? $"{shown} не ответил вовремя.";
    }

    /// <summary>Какие кнопки и пункты видны в окне программы — чтобы ИИ знал, что нажимать.</summary>
    public static string ListButtons(string app)
    {
        List<OpenWindow> windows = WindowControl.Find(AssistantService.NameVariants(app), out string shown);
        if (windows.Count == 0) return $"Программа «{app}» не открыта.";
        string? result = RunWithTimeout(() =>
        {
            string names = string.Join(", ", windows.SelectMany(w => Collect(AutomationElement.FromHandle(w.Handle)))
                .Select(i => i.name).Distinct().Take(80));
            return names.Length == 0 ? $"В {shown} не видно элементов." : $"{shown}: {names}";
        });
        return result ?? $"{shown} не ответил вовремя.";
    }

    /// <summary>Нажать клавиши в программе: «space», «ctrl+f», «enter», «f5», «alt+tab».</summary>
    public static string Keys(string app, string keys)
    {
        List<OpenWindow> windows = WindowControl.Find(AssistantService.NameVariants(app), out string shown);
        if (windows.Count == 0) return $"Программа «{app}» не открыта.";
        WindowControl.Restore(windows);
        Thread.Sleep(300);
        foreach (string combo in keys.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SendCombo(combo)) return $"Не знаю клавишу «{combo}».";
            Thread.Sleep(80);
        }
        return $"Нажато в {shown}: {keys}";
    }

    /// <summary>Напечатать текст в программе (в активное поле), при enter — нажать Enter.</summary>
    public static string Type(string app, string text, bool enter)
    {
        List<OpenWindow> windows = WindowControl.Find(AssistantService.NameVariants(app), out string shown);
        if (windows.Count == 0) return $"Программа «{app}» не открыта.";
        WindowControl.Restore(windows);
        Thread.Sleep(300);
        foreach (char c in text) SendChar(c);
        if (enter) SendCombo("enter");
        return $"Напечатано в {shown}: {text}";
    }

    /// <summary>
    /// Быстрый переход Ctrl+K (Discord, Slack): открыть окно, Ctrl+K, напечатать название, подождать подсказки, Enter.
    /// В Discord выбранный так голосовой канал сразу подключается.
    /// </summary>
    public static string GoToChat(string app, string name)
    {
        if (name.Trim().Length == 0) return "Не сказано, куда перейти.";
        List<OpenWindow> windows = WindowControl.Find(AssistantService.NameVariants(app), out string shown);
        if (windows.Count == 0) return $"Программа «{app}» не открыта — сначала open_app.";
        WindowControl.Restore(windows);
        Thread.Sleep(400);
        SendCombo("ctrl+k");
        Thread.Sleep(450);
        foreach (char c in name.Trim()) SendChar(c);
        Thread.Sleep(700); // список совпадений появляется не сразу
        SendCombo("enter");
        return $"Перешёл в {shown} к «{name.Trim()}» через быстрый переход. Если открылось не то — посмотри app_buttons.";
    }

    // ---------- музыка ----------

    private sealed record MusicService(string Name, string[] Apps, string Web, string SearchUrl, string[] PlayWords, string[] StartTargets);

    private static readonly MusicService Yandex = new("Яндекс Музыка", new[] { "Яндекс Музыка", "Yandex Music" },
        "https://music.yandex.ru", "https://music.yandex.ru/search?text={0}",
        new[] { "Воспроизвести", "Слушать", "Играть", "Play" }, new[] { "Моя волна", "Слушать", "Воспроизвести" });

    private static readonly MusicService YouTubeMusic = new("YouTube Music", new[] { "YouTube Music" },
        "https://music.youtube.com", "https://music.youtube.com/search?q={0}",
        new[] { "Воспроизвести", "Слушать", "Play", "Перемешать", "Shuffle" }, new[] { "Мой джем", "My Supermix", "Перемешать", "Воспроизвести", "Play" });

    private static readonly MusicService Spotify = new("Spotify", new[] { "Spotify" },
        "https://open.spotify.com", "spotify:search:{0}",
        new[] { "Воспроизвести", "Play" }, new[] { "Воспроизвести", "Play" });

    /// <summary>«Яндекс Музыка», «ютуб музыка», «spotify» в любой форме → сервис.</summary>
    public static bool IsMusicService(string spoken) =>
        System.Text.RegularExpressions.Regex.IsMatch(spoken, ServicePattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Название сервиса в любом написании: «Яндекс.Музыка», «яндекс-музыке», «YouTube Music», «ютуб музыку», «Spotify».</summary>
    public const string ServicePattern =
        @"янд\w*[\s.\-]*музык\w*|yandex[\s.\-]*music\w*|(?:ютуб|ютюб|youtube)[\s.\-]*(?:musi[ck]\w*|музык\w*)|спотиф\w*|spotify";

    /// <summary>Итог команды «включи музыку»: заиграла ли музыка на самом деле (по медиапульту Windows) и что именно.</summary>
    public sealed record MusicResult(bool Playing, string Message);

    private static MusicService ServiceOf(string spoken) =>
        spoken.Contains("янд", StringComparison.OrdinalIgnoreCase) || spoken.Contains("yandex", StringComparison.OrdinalIgnoreCase) ? Yandex
        : spoken.Contains("спот", StringComparison.OrdinalIgnoreCase) || spoken.Contains("spot", StringComparison.OrdinalIgnoreCase) ? Spotify
        : YouTubeMusic;

    /// <summary>
    /// Включить музыку: открыть сервис (программу или сайт), дождаться окна и нажать «Моя волна» / «Воспроизвести»;
    /// с запросом — найти и включить исполнителя или трек. Долго (до ~25 секунд), поэтому выполняется в фоне.
    /// </summary>
    public static async Task<MusicResult> PlayMusicAsync(string spokenService, string? query)
    {
        query = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        // сервис подключён к острову — играем во встроенном плеере: быстрее и без кликов по чужому окну
        MusicResult? builtIn = null;
        try
        {
            builtIn = await System.Windows.Application.Current.Dispatcher
                .InvokeAsync(() => App.Music.PlayVoiceAsync(spokenService, query)).Task.Unwrap();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        if (builtIn != null)
        {
            App.LogVoice($"  музыка [встроенный плеер, {spokenService}] «{query}» → {(builtIn.Playing ? "ИГРАЕТ" : "не играет")}: {builtIn.Message}");
            return builtIn;
        }
        return await PlayMusicInAppAsync(spokenService, query);
    }

    private static Task<MusicResult> PlayMusicInAppAsync(string spokenService, string? query) => Task.Run(() =>
    {
        MusicService svc = ServiceOf(spokenService);
        MediaInfo? before = App.Media.Current;
        string steps = PlayMusic(svc, query);
        MusicResult result = Verify(before, query, steps);
        App.LogVoice($"  музыка [{svc.Name}] «{query}» → {steps} → {(result.Playing ? "ИГРАЕТ" : "не играет")}: {result.Message}");
        return result;
    });

    /// <summary>
    /// Не верим кнопкам на слово: до 8 секунд смотрим в медиапульт Windows. Заиграло (новый трек или с паузы) — успех,
    /// и говорим, что именно играет. Нет — честно сообщаем, что не вышло, и на каком шаге.
    /// </summary>
    private static MusicResult Verify(MediaInfo? before, string? query, string steps)
    {
        string[] words = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToArray();
        MediaInfo? now = null;
        for (int i = 0; i < 16; i++)
        {
            Thread.Sleep(500);
            now = App.Media.Current;
            if (now is not { IsPlaying: true }) continue;
            bool changed = before is not { IsPlaying: true } || before.Title != now.Title;
            bool matches = words.Length == 0 || words.Any(w => (now.Title + " " + now.Artist).ToLowerInvariant().Contains(w));
            if (changed && matches) break;
            if (changed && i >= 8) break; // заиграло что-то другое — тоже итог
        }
        if (now is not { IsPlaying: true })
            return new MusicResult(false, $"музыка не заиграла. {steps}");
        string track = now.Artist.Length > 0 ? $"{now.Artist}, {now.Title}" : now.Title;
        bool asked = words.Length == 0 || words.Any(w => (now.Title + " " + now.Artist).ToLowerInvariant().Contains(w));
        return new MusicResult(true, asked ? $"Играет {track}." : $"Играет {track}, но это не похоже на «{query}».");
    }

    private static string PlayMusic(MusicService svc, string? query)
    {
        List<OpenWindow> windows = WindowControl.Find(svc.Apps, out _);
        bool opened = windows.Count == 0;
        if (opened)
        {
            AppEntry? app = svc.Apps.Select(AppIndex.FindConfident).FirstOrDefault(e => e != null);
            if (app != null) app.Launch();
            else AppIndex.OpenUri(query != null && !svc.SearchUrl.StartsWith("spotify:") ? string.Format(svc.SearchUrl, Uri.EscapeDataString(query)) : svc.Web);
            for (int i = 0; i < 30 && windows.Count == 0; i++)
            {
                Thread.Sleep(500);
                windows = WindowControl.Find(svc.Apps, out _);
            }
            if (windows.Count == 0) return $"{svc.Name} не открылась.";
            Thread.Sleep(3500); // интерфейс Electron/сайта догружается после появления окна
        }

        if (query == null)
        {
            foreach (string target in svc.StartTargets)
            {
                string r = Click(svc.Apps[0], target);
                if (IsDone(r)) return r;
            }
            SendCombo("playpause"); // последний трек — медиаклавишей
            return "Нажал «воспроизведение» медиаклавишей.";
        }

        if (svc == Spotify)
        {
            AppIndex.OpenUri(string.Format(svc.SearchUrl, Uri.EscapeDataString(query)));
            Thread.Sleep(2500);
        }
        else
        {
            string typed = TypeIntoSearch(svc.Apps[0], query);
            if (typed.Length > 0) return typed;
            Thread.Sleep(3000); // результаты поиска
        }
        return PlayFromResults(svc, query);
    }

    /// <summary>Мы уже в фоновом потоке и шаги сами по себе с ожиданиями — без общего таймаута, только перехват ошибок.</summary>
    private static string? Safe(Func<string> work)
    {
        try
        {
            return work();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return "Окно программы изменилось по ходу: " + ex.Message;
        }
    }

    private static bool IsDone(string r) => System.Text.RegularExpressions.Regex.IsMatch(r, "^(Нажато|Переключено|Выбрано|Открыто|Щёлкнул)");

    /// <summary>Найти поле поиска в окне и ввести запрос. Пустая строка — получилось.</summary>
    private static string TypeIntoSearch(string app, string query)
    {
        List<OpenWindow> windows = WindowControl.Find(AssistantService.NameVariants(app), out string shown);
        if (windows.Count == 0) return $"{app} не открыта.";
        WindowControl.Restore(windows);
        Thread.Sleep(300);
        string? error = Safe(() =>
        {
            AutomationElement? box = null;
            foreach (OpenWindow w in windows)
            {
                AutomationElementCollection edits = AutomationElement.FromHandle(w.Handle).FindAll(TreeScope.Descendants,
                    new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ComboBox)));
                foreach (AutomationElement e in edits)
                {
                    string n = (e.Current.Name ?? "").ToLowerInvariant();
                    if (n.Contains("поиск") || n.Contains("search") || n.Contains("найти") || n.Contains("искать")) { box = e; break; }
                    box ??= e;
                }
                if (box != null) break;
            }
            if (box == null)
            {
                // кнопка-лупа, которая открывает поле
                string r = Click(app, "Поиск");
                if (!IsDone(r)) return $"Не нашёл поиск в {shown}.";
                Thread.Sleep(600);
            }
            else
            {
                try
                {
                    box.SetFocus();
                }
                catch
                {
                    System.Windows.Rect rect = box.Current.BoundingRectangle;
                    ClickAt((int)(rect.X + rect.Width / 2), (int)(rect.Y + rect.Height / 2));
                }
                Thread.Sleep(200);
            }
            SendCombo("ctrl+a");
            foreach (char c in query) SendChar(c);
            SendCombo("enter");
            return "";
        });
        return error ?? $"{shown} не ответил вовремя.";
    }

    /// <summary>
    /// В результатах поиска: кнопка «Воспроизвести …» рядом с найденным, иначе открыть найденное и нажать «Слушать»/«Воспроизвести».
    /// Просто «Воспроизвести» без названия — это кнопка плеера (продолжит прошлый трек), её берём в последнюю очередь.
    /// </summary>
    private static string PlayFromResults(MusicService svc, string query)
    {
        List<OpenWindow> windows = WindowControl.Find(svc.Apps, out string shown);
        if (windows.Count == 0) return $"{svc.Name} закрылась.";
        string[] words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToArray();
        string? r = Safe(() =>
        {
            List<(AutomationElement el, string name)> items = windows.SelectMany(w => Collect(AutomationElement.FromHandle(w.Handle))).ToList();
            bool HasQuery(string n) => words.Length == 0 || words.Any(w => n.ToLowerInvariant().Contains(w));
            bool HasPlay(string n) => svc.PlayWords.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            var direct = items.FirstOrDefault(i => HasPlay(i.name) && HasQuery(i.name) && i.name.Length > 14);
            if (direct.el != null) return Press(direct.el, direct.name, shown);

            var found = items.FirstOrDefault(i => HasQuery(i.name) && !HasPlay(i.name));
            if (found.el == null)
                return $"Не нашёл «{query}» в {shown}. Видно: {string.Join(", ", items.Select(i => i.name).Distinct().Take(30))}";
            Press(found.el, found.name, shown);
            Thread.Sleep(2500);
            foreach (string play in svc.PlayWords)
            {
                string done = Click(svc.Apps[0], play);
                if (IsDone(done)) return $"Открыл «{found.name}» и {done}";
            }
            return $"Открыл «{found.name}», но не нашёл кнопку воспроизведения.";
        });
        return r ?? $"{shown} не ответил вовремя.";
    }

    // ---------- поиск элементов ----------

    private static List<(AutomationElement el, string name)> Collect(AutomationElement root)
    {
        var result = new List<(AutomationElement, string)>();
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(Clickable.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray()));
        AutomationElementCollection found;
        try
        {
            found = root.FindAll(TreeScope.Descendants, condition);
        }
        catch
        {
            return result;
        }
        foreach (AutomationElement el in found)
        {
            try
            {
                string name = el.Current.Name?.Trim() ?? "";
                if (name.Length is 0 or > 80) continue;
                // текст и картинки берём, только если по ним можно кликнуть (у них есть Invoke) или это кнопка
                ControlType type = el.Current.ControlType;
                if ((type == ControlType.Text || type == ControlType.Image || type == ControlType.Group || type == ControlType.Custom)
                    && !(el.TryGetCurrentPattern(InvokePattern.Pattern, out _) || el.Current.IsKeyboardFocusable))
                    continue;
                result.Add((el, name));
            }
            catch
            {
                // элемент исчез, пока перебирали
            }
        }
        return result;
    }

    private static (AutomationElement el, string name)? Best(List<(AutomationElement el, string name)> items, string target)
    {
        (AutomationElement el, string name)? best = null;
        double bestScore = 0;
        string t = target.Trim().ToLowerInvariant();
        foreach ((AutomationElement el, string name) in items)
        {
            string n = name.ToLowerInvariant();
            double s = n == t ? 1.0
                : n.StartsWith(t, StringComparison.Ordinal) ? 0.92
                : n.Contains(t, StringComparison.Ordinal) ? 0.85
                : AppIndex.Similarity(target, name);
            // кнопки важнее текста с тем же названием
            try
            {
                if (el.Current.ControlType == ControlType.Button) s += 0.03;
            }
            catch
            {
                continue;
            }
            if (s > bestScore)
            {
                bestScore = s;
                best = (el, name);
            }
        }
        return bestScore >= 0.72 ? best : null;
    }

    private static string Press(AutomationElement el, string name, string app)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return $"Нажато «{name}» в {app}.";
            }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out object toggle))
            {
                ((TogglePattern)toggle).Toggle();
                return $"Переключено «{name}» в {app}.";
            }
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object select))
            {
                ((SelectionItemPattern)select).Select();
                return $"Выбрано «{name}» в {app}.";
            }
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expand))
            {
                ((ExpandCollapsePattern)expand).Expand();
                return $"Открыто «{name}» в {app}.";
            }
        }
        catch
        {
            // не вышло программно — кликнем мышью
        }
        System.Windows.Rect r = el.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width < 1) return $"«{name}» в {app} нельзя нажать.";
        ClickAt((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
        return $"Щёлкнул «{name}» в {app}.";
    }

    private static string? RunWithTimeout(Func<string> work)
    {
        // UI Automation в тяжёлых окнах (Electron) бывает медленным — не вешаем остров дольше 8 секунд
        Task<string> task = Task.Run(work);
        // если не дождались — ошибку из фона всё равно «забираем», иначе она всплывёт в журнале как необработанная
        task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        try
        {
            return task.Wait(TimeSpan.FromSeconds(8)) ? task.Result : null;
        }
        catch (AggregateException ex)
        {
            App.LogError(ex.InnerException ?? ex);
            return "Окно программы изменилось, пока я искал кнопку. Попробуйте ещё раз.";
        }
    }

    // ---------- мышь и клавиатура ----------

    private static void ClickAt(int x, int y)
    {
        GetCursorPos(out POINT old);
        SetCursorPos(x, y);
        Thread.Sleep(40);
        mouse_event(0x0002, 0, 0, 0, IntPtr.Zero); // левая вниз
        mouse_event(0x0004, 0, 0, 0, IntPtr.Zero); // левая вверх
        Thread.Sleep(60);
        SetCursorPos(old.X, old.Y);
    }

    private static readonly Dictionary<string, byte> KeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D, ["space"] = 0x20, ["пробел"] = 0x20, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["playpause"] = 0xB3, ["next"] = 0xB0, ["prev"] = 0xB1, ["mute"] = 0xAD, ["volumeup"] = 0xAF, ["volumedown"] = 0xAE,
    };

    private static bool SendCombo(string combo)
    {
        var codes = new List<byte>();
        foreach (string part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part.Trim();
            if (KeyCodes.TryGetValue(p, out byte code)) codes.Add(code);
            else if (p.Length == 1 && char.IsLetterOrDigit(p[0]) && p[0] < 128) codes.Add((byte)char.ToUpperInvariant(p[0]));
            else if (p.Length is 2 or 3 && (p[0] is 'f' or 'F') && int.TryParse(p[1..], out int f) && f is >= 1 and <= 24) codes.Add((byte)(0x6F + f));
            else return false;
        }
        foreach (byte c in codes) keybd_event(c, 0, 0, IntPtr.Zero);
        for (int i = codes.Count - 1; i >= 0; i--) keybd_event(codes[i], 0, 2, IntPtr.Zero);
        return true;
    }

    private static void SendChar(char c)
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1;
        inputs[0].ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 }; // KEYEVENTF_UNICODE
        inputs[1].type = 1;
        inputs[1].ki = new KEYBDINPUT { wScan = c, dwFlags = 0x0004 | 0x0002 };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
        Thread.Sleep(5);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] private MOUSEINPUT _mouse; // задаёт полный размер структуры (иначе SendInput её не примет)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}

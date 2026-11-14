using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WikarIsland.Services;

/// <summary>
/// Быстрая строка: «таймер 5 мин», «напомни в 18:30 позвонить», «выключи через час», «скрин», «vpn», «переведи …».
/// Всё, что не похоже на команду, уходит вопросом в ИИ-чат.
/// </summary>
public static class CommandService
{
    public static readonly (string example, string description)[] Examples =
    {
        ("таймер 10 мин чай", "таймер с подписью"),
        ("помидор", "Pomodoro: 25 мин работы, 5 отдыха"),
        ("напомни в 18:30 позвонить маме", "напоминание на время"),
        ("напомни через 20 мин выключить духовку", "напоминание через…"),
        ("напомни каждый день в 9:00 таблетки", "повторяющееся"),
        ("выключи через 1 ч", "таймер выключения ПК"),
        ("скрин / скрин весь", "скриншот области или экрана"),
        ("заметка купить молоко", "новая заметка"),
        ("громкость 30", "громкость системы"),
        ("vpn · zapret · не беспокоить", "включить / выключить"),
        ("открой discord", "ярлык или программа"),
        ("переведи hello world", "DeepL"),
        ("найди погода в Берлине", "поиск в браузере"),
        ("любой вопрос", "спросить ИИ"),
    };

    /// <summary>Выполнить команду. Возвращает текст-результат для подсказки в строке (или null, если строку можно закрыть).</summary>
    public static string? Execute(string input)
    {
        string text = input.Trim();
        if (text.Length == 0) return null;
        string lower = text.ToLowerInvariant();
        Match m;

        // --- таймеры ---
        if ((m = Regex.Match(lower, @"^(таймер|timer)\s+(.+)$")).Success)
        {
            if (!TryParseDuration(m.Groups[2].Value, out TimeSpan d, out string rest)) return "Не понял время. Пример: таймер 10 мин";
            App.FocusTimer.Start(d, Capitalize(rest));
            return null;
        }
        if (Regex.IsMatch(lower, @"^(помидор|pomodoro|фокус)$"))
        {
            App.FocusTimer.StartPomodoro();
            return null;
        }
        if (Regex.IsMatch(lower, @"^(стоп|stop|отмена)( таймер)?$"))
        {
            App.FocusTimer.Stop();
            return null;
        }
        if (Regex.IsMatch(lower, @"^(пауза|pause)$"))
        {
            App.FocusTimer.TogglePause();
            return null;
        }

        // --- напоминания ---
        if ((m = Regex.Match(text, @"^напомн\w*\s+(.+)$", RegexOptions.IgnoreCase)).Success)
        {
            if (!TryParseReminder(m.Groups[1].Value, out DateTime due, out string what, out string repeat))
                return "Не понял время. Примеры: «напомни в 18:30 …», «напомни через 20 мин …», «напомни завтра в 9 …»";
            App.Reminders.Add(what.Length > 0 ? Capitalize(what) : "Напоминание", due, repeat);
            App.Pill.ShowNotification(new NotificationItem
            {
                AppId = "wikar.reminder",
                AppName = "Напоминание",
                Title = "Напомню " + FormatDue(due),
                Body = what.Length > 0 ? Capitalize(what) : "Напоминание",
                Time = DateTime.Now,
            });
            return null;
        }

        // --- питание ---
        if (Regex.IsMatch(lower, @"^(отмена выключения|не выключай|отмени выключение)$"))
        {
            App.PowerTimer.Cancel();
            return null;
        }
        if ((m = Regex.Match(lower, @"^(выключи\w*|перезагру\w*|сон|спящий режим|гибернац\w*)(?:\s+(?:пк|компьютер))?\s+через\s+(.+)$")).Success)
        {
            if (!TryParseDuration(m.Groups[2].Value, out TimeSpan d, out _)) return "Не понял время. Пример: выключи через 30 мин";
            string verb = m.Groups[1].Value;
            PowerAction action = verb.StartsWith("перезагру") ? PowerAction.Restart
                : verb.StartsWith("гибернац") ? PowerAction.Hibernate
                : verb.StartsWith("сон") || verb.StartsWith("спящ") ? PowerAction.Sleep
                : PowerAction.Shutdown;
            App.PowerTimer.StartIn(d, action);
            return null;
        }

        // --- скриншоты ---
        if ((m = Regex.Match(lower, @"^(скрин|скриншот|screenshot)(\s+.+)?$")).Success)
        {
            App.Panel.Screenshot(full: m.Groups[2].Success && Regex.IsMatch(m.Groups[2].Value, "вес|экран|полн|full"));
            return null;
        }

        // --- переключатели ---
        if ((m = Regex.Match(lower, @"^(vpn|впн)(\s+(вкл|выкл|on|off))?$")).Success)
        {
            bool? want = OnOff(m.Groups[3].Value);
            if (want == null || want != App.Vpn.IsConnected) _ = App.Vpn.ToggleAsync();
            return null;
        }
        if ((m = Regex.Match(lower, @"^(zapret|запрет)(\s+(вкл|выкл|on|off))?$")).Success)
        {
            bool? want = OnOff(m.Groups[3].Value);
            if (want == null || want != App.Zapret.IsRunning) _ = App.Zapret.ToggleAsync();
            return null;
        }
        if ((m = Regex.Match(lower, @"^(не беспокоить|dnd)(\s+(вкл|выкл|on|off))?$")).Success)
        {
            bool? want = OnOff(m.Groups[3].Value);
            App.SetDnd(want ?? !App.Settings.DndManual);
            return null;
        }

        // --- громкость ---
        if ((m = Regex.Match(lower, @"^(громкость|звук|volume)\s+(\d{1,3})%?$")).Success)
        {
            App.Audio.Volume = Math.Clamp(int.Parse(m.Groups[2].Value), 0, 100) / 100f;
            App.Panel.RefreshSound();
            return null;
        }

        // --- заметка ---
        if ((m = Regex.Match(text, @"^(заметка|запиши|note)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            Note note = App.Notes.Create();
            note.Text = m.Groups[2].Value.Trim();
            note.Updated = DateTime.Now;
            App.Notes.Save();
            App.Pill.ShowNotification(new NotificationItem
            {
                AppId = "wikar.note",
                AppName = "Заметки",
                Title = "Заметка сохранена",
                Body = note.Text,
                Time = DateTime.Now,
            });
            return null;
        }

        // --- программы и сайты ---
        if ((m = Regex.Match(text, @"^(открой|запусти|open)\s+(.+)$", RegexOptions.IgnoreCase)).Success)
        {
            string name = m.Groups[2].Value.Trim();
            LauncherItem? item = App.Settings.Launcher.FirstOrDefault(l => l.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (item != null) ShellHelper.Open(item.Path);
            else if (Regex.IsMatch(name, @"^[\w-]+(\.[\w-]+)+(/.*)?$")) OpenUrl("https://" + name);
            else
            {
                try { Process.Start(new ProcessStartInfo(name) { UseShellExecute = true }); }
                catch { return $"Не нашёл «{name}». Добавь программу в ярлыки на «Главной»"; }
            }
            return null;
        }
        if ((m = Regex.Match(text, @"^(найди|гугл|google|поиск)\s+(.+)$", RegexOptions.IgnoreCase)).Success)
        {
            OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(m.Groups[2].Value.Trim()));
            return null;
        }

        // --- ИИ ---
        if ((m = Regex.Match(text, @"^(переведи|перевод|translate)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            App.Panel.AskAi("deepl", m.Groups[2].Value.Trim());
            return null;
        }
        if ((m = Regex.Match(text, @"^(gpt|chatgpt|чатгпт)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            App.Panel.AskAi("chatgpt", m.Groups[2].Value.Trim());
            return null;
        }
        if ((m = Regex.Match(text, @"^(claude|клод)\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            App.Panel.AskAi("claude", m.Groups[2].Value.Trim());
            return null;
        }

        App.Panel.AskAi(App.Settings.CommandAi, text);
        return null;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.LogError(ex); }
    }

    private static bool? OnOff(string s) => s switch
    {
        "вкл" or "on" => true,
        "выкл" or "off" => false,
        _ => null,
    };

    private static string Capitalize(string s)
    {
        s = s.Trim();
        return s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
    }

    // ---------- разбор времени ----------

    private const string UnitPattern = @"(секунд\w*|сек|с|s|минут\w*|мин|м|m|час\w*|ч|h)";

    /// <summary>«10», «10 мин», «1,5 часа», «полчаса», «час», «1:30» (мин:сек). Остаток строки — подпись.</summary>
    public static bool TryParseDuration(string input, out TimeSpan duration, out string rest)
    {
        string s = input.Trim().ToLowerInvariant();
        duration = TimeSpan.Zero;
        rest = "";
        Match m;
        if ((m = Regex.Match(s, @"^полчаса\b\s*(.*)$")).Success)
        {
            duration = TimeSpan.FromMinutes(30);
            rest = m.Groups[1].Value;
            return true;
        }
        if ((m = Regex.Match(s, @"^час\b\s*(.*)$")).Success)
        {
            duration = TimeSpan.FromHours(1);
            rest = m.Groups[1].Value;
            return true;
        }
        if ((m = Regex.Match(s, @"^(\d{1,3}):(\d{2})\b\s*(.*)$")).Success)
        {
            duration = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) + TimeSpan.FromSeconds(int.Parse(m.Groups[2].Value));
            rest = m.Groups[3].Value;
            return duration > TimeSpan.Zero;
        }
        // одна или несколько пар «число единица»: «1 ч 30 мин»
        var total = TimeSpan.Zero;
        int pos = 0;
        var pair = new Regex(@"\G\s*(\d+(?:[.,]\d+)?)\s*" + UnitPattern + @"?(?=\s|$)");
        while ((m = pair.Match(s, pos)).Success && m.Length > 0)
        {
            double n = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            string unit = m.Groups[2].Value;
            total += unit.StartsWith('с') || unit == "s" ? TimeSpan.FromSeconds(n)
                : unit.StartsWith('ч') || unit == "h" ? TimeSpan.FromHours(n)
                : TimeSpan.FromMinutes(n); // без единицы — минуты
            pos = m.Index + m.Length;
        }
        if (total <= TimeSpan.Zero) return false;
        duration = total;
        rest = s[pos..].Trim();
        return true;
    }

    /// <summary>«в 18:30 …», «через 20 мин …», «завтра в 9 …», «каждый день в 9:00 …», «по будням в 8 …».</summary>
    public static bool TryParseReminder(string input, out DateTime due, out string text, out string repeat)
    {
        string s = input.Trim();
        due = default;
        text = "";
        repeat = "none";
        Match m;

        if ((m = Regex.Match(s, @"^(каждый день|ежедневно|по будням|каждую неделю|еженедельно)\s+", RegexOptions.IgnoreCase)).Success)
        {
            string r = m.Groups[1].Value.ToLowerInvariant();
            repeat = r.Contains("будн") ? "weekdays" : r.Contains("недел") ? "weekly" : "daily";
            s = s[m.Length..];
        }

        if ((m = Regex.Match(s, @"^через\s+(.+)$", RegexOptions.IgnoreCase)).Success)
        {
            if (!TryParseDuration(m.Groups[1].Value, out TimeSpan d, out _)) return false;
            // текст берём из исходной строки (с регистром), отрезав часть со временем
            string original = m.Groups[1].Value;
            TryParseDuration(original, out _, out string restLower);
            text = original.Length >= restLower.Length ? original[^restLower.Length..].Trim() : restLower;
            due = DateTime.Now + d;
            return true;
        }

        DateTime day = DateTime.Today;
        bool explicitDay = false;
        if ((m = Regex.Match(s, @"^(сегодня|завтра|послезавтра)\s+", RegexOptions.IgnoreCase)).Success)
        {
            string w = m.Groups[1].Value.ToLowerInvariant();
            day = w == "завтра" ? day.AddDays(1) : w == "послезавтра" ? day.AddDays(2) : day;
            explicitDay = true;
            s = s[m.Length..];
        }

        if ((m = Regex.Match(s, @"^(?:в\s+)?(\d{1,2})(?:[:.](\d{2}))?(?:\s*(?:ч|часов|часа|час))?(?=\s|$)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)).Success)
        {
            int h = int.Parse(m.Groups[1].Value);
            int min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            if (h > 23 || min > 59) return false;
            due = day.AddHours(h).AddMinutes(min);
            if (!explicitDay && due <= DateTime.Now) due = due.AddDays(1);
            if (repeat == "weekdays")
                while (due.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) due = due.AddDays(1);
            text = m.Groups[3].Value.Trim();
            return true;
        }
        return false;
    }

    public static string FormatDue(DateTime due)
    {
        string time = due.ToString("HH:mm");
        if (due.Date == DateTime.Today) return $"сегодня в {time}";
        if (due.Date == DateTime.Today.AddDays(1)) return $"завтра в {time}";
        return due.ToString("d MMMM в HH:mm", new CultureInfo("ru-RU"));
    }
}

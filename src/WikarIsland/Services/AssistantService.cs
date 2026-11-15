using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <param name="Text">Что сказать (пусто — промолчать).</param>
/// <param name="ExpectReply">Ждём ответа без слова «Капсулка» (подтверждение).</param>
/// <param name="Silent">Не ждать продолжения разговора («стоп», «спасибо»).</param>
public sealed record AssistantReply(string Text, bool ExpectReply = false, bool Silent = false);

public sealed record AiPreset(string Key, string Name, string BaseUrl, string Model, string KeyUrl);

/// <summary>
/// Мозг Капсулки. Сначала быстрые команды прямо на компьютере (громкость, музыка, VPN, таймеры, запуск игр,
/// поиск), остальное — ИИ через OpenAI-совместимый API (Groq, Gemini, OpenRouter, Ollama…) с инструментами,
/// чтобы ИИ тоже мог управлять ПК. Опасное (выключение, перезагрузка) — только после «да».
/// </summary>
public sealed class AssistantService
{
    public static readonly AiPreset[] Presets =
    {
        new("groq", "Groq (бесплатно, быстро)", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", "https://console.groq.com/keys"),
        new("gemini", "Google Gemini (бесплатный ключ)", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", "https://aistudio.google.com/apikey"),
        new("openrouter", "OpenRouter (бесплатные модели)", "https://openrouter.ai/api/v1", "meta-llama/llama-3.3-70b-instruct:free", "https://openrouter.ai/keys"),
        new("ollama", "Ollama (на этом ПК, без ключа)", "http://localhost:11434/v1", "qwen2.5:7b", "https://ollama.com/download"),
        new("custom", "Свой адрес (OpenAI-совместимый)", "", "", ""),
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Сайты, которые открываем по имени.</summary>
    private static readonly Dictionary<string, string> Sites = new()
    {
        ["ютуб"] = "https://www.youtube.com", ["youtube"] = "https://www.youtube.com",
        ["гугл"] = "https://www.google.com", ["google"] = "https://www.google.com",
        ["вк"] = "https://vk.com", ["вконтакте"] = "https://vk.com", ["почту"] = "https://mail.google.com", ["почта"] = "https://mail.google.com",
        ["гмейл"] = "https://mail.google.com", ["твич"] = "https://www.twitch.tv", ["гитхаб"] = "https://github.com",
        ["яндекс"] = "https://ya.ru", ["кинопоиск"] = "https://www.kinopoisk.ru", ["нетфликс"] = "https://www.netflix.com",
        ["википедию"] = "https://ru.wikipedia.org", ["википедия"] = "https://ru.wikipedia.org",
        ["чат гпт"] = "https://chatgpt.com", ["чатгпт"] = "https://chatgpt.com", ["переводчик"] = "https://www.deepl.com/translator",
        ["карты"] = "https://maps.google.com", ["авито"] = "https://www.avito.ru", ["озон"] = "https://www.ozon.ru",
        ["вайлдберриз"] = "https://www.wildberries.ru", ["амазон"] = "https://www.amazon.de", ["реддит"] = "https://www.reddit.com",
    };

    /// <summary>Русские названия программ Windows и частые сокращения игр.</summary>
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["блокнот"] = "notepad.exe", ["калькулятор"] = "calc.exe", ["проводник"] = "explorer.exe",
        ["диспетчер задач"] = "taskmgr.exe", ["панель управления"] = "control.exe", ["параметры"] = "ms-settings:",
        ["настройки windows"] = "ms-settings:", ["командную строку"] = "cmd.exe", ["терминал"] = "wt.exe", ["пейнт"] = "mspaint.exe",
        ["ведьмак"] = "Witcher", ["ведьмака"] = "Witcher", ["гта"] = "Grand Theft Auto", ["кс"] = "Counter-Strike",
        ["контру"] = "Counter-Strike", ["контра"] = "Counter-Strike", ["варфейс"] = "Warface", ["танки"] = "World of Tanks",
        ["хром"] = "Chrome", ["браузер"] = "", // браузер открываем отдельно
    };

    private Func<string>? _pending;      // действие, ждущее «да»
    private DateTime _pendingUntil;
    private readonly List<JsonObject> _history = new();
    private DateTime _lastTalk;

    public static AiPreset Preset => Presets.FirstOrDefault(p => p.Key == App.Settings.AiPreset) ?? Presets[0];
    public static bool AiConfigured => App.Settings.AiBaseUrl.Length > 0 && App.Settings.AiModel.Length > 0
                                       && (App.Settings.AiKey.Length > 0 || App.Settings.AiPreset == "ollama");

    public async Task<AssistantReply> HandleAsync(string spoken)
    {
        string text = Regex.Replace(spoken.ToLowerInvariant().Replace('ё', 'е'), @"[^\w\s:,.\-]", " ").Trim();
        text = Regex.Replace(text, @"\s+", " ");
        if (text.Length == 0) return new AssistantReply("");

        // ответ на «вы уверены?»
        if (_pending != null)
        {
            Func<string> action = _pending;
            _pending = null;
            if (DateTime.Now <= _pendingUntil && Regex.IsMatch(text, @"^(да|да да|конечно|подтверждаю|давай|выполняй|ага|угу|так точно)\b"))
                return new AssistantReply(action(), Silent: true);
            if (DateTime.Now <= _pendingUntil) return new AssistantReply("Отменил, сэр.", Silent: true);
        }

        AssistantReply? local = Local(text);
        if (local != null) return local;

        if (AiConfigured) return await AskAiAsync(spoken);

        // мозг не подключён — задаём вопрос в веб-чате, где уже есть вход в аккаунт
        App.Panel.AskAi(App.Settings.CommandAi, spoken);
        return new AssistantReply(App.Settings.CommandAi == "claude"
            ? "Спрашиваю у Claude, ответ на экране. Чтобы я отвечал голосом, подключите ИИ в настройках."
            : "Спрашиваю у ChatGPT, ответ на экране. Чтобы я отвечал голосом, подключите ИИ в настройках.", Silent: true);
    }

    // ---------- быстрые команды ----------

    private AssistantReply? Local(string text)
    {
        string t = RuNumbers.Digitize(text);
        Match m;

        if (Regex.IsMatch(t, @"^(стоп|хватит|замолчи|молчи|отмена|тихо|забудь)$"))
            return new AssistantReply("", Silent: true);
        if (Regex.IsMatch(t, @"^(спасибо|благодарю|спасибо большое)\b"))
            return new AssistantReply("Всегда к вашим услугам, сэр.", Silent: true);
        if (Regex.IsMatch(t, @"^(привет|здравствуй|добрый (день|вечер)|доброе утро)\b") && t.Split(' ').Length <= 3)
            return new AssistantReply(Greeting());

        // --- время, дата, погода, железо ---
        if (Regex.IsMatch(t, @"(который час|сколько (сейчас )?времени|сколько время)"))
            return new AssistantReply($"Сейчас {DateTime.Now:HH:mm}.");
        if (Regex.IsMatch(t, @"(какое (сегодня )?число|какой (сегодня )?день|какая (сегодня )?дата)"))
            return new AssistantReply("Сегодня " + DateTime.Now.ToString("dddd, d MMMM", new CultureInfo("ru-RU")) + ".");
        if (Regex.IsMatch(t, @"^(какая |что с |как )?погод"))
            return new AssistantReply(WeatherText());
        if (Regex.IsMatch(t, @"(температур\w* (процессора|видеокарты|компьютера|железа)|нагрузк\w* (процессора|видеокарты|компьютера)|как (там )?(железо|компьютер))"))
            return new AssistantReply(SystemText());
        if (Regex.IsMatch(t, @"(что (сейчас )?играет|что за (песня|трек|музыка)|какая (это )?песня)"))
        {
            MediaInfo? mi = App.Media.Current;
            return new AssistantReply(mi == null ? "Сейчас ничего не играет." : $"{mi.Title}" + (mi.Artist.Length > 0 ? $", {mi.Artist}." : "."));
        }

        // --- звук ---
        if ((m = Regex.Match(t, @"(громкость|звук)( на)? (\d{1,3})")).Success)
        {
            App.Audio.Volume = Math.Clamp(int.Parse(m.Groups[3].Value), 0, 100) / 100f;
            App.Panel.RefreshSound();
            return new AssistantReply($"Громкость {m.Groups[3].Value}.", Silent: true);
        }
        if (Regex.IsMatch(t, @"(погромче|громче|прибавь( звук)?|сделай громче)"))
        {
            App.Audio.Step(0.1f);
            App.Panel.RefreshSound();
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"(потише|тише|убавь( звук)?|сделай тише)"))
        {
            App.Audio.Step(-0.1f);
            App.Panel.RefreshSound();
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(выключи звук|без звука|отключи звук|мьют)$"))
        {
            App.Audio.Muted = true;
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(включи звук|верни звук)$"))
        {
            App.Audio.Muted = false;
            return new AssistantReply("Звук включён.", Silent: true);
        }

        // --- музыка ---
        if (Regex.IsMatch(t, @"^(пауза|поставь на паузу|останови музыку|стоп музыка|выключи музыку)$"))
        {
            if (App.Media.Current?.IsPlaying == true) App.Media.PlayPause();
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(продолжи|играй|включи музыку|сними с паузы|продолжай)$"))
        {
            if (App.Media.Current is { IsPlaying: false }) App.Media.PlayPause();
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(следующ\w*( трек| песня| песню)?|дальше|переключи( трек)?|некст)$"))
        {
            App.Media.Next();
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(предыдущ\w*( трек| песня| песню)?|верни( трек)?|назад)$"))
        {
            App.Media.Previous();
            return new AssistantReply("", Silent: true);
        }

        // --- функции острова ---
        if ((m = Regex.Match(t, @"не беспокоить на (?:(\d+(?:,\d+)?) )?(минут\w*|час\w*)")).Success)
        {
            double n = m.Groups[1].Success ? double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : 1;
            App.SetDndFor(m.Groups[2].Value.StartsWith("час") ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n));
            return new AssistantReply("Не беспокою.", Silent: true);
        }
        if ((m = Regex.Match(t, @"^(включи|выключи|отключи|подключи)\s+(vpn|впн|ви пи эн|в п н|запрет|zapret|не беспокоить|режим не беспокоить)")).Success)
        {
            bool on = m.Groups[1].Value is "включи" or "подключи";
            string what = m.Groups[2].Value;
            if (what.Contains("беспоко"))
            {
                App.SetDnd(on);
                return new AssistantReply(on ? "Не беспокою." : "Режим «не беспокоить» выключен.", Silent: true);
            }
            if (what.Contains("запрет") || what == "zapret")
            {
                if (on != App.Zapret.IsRunning) _ = App.Zapret.ToggleAsync();
                return new AssistantReply(on ? "Включаю запрет." : "Запрет выключен.", Silent: true);
            }
            if (App.Settings.VpnServers.Count == 0) return new AssistantReply("Серверов VPN нет. Добавьте ссылку на вкладке VPN.");
            if (on != App.Vpn.IsConnected) _ = App.Vpn.ToggleAsync();
            return new AssistantReply(on ? "Подключаю VPN." : "VPN отключён.", Silent: true);
        }
        if (Regex.IsMatch(t, @"(сделай )?(скрин|скриншот)"))
        {
            App.Panel.Screenshot(full: Regex.IsMatch(t, "(весь|всего|экран)"));
            return new AssistantReply("", Silent: true);
        }
        if ((m = Regex.Match(t, @"(?:поставь |заведи |запусти )?таймер на (.+)$")).Success)
        {
            if (!CommandService.TryParseDuration(UnitsToShort(m.Groups[1].Value), out TimeSpan d, out string rest))
                return new AssistantReply("Не расслышал время для таймера.");
            App.FocusTimer.Start(d, rest.Length > 0 ? char.ToUpper(rest[0]) + rest[1..] : "");
            return new AssistantReply($"Таймер на {Human(d)}.", Silent: true);
        }
        if ((m = Regex.Match(t, @"^напомни\w*\s+(.+)$")).Success)
        {
            string when = UnitsToShort(Regex.Replace(m.Groups[1].Value, @"\bв (\d{1,2}) (\d{2})\b", "в $1:$2"));
            if (!CommandService.TryParseReminder(when, out DateTime due, out string what, out string repeat))
                return new AssistantReply("Не понял, когда напомнить. Скажите, например: напомни через двадцать минут проверить духовку.");
            App.Reminders.Add(what.Length > 0 ? char.ToUpper(what[0]) + what[1..] : "Напоминание", due, repeat);
            return new AssistantReply($"Напомню {CommandService.FormatDue(due)}.", Silent: true);
        }
        if ((m = Regex.Match(t, @"^(запиши|заметка|запомни)\s+(.+)$")).Success)
        {
            Note note = App.Notes.Create();
            note.Text = char.ToUpper(m.Groups[2].Value[0]) + m.Groups[2].Value[1..];
            note.Updated = DateTime.Now;
            App.Notes.Save();
            return new AssistantReply("Записал.", Silent: true);
        }
        if ((m = Regex.Match(t, @"^(открой|покажи)\s+(центр управления|настройки острова|настройки|заметки|буфер обмена|погоду|систему|впн|vpn|запрет)$")).Success)
        {
            string tab = m.Groups[2].Value switch
            {
                "центр управления" => "control",
                "заметки" => "home",
                "буфер обмена" => "clipboard",
                "погоду" => "weather",
                "систему" => "system",
                "впн" or "vpn" => "vpn",
                "запрет" => "zapret",
                _ => "settings",
            };
            App.Panel.ShowTab(tab, activate: true);
            return new AssistantReply("", Silent: true);
        }

        // --- питание ---
        if ((m = Regex.Match(t, @"^(выключи|перезагрузи|усыпи)\s*(компьютер|комп|пк)?\s+через\s+(.+)$")).Success
            && CommandService.TryParseDuration(UnitsToShort(m.Groups[3].Value), out TimeSpan delay, out _))
        {
            PowerAction a = m.Groups[1].Value == "перезагрузи" ? PowerAction.Restart : m.Groups[1].Value == "усыпи" ? PowerAction.Sleep : PowerAction.Shutdown;
            App.PowerTimer.StartIn(delay, a);
            return new AssistantReply($"{PowerTimerService.ActionName(a)} через {Human(delay)}. Отменить можно в капсуле.", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(выключи (компьютер|комп|пк)|выключение компьютера|заверши работу)$"))
            return Confirm("Выключить компьютер, сэр?", () => { PowerTimerService.Execute(PowerAction.Shutdown); return ""; });
        if (Regex.IsMatch(t, @"^(перезагрузи (компьютер|комп|пк)|перезагрузка|перезагрузись)$"))
            return Confirm("Перезагрузить компьютер?", () => { PowerTimerService.Execute(PowerAction.Restart); return ""; });
        if (Regex.IsMatch(t, @"^(спящий режим|усни|усыпи компьютер|в сон)$"))
        {
            PowerTimerService.Execute(PowerAction.Sleep);
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(заблокируй( компьютер| экран)?|блокировка)$"))
        {
            LockWorkStation();
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(отмени выключение|не выключай|отмена выключения)$"))
        {
            App.PowerTimer.Cancel();
            return new AssistantReply("Выключение отменено.", Silent: true);
        }

        // --- поиск и сайты ---
        if ((m = Regex.Match(t, @"^(?:найди|поищи|включи|открой|покажи)\s+(?:на|в)\s+(?:ютубе|ютюбе|youtube)\s+(.+)$")).Success)
        {
            AppIndex.OpenUri("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(m.Groups[1].Value));
            return new AssistantReply("Ищу на ютубе.", Silent: true);
        }
        if ((m = Regex.Match(t, @"^(?:найди|поищи|загугли|погугли|гугл)\s+(?:в интернете\s+|в гугле\s+|в браузере\s+)?(.+)$")).Success)
        {
            AppIndex.OpenUri("https://www.google.com/search?q=" + Uri.EscapeDataString(m.Groups[1].Value));
            return new AssistantReply("Ищу.", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(открой|запусти|включи) (браузер|интернет)$"))
        {
            AppIndex.OpenUri("https://www.google.com");
            return new AssistantReply("", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(закрой|закрыть) (окно|это|программу|вкладку)$"))
        {
            CloseForeground(t.Contains("вкладк"));
            return new AssistantReply("", Silent: true);
        }

        // --- запуск программ, игр и сайтов ---
        if ((m = Regex.Match(t, @"^(?:открой|запусти|включи|поиграем в|давай поиграем в|хочу поиграть в)\s+(?:игру\s+|программу\s+|сайт\s+)?(.+)$")).Success)
        {
            string name = m.Groups[1].Value.Trim();
            if (Sites.TryGetValue(name, out string? url))
            {
                AppIndex.OpenUri(url);
                return new AssistantReply("", Silent: true);
            }
            if (Aliases.TryGetValue(name, out string? alias) && alias.Length > 0)
            {
                if (alias.EndsWith(".exe", StringComparison.Ordinal) || alias.EndsWith(":", StringComparison.Ordinal))
                {
                    AppIndex.OpenUri(alias);
                    return new AssistantReply("", Silent: true);
                }
                name = alias;
            }
            (AppEntry? entry, double score) = AppIndex.Find(name);
            if (entry != null && score >= 0.7)
            {
                entry.Launch();
                return new AssistantReply(entry.Kind is "Steam" or "Epic" ? $"Запускаю {entry.Name}. Приятной игры, сэр." : $"Открываю {entry.Name}.", Silent: true);
            }
            if (!AiConfigured) return new AssistantReply($"Не нашёл «{name}» среди игр и программ.");
            return null; // пусть ИИ угадает по списку игр
        }

        return null;
    }

    private AssistantReply Confirm(string question, Func<string> action)
    {
        _pending = action;
        _pendingUntil = DateTime.Now.AddSeconds(15);
        return new AssistantReply(question + " Скажите «да».", ExpectReply: true);
    }

    private static string Greeting()
    {
        int h = DateTime.Now.Hour;
        string part = h < 5 ? "Доброй ночи" : h < 12 ? "Доброе утро" : h < 18 ? "Добрый день" : "Добрый вечер";
        return $"{part}, сэр. Чем могу помочь?";
    }

    /// <summary>«5 минут» → «5 мин», «2 часа» → «2 ч»: так понимает разбор времени быстрой строки.</summary>
    private static string UnitsToShort(string s) =>
        Regex.Replace(Regex.Replace(Regex.Replace(s, @"\b(\d+(?:,\d+)?) минут\w*", "$1 мин"), @"\b(\d+(?:,\d+)?) час\w*", "$1 ч"), @"\b(\d+) секунд\w*", "$1 сек")
            .Replace("через час", "через 1 ч").Replace("на час", "на 1 ч");

    private static string Human(TimeSpan d) =>
        d.TotalHours >= 1 ? (d.Minutes > 0 ? $"{(int)d.TotalHours} ч {d.Minutes} мин" : $"{(int)d.TotalHours} ч")
        : d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} мин" : $"{d.Seconds} сек";

    private static string WeatherText()
    {
        WeatherData? w = App.Weather.Current;
        if (w == null) return "Погода пока не загружена. Выберите город в настройках.";
        string text = $"{w.City}: {w.TempText}, {w.Text.ToLowerInvariant()}";
        if (Math.Abs(w.Feels - w.Temp) >= 3) text += $", ощущается как {Math.Round(w.Feels):0}°";
        DayForecast? today = w.Days.FirstOrDefault();
        if (today != null) text += $". Днём до {Math.Round(today.Max):0}°";
        return text + ".";
    }

    private static string SystemText()
    {
        SystemSnapshot? s = App.SystemMonitor.Current;
        if (s == null)
        {
            App.SystemMonitor.Start("voice");
            _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => App.SystemMonitor.Stop("voice"));
            return "Секунду, датчики просыпаются. Спросите ещё раз.";
        }
        var parts = new List<string>();
        if (s.Cpu != null) parts.Add($"процессор {s.Cpu.Load:0}%" + (s.Cpu.Temperature is double ct ? $", {ct:0} градусов" : ""));
        if (s.Gpu != null) parts.Add($"видеокарта {s.Gpu.Load:0}%" + (s.Gpu.Temperature is double gt ? $", {gt:0} градусов" : ""));
        if (s.RamTotalGb > 0) parts.Add($"память {s.RamUsedGb:0.0} из {s.RamTotalGb:0} гигабайт");
        return parts.Count == 0 ? "Датчики молчат." : char.ToUpper(parts[0][0]) + string.Join("; ", parts)[1..] + ".";
    }

    // ---------- ИИ ----------

    private static string SystemPrompt()
    {
        string games = string.Join(", ", AppIndex.GameNames.Take(150));
        return
            "Ты — Капсулка, голосовой ассистент на Windows-компьютере пользователя, в духе Джарвиса из «Железного человека»: " +
            "спокойный, вежливый, с лёгкой иронией, обращаешься к пользователю «сэр». " +
            "Отвечай по-русски и КОРОТКО — одно-два предложения, не больше 40 слов: ответ озвучивается голосом. " +
            "Без markdown, списков, ссылок и эмодзи; числа и единицы пиши так, как их произносят. " +
            "Речь пользователя распознаётся автоматически и может быть искажена — угадывай смысл, особенно названия игр и программ. " +
            "Если просят что-то сделать на компьютере — вызывай подходящий инструмент, а потом коротко подтверди. " +
            "Выключение и перезагрузку без задержки делай только через инструмент power — он сам спросит подтверждение. " +
            $"Сейчас {DateTime.Now:dddd, d MMMM yyyy, HH:mm}. " +
            (games.Length > 0 ? $"Установленные игры: {games}." : "");
    }

    private static JsonArray Tools()
    {
        JsonObject Fn(string name, string description, JsonObject properties, params string[] required) => new()
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
                },
            },
        };
        JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
        JsonObject Num(string d) => new() { ["type"] = "number", ["description"] = d };
        JsonObject Bool(string d) => new() { ["type"] = "boolean", ["description"] = d };
        JsonObject Enum(string d, params string[] values) => new()
        {
            ["type"] = "string", ["description"] = d, ["enum"] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()),
        };

        return new JsonArray
        {
            Fn("open_app", "Запустить игру, программу или ярлык по названию (точное название из списка игр, если подходит).",
                new JsonObject { ["name"] = Str("Название игры или программы") }, "name"),
            Fn("open_url", "Открыть сайт в браузере.", new JsonObject { ["url"] = Str("Полный адрес https://…") }, "url"),
            Fn("web_search", "Поиск в интернете или на YouTube.",
                new JsonObject { ["query"] = Str("Запрос"), ["site"] = Enum("Где искать", "google", "youtube") }, "query"),
            Fn("set_volume", "Громкость системы: точное значение или изменение.",
                new JsonObject { ["percent"] = Num("0–100"), ["change"] = Num("Изменить на столько процентов, например -10") }),
            Fn("media", "Управление музыкой и видео.", new JsonObject { ["action"] = Enum("Действие", "play", "pause", "next", "previous") }, "action"),
            Fn("toggle", "Включить или выключить функцию острова.",
                new JsonObject { ["feature"] = Enum("Что", "vpn", "zapret", "dnd"), ["on"] = Bool("Включить?") }, "feature", "on"),
            Fn("timer", "Таймер обратного отсчёта в капсуле.",
                new JsonObject { ["minutes"] = Num("Минут"), ["label"] = Str("Подпись") }, "minutes"),
            Fn("reminder", "Напоминание.",
                new JsonObject { ["text"] = Str("О чём напомнить"), ["at"] = Str("Время ЧЧ:ММ сегодня/завтра"), ["in_minutes"] = Num("Или через сколько минут") }, "text"),
            Fn("screenshot", "Скриншот.", new JsonObject { ["full"] = Bool("Весь экран (иначе выделить область)") }),
            Fn("note", "Сохранить заметку.", new JsonObject { ["text"] = Str("Текст") }, "text"),
            Fn("power", "Питание ПК. Без задержки выключение и перезагрузка требуют подтверждения пользователя.",
                new JsonObject { ["action"] = Enum("Действие", "shutdown", "restart", "sleep", "lock", "cancel"), ["delay_minutes"] = Num("Через сколько минут") }, "action"),
            Fn("system_info", "Загрузка и температура процессора, видеокарты, память.", new JsonObject()),
            Fn("weather", "Текущая погода в городе пользователя.", new JsonObject()),
            Fn("now_playing", "Что сейчас играет.", new JsonObject()),
        };
    }

    private string RunTool(string name, JsonObject args)
    {
        string S(string key) => args[key]?.ToString() ?? "";
        double? N(string key)
        {
            JsonNode? n = args[key];
            if (n == null) return null;
            try { return n.GetValue<double>(); }
            catch { return double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null; }
        }
        bool B(string key) => args[key]?.ToString().Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        switch (name)
        {
            case "open_app":
            {
                (AppEntry? e, double score) = AppIndex.Find(S("name"));
                if (e == null || score < 0.55) return $"Не найдено: {S("name")}";
                e.Launch();
                return $"Запущено: {e.Name}";
            }
            case "open_url":
                string url = S("url");
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
                AppIndex.OpenUri(url);
                return "Открыто";
            case "web_search":
                AppIndex.OpenUri(S("site") == "youtube"
                    ? "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(S("query"))
                    : "https://www.google.com/search?q=" + Uri.EscapeDataString(S("query")));
                return "Поиск открыт";
            case "set_volume":
                if (N("percent") is double p) App.Audio.Volume = (float)Math.Clamp(p / 100, 0, 1);
                else if (N("change") is double c) App.Audio.Step((float)(c / 100));
                App.Panel.RefreshSound();
                return $"Громкость {Math.Round(App.Audio.Volume * 100)}%";
            case "media":
                switch (S("action"))
                {
                    case "next": App.Media.Next(); break;
                    case "previous": App.Media.Previous(); break;
                    case "pause": if (App.Media.Current?.IsPlaying == true) App.Media.PlayPause(); break;
                    default: if (App.Media.Current?.IsPlaying != true) App.Media.PlayPause(); break;
                }
                return "Готово";
            case "toggle":
            {
                bool on = B("on");
                switch (S("feature"))
                {
                    case "dnd": App.SetDnd(on); break;
                    case "zapret": if (on != App.Zapret.IsRunning) _ = App.Zapret.ToggleAsync(); break;
                    default:
                        if (App.Settings.VpnServers.Count == 0) return "VPN не настроен: нет серверов";
                        if (on != App.Vpn.IsConnected) _ = App.Vpn.ToggleAsync();
                        break;
                }
                return on ? "Включено" : "Выключено";
            }
            case "timer":
                App.FocusTimer.Start(TimeSpan.FromMinutes(Math.Clamp(N("minutes") ?? 5, 0.1, 24 * 60)), S("label"));
                return "Таймер запущен";
            case "reminder":
            {
                DateTime due;
                if (N("in_minutes") is double mins) due = DateTime.Now.AddMinutes(mins);
                else if (TimeSpan.TryParse(S("at"), out TimeSpan at))
                {
                    due = DateTime.Today + at;
                    if (due <= DateTime.Now) due = due.AddDays(1);
                }
                else return "Не указано время";
                App.Reminders.Add(S("text"), due, "none");
                return "Напомню " + CommandService.FormatDue(due);
            }
            case "screenshot":
                App.Panel.Screenshot(full: B("full"));
                return "Готово";
            case "note":
            {
                Note note = App.Notes.Create();
                note.Text = S("text");
                note.Updated = DateTime.Now;
                App.Notes.Save();
                return "Заметка сохранена";
            }
            case "power":
            {
                string action = S("action");
                double delay = N("delay_minutes") ?? 0;
                if (action == "cancel")
                {
                    App.PowerTimer.Cancel();
                    return "Таймер выключения отменён";
                }
                if (action == "lock")
                {
                    LockWorkStation();
                    return "Заблокировано";
                }
                PowerAction a = action == "restart" ? PowerAction.Restart : action == "sleep" ? PowerAction.Sleep : PowerAction.Shutdown;
                if (delay > 0)
                {
                    App.PowerTimer.StartIn(TimeSpan.FromMinutes(delay), a);
                    return $"{PowerTimerService.ActionName(a)} через {delay:0} мин";
                }
                if (a == PowerAction.Sleep)
                {
                    PowerTimerService.Execute(a);
                    return "Сплю";
                }
                _pending = () => { PowerTimerService.Execute(a); return ""; };
                _pendingUntil = DateTime.Now.AddSeconds(15);
                _askedConfirmation = true;
                return "Нужно подтверждение: спроси пользователя одной короткой фразой и попроси сказать «да».";
            }
            case "system_info":
                return SystemText();
            case "weather":
                return WeatherText();
            case "now_playing":
                MediaInfo? mi = App.Media.Current;
                return mi == null ? "Ничего не играет" : $"{mi.Title} — {mi.Artist} ({(mi.IsPlaying ? "играет" : "пауза")})";
        }
        return "Нет такого инструмента";
    }

    private bool _askedConfirmation;

    private async Task<AssistantReply> AskAiAsync(string spoken)
    {
        if ((DateTime.Now - _lastTalk).TotalMinutes > 5) _history.Clear();
        _lastTalk = DateTime.Now;
        _askedConfirmation = false;

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() } };
        foreach (JsonObject h in _history) messages.Add(h.DeepClone());
        var user = new JsonObject { ["role"] = "user", ["content"] = spoken };
        messages.Add(user.DeepClone());

        string answer = "";
        try
        {
            for (int step = 0; step < 4; step++)
            {
                JsonObject message = await CompleteAsync(messages, tools: true);
                var calls = message["tool_calls"] as JsonArray;
                if (calls == null || calls.Count == 0)
                {
                    answer = message["content"]?.ToString() ?? "";
                    break;
                }

                // назад отдаём только то, что понимают все совместимые API
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = message["content"]?.ToString(),
                    ["tool_calls"] = calls.DeepClone(),
                });
                foreach (JsonNode? call in calls)
                {
                    string id = call?["id"]?.ToString() ?? "";
                    string fn = call?["function"]?["name"]?.ToString() ?? "";
                    JsonObject args;
                    try
                    {
                        args = JsonNode.Parse(call?["function"]?["arguments"]?.ToString() ?? "{}") as JsonObject ?? new JsonObject();
                    }
                    catch
                    {
                        args = new JsonObject();
                    }
                    string result;
                    try
                    {
                        result = RunTool(fn, args);
                    }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                        result = "Ошибка: " + ex.Message;
                    }
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
                }
            }
        }
        catch (HttpRequestException ex)
        {
            App.LogError(ex);
            return new AssistantReply(ex.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => "ИИ не принял ключ. Проверьте его в настройках, сэр.",
                System.Net.HttpStatusCode.TooManyRequests => "Бесплатный лимит ИИ на сейчас исчерпан. Попробуйте через минуту.",
                _ => "Не могу достучаться до ИИ. Проверьте интернет.",
            }, Silent: true);
        }
        catch (TaskCanceledException)
        {
            return new AssistantReply("ИИ слишком долго думает. Попробуйте ещё раз.", Silent: true);
        }

        answer = answer.Trim();
        if (answer.Length == 0) answer = "Готово, сэр.";
        _history.Add(user);
        _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
        while (_history.Count > 12) _history.RemoveAt(0);
        return new AssistantReply(answer, ExpectReply: _askedConfirmation);
    }

    private static async Task<JsonObject> CompleteAsync(JsonArray messages, bool tools)
    {
        var body = new JsonObject
        {
            ["model"] = App.Settings.AiModel,
            ["messages"] = messages.DeepClone(),
            ["temperature"] = 0.6,
            ["max_tokens"] = 400,
        };
        if (tools)
        {
            body["tools"] = Tools();
            body["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, App.Settings.AiBaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (App.Settings.AiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", App.Settings.AiKey);
        request.Headers.Add("HTTP-Referer", "https://github.com/Wikaar/Wikar-DynamicIsland"); // OpenRouter просит
        request.Headers.Add("X-Title", "Wikar Island");

        using HttpResponseMessage response = await Http.SendAsync(request);
        string json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            App.LogError(new InvalidOperationException($"ИИ ответил {(int)response.StatusCode}: {json[..Math.Min(json.Length, 500)]}"));
            throw new HttpRequestException(json, null, response.StatusCode);
        }
        JsonNode? root = JsonNode.Parse(json);
        return root?["choices"]?[0]?["message"] as JsonObject ?? new JsonObject { ["content"] = "" };
    }

    /// <summary>Кнопка «Проверить» в настройках.</summary>
    public static async Task<string> TestAsync()
    {
        try
        {
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "Отвечай по-русски одним коротким предложением." },
                new JsonObject { ["role"] = "user", ["content"] = "Скажи, что связь установлена." },
            };
            JsonObject m = await CompleteAsync(messages, tools: false);
            return "Работает: " + (m["content"]?.ToString().Trim() ?? "");
        }
        catch (HttpRequestException ex)
        {
            return ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? "Ключ не подошёл"
                : $"Ошибка: {(ex.StatusCode is { } c ? (int)c + " " : "")}{Short(ex.Message)}";
        }
        catch (Exception ex)
        {
            return "Ошибка: " + Short(ex.Message);
        }
    }

    private static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;

    // ---------- окна ----------

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    /// <summary>Закрыть окно на переднем плане (как крестик — программа сама спросит про сохранение) или вкладку (Ctrl+W).</summary>
    private static void CloseForeground(bool tab)
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero || NativeMethods.ForegroundIsOurs()) return;
        if (tab)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);       // Ctrl
            keybd_event(0x57, 0, 0, UIntPtr.Zero);       // W
            keybd_event(0x57, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
            return;
        }
        PostMessage(fg, 0x0010, IntPtr.Zero, IntPtr.Zero); // WM_CLOSE
    }
}

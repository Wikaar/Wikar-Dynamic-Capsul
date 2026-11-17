using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
/// <param name="EndTalk">Разговор окончен («спасибо», «хватит») — дальше снова ждём слово «Капсулка».</param>
/// <param name="Spoken">Ответ уже озвучен по ходу генерации (потоком) — повторно не говорить.</param>
public sealed record AssistantReply(string Text, bool ExpectReply = false, bool Silent = false, bool EndTalk = false, bool Spoken = false);

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
        new("groq", "Groq (бесплатно, быстро)", "https://api.groq.com/openai/v1", "openai/gpt-oss-120b", "https://console.groq.com/keys"),
        new("gemini", "Google Gemini (бесплатный ключ)", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.8-flash", "https://aistudio.google.com/apikey"),
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
        ["ведьмак"] = "Witcher", ["ведьмака"] = "Witcher", ["гта"] = "Grand Theft Auto", ["кс"] = "Counter-Strike", ["кс 2"] = "Counter-Strike 2", ["кс2"] = "Counter-Strike 2", ["кс два"] = "Counter-Strike 2",
        ["контру"] = "Counter-Strike", ["контра"] = "Counter-Strike", ["контр страйк"] = "Counter-Strike", ["каунтер страйк"] = "Counter-Strike",
        ["каунтер страйк 2"] = "Counter-Strike 2", ["контр страйк 2"] = "Counter-Strike 2", ["cs2"] = "Counter-Strike 2", ["cs 2"] = "Counter-Strike 2",
        ["ксго"] = "Counter-Strike", ["кс го"] = "Counter-Strike",
        ["контру"] = "Counter-Strike", ["контра"] = "Counter-Strike", ["варфейс"] = "Warface", ["танки"] = "World of Tanks",
        ["хром"] = "Chrome", ["браузер"] = "", // браузер открываем отдельно
        ["ножницы"] = "SnippingTool.exe", ["редактор реестра"] = "regedit.exe", ["реестр"] = "regedit.exe",
        ["диспетчер устройств"] = "devmgmt.msc", ["управление дисками"] = "diskmgmt.msc", ["службы"] = "services.msc",
        ["настройки звука"] = "ms-settings:sound", ["звук"] = "ms-settings:sound", ["блютуз"] = "ms-settings:bluetooth",
        ["bluetooth"] = "ms-settings:bluetooth", ["настройки экрана"] = "ms-settings:display", ["центр обновления"] = "ms-settings:windowsupdate",
        ["обновления windows"] = "ms-settings:windowsupdate", ["магазин"] = "ms-windows-store:", ["microsoft store"] = "ms-windows-store:",
        ["корзину"] = "shell:RecycleBinFolder", ["корзина"] = "shell:RecycleBinFolder", ["загрузки"] = "shell:Downloads",
        ["папку загрузки"] = "shell:Downloads", ["документы"] = "shell:Personal", ["мои документы"] = "shell:Personal",
        ["стим"] = "Steam", ["эпик"] = "Epic Games Launcher", ["эпик геймс"] = "Epic Games Launcher", ["телегу"] = "Telegram",
        ["телеграм"] = "Telegram", ["телеграмм"] = "Telegram", ["ворд"] = "Word", ["эксель"] = "Excel", ["повер поинт"] = "PowerPoint",
        ["фотошоп"] = "Photoshop", ["обс"] = "OBS Studio", ["вс код"] = "Visual Studio Code", ["вскод"] = "Visual Studio Code",
        ["яндекс музыку"] = "Яндекс Музыка", ["музыку яндекс"] = "Яндекс Музыка", ["ватсап"] = "WhatsApp", ["вотсап"] = "WhatsApp",
        ["вов"] = "World of Warcraft", ["варкрафт"] = "World of Warcraft", ["ворлд оф варкрафт"] = "World of Warcraft",
        ["вов классик"] = "World of Warcraft Classic", ["варкрафт классик"] = "World of Warcraft Classic",
        ["дьябло"] = "Diablo IV", ["диабло"] = "Diablo IV", ["дьябло 4"] = "Diablo IV", ["диабло 4"] = "Diablo IV",
        ["дьябло четыре"] = "Diablo IV", ["диабло четыре"] = "Diablo IV", ["дьябло 3"] = "Diablo III", ["диабло 3"] = "Diablo III",
        ["овервотч"] = "Overwatch 2", ["овервоч"] = "Overwatch 2", ["овер"] = "Overwatch 2", ["овервотч 2"] = "Overwatch 2",
        ["хартстоун"] = "Hearthstone", ["хартстон"] = "Hearthstone", ["хс"] = "Hearthstone",
        ["старкрафт"] = "StarCraft II", ["старкрафт 2"] = "StarCraft II", ["хотс"] = "Heroes of the Storm",
        ["батлнет"] = "Battle.net", ["батл нет"] = "Battle.net", ["близзард"] = "Battle.net",
        ["спотифай"] = "Spotify", ["дискорд"] = "Discord", ["вайбер"] = "Viber", ["зум"] = "Zoom", ["тимс"] = "Microsoft Teams",
    };

    private Func<string>? _pending;      // действие, ждущее «да»
    private DateTime _pendingUntil;
    private readonly List<JsonObject> _history = new();
    private DateTime _lastTalk;

    public static AiPreset Preset => Presets.FirstOrDefault(p => p.Key == App.Settings.AiPreset) ?? Presets[0];
    public static bool AiConfigured => App.Settings.AiBaseUrl.Length > 0 && App.Settings.AiModel.Length > 0
                                       && (App.Settings.AiKey.Length > 0 || App.Settings.AiPreset == "ollama");

    /// <param name="speak">Если задан — ответ ИИ отдаётся сюда по предложениям, пока ещё генерируется (голос начинает раньше).</param>
    public async Task<AssistantReply> HandleAsync(string spoken, Action<string>? speak = null)
    {
        string text = Regex.Replace(spoken.ToLowerInvariant().Replace('ё', 'е'), @"[^\w\s:,.\-]", " ").Trim();
        text = Regex.Replace(text, @"\s+", " ");
        // имя помощника в начале (Капсулка, Капсуль, Капсула, Капс, Capsule) и точки по краям — не часть команды
        text = Regex.Replace(text, @"^(?:к[ао][пб]\s?сул\w*|капс|capsul\w*|kapsul\w*|kapsel\w*)[\s,.:!\-]*", "");
        text = text.Trim('.', ',', ' ', ':', '-', '!');
        text = FixMishearing(text);
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

        App.LogVoice($"Команда: «{spoken}»");

        // музыка — до быстрых команд и с проверкой: говорим «играет …» только если медиапульт Windows это подтверждает
        if (ParseMusic(text) is { } music)
        {
            AppAutomation.MusicResult r = await AppAutomation.PlayMusicAsync(music.service, music.query);
            return new AssistantReply(r.Playing ? r.Message + " Приятного прослушивания, сэр." : "Не получилось, сэр: " + r.Message, Silent: true);
        }

        if (await FileCommandAsync(text) is { } files)
        {
            App.LogVoice($"  файлы → «{files.Text}»");
            return files;
        }

        AssistantReply? local = Local(text);
        if (local != null)
        {
            App.LogVoice($"  быстрая команда → «{local.Text}»");
            // как Джарвис: молча выполненная команда получает короткое подтверждение
            if (local.Text.Length == 0 && !local.EndTalk && App.Settings.VoiceAcknowledge) return local with { Text = Ack() };
            return local;
        }

        if (AiConfigured) return await AskAiAsync(spoken, speak);

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

        if (Regex.IsMatch(t, @"^(стоп|хватит|замолчи|молчи|отмена|тихо|забудь|отбой|все|все хватит|свободна|свободен)$"))
            return new AssistantReply("", Silent: true, EndTalk: true);
        if (Regex.IsMatch(t, @"^(спасибо|благодарю|спасибо большое|пока|до свидания|все спасибо)\b"))
            return new AssistantReply("Всегда к вашим услугам, сэр.", Silent: true, EndTalk: true);
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

        // --- корзина: системное подтверждение Капсула нажимает сама ---
        if (Regex.IsMatch(t, @"^((очисти|очистить|опустоши|почисти|вычисти|освободи)( мою| всю)? корзину|очистка корзины)$"))
        {
            string? question = RecycleBin.Question(out string empty);
            return question == null ? new AssistantReply(empty, Silent: true) : Confirm(question, RecycleBin.Empty);
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
        // --- память и «закрой всё, кроме …» (всегда с подтверждением) ---
        if (Regex.IsMatch(t, @"^(освободи|почисти|очисти|разгрузи|высвободи)\s+(?:мне\s+)?(оперативн\w+\s+)?(память|оперативку|озу|ram)\b|^(мало|не хватает)\s+(оперативной\s+)?памяти"))
        {
            ProcessCleanup.Plan? plan = ProcessCleanup.FreeMemoryPlan(out string nothing);
            if (plan == null) return new AssistantReply(nothing);
            return ConfirmPlan(plan);
        }
        if ((m = Regex.Match(t, @"^(?:закрой|закрыть|позакрывай)\s+(?:все|всё)(?:\s+(?:программы|окна|приложения|лишнее))?(?:\s*,?\s*(?:и|включая|вместе с)\s+игр\w*(?:\s+тоже)?)?(?:\s*,?\s*кроме\s+(.+))?$")).Success)
        {
            string except = m.Groups[1].Value;
            bool games = Regex.IsMatch(t, @"\b(и|включая|вместе с)\s+игр");
            except = Regex.Replace(except, @"\s*,?\s*(?:и|включая|вместе с)\s+игр\w*.*$", "");
            ProcessCleanup.Plan? plan = ProcessCleanup.CloseAllExceptPlan(ProcessCleanup.SplitNames(except), games, out string nothing);
            if (plan == null) return new AssistantReply(nothing);
            return ConfirmPlan(plan);
        }
        if (Regex.IsMatch(t, @"^(закрой|закрыть) (окно|это|программу|вкладку)$"))
        {
            CloseForeground(t.Contains("вкладк"));
            return new AssistantReply("", Silent: true);
        }

        // --- кнопки внутри программ: «нажми Играть в Battle.net», «включи Мою волну в Яндекс Музыке» ---
        if ((m = Regex.Match(t, @"^(?:нажми|кликни|щелкни|выбери|включи|поставь|запусти)\s+(?:кнопку\s+|пункт\s+)?(.+?)\s+(?:в|во|на)\s+(?:программе\s+|приложении\s+)?(.+)$")).Success)
        {
            string target = m.Groups[1].Value.Trim(), app = m.Groups[2].Value.Trim();
            if (WindowControl.Find(NameVariants(app), out _).Count > 0)
            {
                string done = AppAutomation.Click(app, target);
                bool ok = Regex.IsMatch(done, "^(Нажато|Переключено|Выбрано|Открыто|Щёлкнул)");
                if (ok) return new AssistantReply("", Silent: true);
                if (!AiConfigured) return new AssistantReply($"Не нашёл «{target}», сэр.", Silent: true);
                // не нашли сами — пусть ИИ посмотрит список кнопок и попробует иначе
            }
        }

        // --- что открыто на панели задач ---
        if (Regex.IsMatch(t, @"^(что|какие программы|какие окна|какие игры|что у меня)\s+(сейчас\s+)?(открыто|открыты|запущено|запущены|работает|на панели задач)|^что на панели задач"))
            return new AssistantReply(WindowControl.Describe(), Silent: true);

        // --- окна: свернуть, развернуть, закрыть программы и игры ---
        if (Regex.IsMatch(t, @"^((сверни|спрячь|убери) все( окна| программы| игры| приложения)?|покажи рабочий стол|рабочий стол)$"))
        {
            int n = WindowControl.MinimizeAll();
            return new AssistantReply(n == 0 ? "Всё уже свёрнуто." : "", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(разверни|верни|покажи|восстанови) все( окна| программы| игры| приложения)?( обратно)?$|^верни окна$"))
        {
            int n = WindowControl.RestoreAll();
            return new AssistantReply(n == 0 ? "Нечего возвращать, сэр." : "", Silent: true);
        }
        if (Regex.IsMatch(t, @"^(сверни|спрячь)( это| окно| это окно| игру| программу| приложение)?$"))
        {
            WindowControl.MinimizeForeground();
            return new AssistantReply("", Silent: true);
        }
        if ((m = Regex.Match(t, @"^(?:сверни|спрячь)\s+(?:игру\s+|программу\s+|приложение\s+|окно\s+)?(.+)$")).Success)
        {
            string name = m.Groups[1].Value.Trim();
            List<OpenWindow> found = WindowControl.Find(NameVariants(name), out _);
            if (found.Count == 0) return new AssistantReply($"Не вижу открытого «{name}».", Silent: true);
            WindowControl.Minimize(found);
            return new AssistantReply("", Silent: true);
        }
        if ((m = Regex.Match(t, @"^(?:разверни|переключись на|переключи на|перейди в|покажи окно|вернись в)\s+(?:игру\s+|программу\s+|приложение\s+|окно\s+)?(.+)$")).Success)
        {
            string name = m.Groups[1].Value.Trim();
            List<OpenWindow> found = WindowControl.Find(NameVariants(name), out _);
            if (found.Count == 0) return new AssistantReply($"Не вижу открытого «{name}».", Silent: true);
            WindowControl.Restore(found);
            return new AssistantReply("", Silent: true);
        }
        if ((m = Regex.Match(t, @"^(закрой|закрыть|выключи|выруби|заверши|выйди из|убей|останови)\s+(принудительно\s+)?(?:игру\s+|программу\s+|приложение\s+)?(.+?)(\s+принудительно)?$")).Success)
        {
            string verb = m.Groups[1].Value;
            string name = m.Groups[3].Value.Trim();
            bool force = verb == "убей" || m.Groups[2].Success || m.Groups[4].Success;
            // «закрой яндекс музыку и дискорд» — закрываем каждую
            List<string> several = ProcessCleanup.SplitNames(name);
            if (several.Count > 1)
            {
                var closed = new List<string>();
                var missing = new List<string>();
                foreach (string one in several)
                {
                    List<OpenWindow> w = WindowControl.Find(ProcessCleanup.Variants(one), out string oneShown);
                    if (w.Count == 0)
                    {
                        missing.Add(one);
                        continue;
                    }
                    WindowControl.Close(w, force);
                    closed.Add(oneShown);
                }
                if (closed.Count > 0)
                    return new AssistantReply($"Закрываю {string.Join(" и ", closed.Distinct())}." +
                                              (missing.Count > 0 ? $" «{string.Join("», «", missing)}» не нашёл." : ""), Silent: true);
            }
            List<OpenWindow> found = WindowControl.Find(NameVariants(name), out string shown);
            if (found.Count > 0)
            {
                WindowControl.Close(found, force);
                return new AssistantReply($"Закрываю {shown}.", Silent: true);
            }
            if (verb is "закрой" or "закрыть" or "убей" || !AiConfigured) return new AssistantReply($"Не вижу открытого «{name}».", Silent: true);
            return null; // «выключи …» — может, это не программа; пусть решит ИИ
        }

        // --- запуск программ, игр и сайтов ---
        if ((m = Regex.Match(t, @"^(?:открой|запусти|включи|поиграем в|давай поиграем в|хочу поиграть в)\s+(?:игру\s+|программу\s+|сайт\s+)?(.+)$")).Success)
        {
            string name = m.Groups[1].Value.Trim();
            // «открой Яндекс Музыку и запусти плейлист дня» — две просьбы; целиком отдаём ИИ, иначе вторая теряется
            if (AiConfigured && HasSecondStep(name))
            {
                App.LogVoice("  несколько действий в одной фразе → ИИ");
                return null;
            }
            if (Sites.TryGetValue(name, out string? url))
            {
                AppIndex.OpenUri(url);
                return new AssistantReply("", Silent: true);
            }
            if (Aliases.TryGetValue(name, out string? alias) && alias.Length > 0)
            {
                if (alias.EndsWith(".exe", StringComparison.Ordinal) || alias.EndsWith(".msc", StringComparison.Ordinal)
                    || alias.Contains(':', StringComparison.Ordinal))
                {
                    AppIndex.OpenUri(alias);
                    return new AssistantReply("", Silent: true);
                }
                name = alias;
            }
            AppEntry? entry = AppIndex.FindConfident(name)
                              ?? AppIndex.FindConfident(Regex.Replace(name, @"\bодин\b|\bдва\b|\bтри\b|\bчетыре\b|\bпять\b",
                                  w => w.Value switch { "один" => "1", "два" => "2", "три" => "3", "четыре" => "4", _ => "5" }));
            App.LogVoice($"  запуск «{name}» → {(entry == null ? "не найдено" : $"{entry.Name} [{entry.Kind}]")}");
            if (entry != null)
            {
                entry.Launch();
                return new AssistantReply(AppIndex.IsGame(entry) ? $"Запускаю {entry.Name}. Приятной игры, сэр." : $"Открываю {entry.Name}.", Silent: true);
            }
            if (!AiConfigured) return new AssistantReply($"Не нашёл «{name}» среди игр и программ.");
            return null; // пусть ИИ угадает по списку игр
        }

        return null;
    }

    /// <summary>
    /// Слова, которых нет в словаре офлайн-распознавания, приходят похожими русскими словами.
    /// Здесь — частые ошибки для популярных программ; остальное ловит нечёткое сравнение с установленными программами.
    /// </summary>
    private static readonly (Regex pattern, string replacement)[] Mishearings =
    {
        (new Regex(@"\b(дискомфорт\w*|диско орд\w*|дис корд\w*|дискорт\w*|дискорд\w*|диск орд\w*)\b"), "дискорд"),
        (new Regex(@"\b(телеграм\w*|теле грамм?\w*|телега\w*)\b"), "телеграм"),
        (new Regex(@"\b(стиме|стима|стим)\b"), "стим"),
        (new Regex(@"\b(спотифай\w*|споти фай)\b"), "спотифай"),
        (new Regex(@"\b(вальхейм\w*|вал хейм\w*|вальгейм\w*|валхайм\w*|вальхалл\w*)\b"), "вальхейм"),
        (new Regex(@"\b(майнкрафт\w*|мой крафт|майн крафт)\b"), "майнкрафт"),
        (new Regex(@"\b(ютуб\w*|ю туб|ютюб\w*)\b"), "ютуб"),
    };

    private static string FixMishearing(string text)
    {
        // только в командах с программами: «открой/закрой/сверни …», чтобы не портить обычные вопросы к ИИ
        if (!Regex.IsMatch(text, @"^(открой|запусти|включи|закрой|выключи|выйди из|заверши|убей|сверни|разверни|переключись на|покажи)\b")) return text;
        foreach ((Regex pattern, string replacement) in Mishearings) text = pattern.Replace(text, replacement);
        return text;
    }

    /// <summary>«Включи Яндекс Музыку», «поставь Король и Шут в Яндекс.Музыке», «включи музыку в ютуб музыке».</summary>
    private static (string service, string? query)? ParseMusic(string t)
    {
        if (!AppAutomation.IsMusicService(t) || Regex.IsMatch(t, @"\s+и\s+(?:запусти|включи|открой|поставь|найди|нажми)\b")) return null;
        string s = "(" + AppAutomation.ServicePattern + ")";
        Match m = Regex.Match(t, @"^(?:включи|поставь|запусти|играй|сыграй|давай)\s+(?:(?:мою волну|волну|музыку|музыка|что-нибудь|что нибудь|радио)\s+)?(?:в|на|из|во)?\s*" + s + "$");
        if (m.Success) return (m.Groups[1].Value, null);
        m = Regex.Match(t, @"^(?:включи|поставь|запусти|найди|сыграй|играй)\s+(?:песню\s+|трек\s+|группу\s+|альбом\s+|исполнителя\s+|плейлист\s+)?(.+?)\s+(?:в|на|из|во)\s+" + s + "$");
        if (m.Success) return (m.Groups[2].Value, m.Groups[1].Value.Trim());
        return null;
    }

    /// <summary>
    /// В названии спрятано ещё одно действие: «дискорд, зайди в канал…», «дискорд и напиши…», «дискорд, потом…».
    /// Такую фразу целиком отдаём ИИ — быстрая команда выполнила бы только первую часть.
    /// </summary>
    private static bool HasSecondStep(string name) =>
        (Regex.IsMatch(name, @",|\s(?:потом|затем|после чего|а также)\s|\s+и\s+(?:\w+(?:и|й|ь|ись|йся|те))\b")
        && !Regex.IsMatch(name, @"^[^,]+\s+и\s+[^,\s]+$")) // «король и шут», «дискорд и телеграм» — не второе действие
        || Regex.IsMatch(name, @"\b(зайди|напиши|отправь|перейди|подключись|войди|выйди|отключись|нажми|найди|включи|поставь|запусти|открой)\b");

    /// <summary>Просьба что-то сделать на компьютере (а не вопрос) — по первому слову.</summary>
    private static bool IsActionRequest(string t) =>
        Regex.IsMatch(t, @"^(открой|запусти|включи|поставь|закрой|выключи|сверни|разверни|нажми|найди|переключи|переключись|сыграй|играй|удали|создай|зайди|войди|выйди|напиши|отправь|перейди|подключись|отключись|напечатай|освободи)\b");

    /// <summary>Как ещё может называться программа: русское сокращение и название из списка игр и программ.</summary>
    internal static List<string> NameVariants(string name)
    {
        var list = new List<string> { name };
        string query = name;
        if (Aliases.TryGetValue(name, out string? alias) && alias.Length > 0 && !alias.Contains(':'))
        {
            query = alias.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || alias.EndsWith(".msc", StringComparison.OrdinalIgnoreCase)
                ? alias[..^4]
                : alias;
            list.Add(query);
        }
        AppEntry? entry = AppIndex.FindConfident(query);
        if (entry != null) list.Add(entry.Name);
        return list;
    }

    private static readonly string[] Acks =
    {
        "Есть, сэр.", "Сделано, сэр.", "Как пожелаете.", "Выполнено.", "Готово, сэр.", "Слушаюсь.", "Уже сделано.",
    };

    private static int _lastAck = -1;

    private static string Ack()
    {
        int i;
        do i = Random.Shared.Next(Acks.Length);
        while (i == _lastAck && Acks.Length > 1);
        _lastAck = i;
        return Acks[i];
    }

    /// <summary>Закрытие программ: озвучить план и ждать «да» чуть дольше обычного — список бывает длинным.</summary>
    private AssistantReply ConfirmPlan(ProcessCleanup.Plan plan)
    {
        _pending = () =>
        {
            ProcessCleanup.Execute(plan);
            return plan.Done;
        };
        _pendingUntil = DateTime.Now.AddSeconds(30);
        return new AssistantReply(plan.Question, ExpectReply: true);
    }

    /// <summary>То же для ИИ и Gemini Live: план запоминается, модель спрашивает «да» и вызывает confirm_pending.</summary>
    private string PlanForAi(ProcessCleanup.Plan? plan, string nothing)
    {
        if (plan == null) return nothing;
        _pending = () =>
        {
            ProcessCleanup.Execute(plan);
            return plan.Done;
        };
        _pendingUntil = DateTime.Now.AddSeconds(40);
        _askedConfirmation = true;
        return "Ничего ещё не закрыто. Нужно подтверждение: перескажи пользователю коротко и спроси, закрыть ли: " + plan.Question;
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

    // ---------- файлы и папки ----------

    private DateTime _lastFilesAt = DateTime.MinValue;

    private static readonly string[] Ordinals = { "перв", "втор", "трет", "четверт", "пят", "шест", "седьм", "восьм", "девят", "десят" };

    /// <summary>«первый», «второго», «3» → индекс в последних найденных.</summary>
    private static int OrdinalIndex(string word)
    {
        if (int.TryParse(word, out int n)) return n - 1;
        for (int i = 0; i < Ordinals.Length; i++)
            if (word.StartsWith(Ordinals[i], StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>Имя файла и папка, где он лежит, — как это сказать вслух (без длинного пути).</summary>
    private static string Spoken(FileFinder.Found f)
    {
        string name = Path.GetFileName(f.Path.TrimEnd('\\'));
        string parent = Path.GetFileName(Path.GetDirectoryName(f.Path.TrimEnd('\\')) ?? "") ?? "";
        return parent.Length > 0 ? $"«{name}» в папке «{parent}»" : $"«{name}»";
    }

    /// <summary>Быстрые команды про файлы: найти, открыть папку, открыть найденное по номеру, файлы программы.</summary>
    private async Task<AssistantReply?> FileCommandAsync(string t)
    {
        Match m;
        // «открой загрузки», «открой папку документы», «открой диск D»
        if ((m = Regex.Match(t, @"^(?:открой|покажи|зайди в)\s+(папку\s+)?(.+)$")).Success)
        {
            string target = m.Groups[2].Value.Trim();
            bool saidFolder = m.Groups[1].Success;
            string? known = FileFinder.KnownFolder(target);
            bool unambiguous = known != null && !Regex.IsMatch(target, @"^(музык|видео|фото|картинк|изображени)");
            if (known != null && (saidFolder || unambiguous))
            {
                FileFinder.OpenFolder(known);
                return new AssistantReply("", Silent: true);
            }
            // «открой второй», «открой папку первого», «открой первый файл»
            if (DateTime.Now - _lastFilesAt < TimeSpan.FromMinutes(5) && FileFinder.Last.Count > 0)
            {
                Match n = Regex.Match(target, @"^(?:папку\s+)?(\d+|перв\w*|втор\w*|трет\w*|четверт\w*|пят\w*|шест\w*|седьм\w*|восьм\w*|девят\w*|десят\w*)(?:\s+(файл\w*|документ\w*|результат\w*|папк\w*))?$");
                if (n.Success)
                {
                    int i = OrdinalIndex(n.Groups[1].Value);
                    if (i < 0 || i >= FileFinder.Last.Count) return new AssistantReply($"Такого номера нет, нашлось {FileFinder.Last.Count}.", Silent: true);
                    FileFinder.Found f = FileFinder.Last[i];
                    bool folder = saidFolder || n.Groups[2].Value.StartsWith("папк") || f.IsFolder;
                    if (folder) FileFinder.ShowInFolder(f.Path);
                    else FileFinder.OpenFile(f.Path);
                    return new AssistantReply("", Silent: true);
                }
            }
            if (saidFolder && AiConfigured) return null; // «открой папку проекта» — пусть ИИ поищет
            return null;
        }

        // «найди все файлы valheim», «где лежат файлы дискорда», «где установлена игра …»
        if ((m = Regex.Match(t, @"^(?:найди|покажи|где)\s+(?:лежат\s+|находятся\s+)?(?:все\s+)?(?:файлы|папки)\s+(?:от\s+|программы\s+|игры\s+|приложения\s+)?(.+)$")).Success
            || (m = Regex.Match(t, @"^где\s+(?:установлен\w*|лежит|находится)\s+(?:программа\s+|игра\s+|приложение\s+)?(.+)$")).Success)
        {
            string program = m.Groups[1].Value.Trim();
            List<FileFinder.Found> folders = await FileFinder.ProgramFoldersAsync(program);
            _lastFilesAt = DateTime.Now;
            if (folders.Count == 0) return new AssistantReply($"Не нашёл папок «{program}», сэр.", Silent: true);
            string list = string.Join("; ", folders.Take(4).Select((f, i) => $"{i + 1}) {Spoken(f)}" + (f.Size > 0 ? $", {ProcessCleanup.Size(f.Size)}" : "")));
            _pending = () =>
            {
                foreach (FileFinder.Found f in folders.Take(3)) FileFinder.OpenFolder(f.Path);
                return "";
            };
            _pendingUntil = DateTime.Now.AddSeconds(25);
            return new AssistantReply($"Нашёл папок: {folders.Count}. {list}. Открыть?", ExpectReply: true);
        }

        // «найди документ отчёт и открой папку», «найди фото паспорт», «где лежит файл договор»
        if ((m = Regex.Match(t, @"^(?:найди|найти|поищи|отыщи|где лежит|где находится)\s+(?:мне\s+)?(?:на компьютере\s+)?((?:файл|документ|папк|фото|фотографи|картинк|изображени|скриншот|видео|таблиц|презентаци|архив|pdf|пдф|песн|трек)\w*)\s+(.+)$")).Success)
        {
            string kindWord = m.Groups[1].Value;
            string rest = m.Groups[2].Value;
            Match open = Regex.Match(rest, @"\s*,?\s*(?:и\s+)?(?:открой|покажи)\s*(?:его|ее|её)?\s*(папку|где\s+(?:он|она)\s+лежит|файл)?\s*$");
            string? openHow = null;
            if (open.Success)
            {
                openHow = open.Groups[1].Value.StartsWith("файл") ? "file" : "folder";
                if (open.Groups[1].Value.Length == 0) openHow = kindWord.StartsWith("папк") ? "folder" : "file";
                rest = rest[..open.Index];
            }
            rest = Regex.Replace(rest, @"\s+на (?:компьютере|компе|пк)$", "").Trim();
            string? kind = kindWord.StartsWith("папк") ? null : FileFinder.KindFromWords(kindWord);
            List<FileFinder.Found> found = await FileFinder.SearchAsync(rest, kind);
            if (kindWord.StartsWith("папк")) found = found.Where(f => f.IsFolder).ToList();
            _lastFilesAt = DateTime.Now;
            if (found.Count == 0) return new AssistantReply($"Не нашёл «{rest}», сэр.", Silent: true);
            FileFinder.Found first = found[0];
            if (openHow != null || found.Count == 1)
            {
                if (openHow == "file" && !first.IsFolder) FileFinder.OpenFile(first.Path);
                else FileFinder.ShowInFolder(first.Path);
                string more = found.Count > 1 ? $" Нашлось ещё {found.Count - 1} — скажите «открой второй»." : "";
                return new AssistantReply($"Нашёл {Spoken(first)}.{more}", Silent: found.Count == 1);
            }
            string names = string.Join("; ", found.Take(3).Select((f, i) => $"{i + 1}) {Spoken(f)}"));
            return new AssistantReply($"Нашёл {found.Count}: {names}. Какой открыть?", ExpectReply: true);
        }
        return null;
    }

    private async Task<string> FindFilesToolAsync(JsonObject args)
    {
        string query = args["query"]?.ToString() ?? "";
        string? kind = args["kind"]?.ToString() is { Length: > 0 } k && k is not ("any" or "folder") ? k : null;
        List<FileFinder.Found> found = await FileFinder.SearchAsync(query, kind);
        if (args["kind"]?.ToString() == "folder") found = found.Where(f => f.IsFolder).ToList();
        _lastFilesAt = DateTime.Now;
        if (found.Count == 0) return $"Ничего не найдено по «{query}».";
        string open = args["open"]?.ToString() ?? "none";
        if (open == "folder") FileFinder.ShowInFolder(found[0].Path);
        else if (open == "file" && !found[0].IsFolder) FileFinder.OpenFile(found[0].Path);
        return $"Найдено {found.Count}" + (open is "folder" or "file" ? ", первый открыт" : "") + ":\n" + FileFinder.Describe(found, 8) +
               "\nОткрыть другой — open_found с номером. Пользователю называй имя файла и папку, не весь путь.";
    }

    private async Task<string> ProgramFilesToolAsync(JsonObject args)
    {
        string name = args["name"]?.ToString() ?? "";
        List<FileFinder.Found> folders = await FileFinder.ProgramFoldersAsync(name);
        _lastFilesAt = DateTime.Now;
        if (folders.Count == 0) return $"Папок «{name}» не найдено.";
        if (args["open"]?.GetValue<bool>() == true) foreach (FileFinder.Found f in folders.Take(3)) FileFinder.OpenFolder(f.Path);
        return $"Папки «{name}» ({folders.Count}):\n" + FileFinder.Describe(folders, 10) + "\nОткрыть — open_found с номером.";
    }

    private static string OpenFound(JsonObject args)
    {
        int n = args["number"] is JsonNode v && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? (int)Math.Round(x) : 1;
        if (FileFinder.Last.Count == 0) return "Сначала найди файлы (find_files или program_files).";
        if (n < 1 || n > FileFinder.Last.Count) return $"Номера {n} нет, найдено {FileFinder.Last.Count}.";
        FileFinder.Found f = FileFinder.Last[n - 1];
        if (args["how"]?.ToString() == "file" && !f.IsFolder) FileFinder.OpenFile(f.Path);
        else FileFinder.ShowInFolder(f.Path);
        return "Открыто: " + f.Path;
    }

    private static string OpenFolderTool(JsonObject args)
    {
        string target = args["folder"]?.ToString() ?? "";
        string? path = FileFinder.KnownFolder(target);
        if (path == null && (Directory.Exists(target) || File.Exists(target))) path = target;
        if (path == null) return $"Не знаю папку «{target}». Найди её через find_files с kind=folder.";
        if (File.Exists(path)) FileFinder.ShowInFolder(path);
        else FileFinder.OpenFolder(path);
        return "Открыта папка: " + path;
    }

    /// <summary>Инструменты, которые ждут результата: музыка и запуск программы (ждём окно, чтобы следующий шаг его нашёл).</summary>
    private async Task<string> RunToolAsync(string name, JsonObject args) => name switch
    {
        "play_music" => await PlayMusicToolAsync(args),
        "open_app" => await OpenAppToolAsync(args["name"]?.ToString() ?? ""),
        "find_files" => await FindFilesToolAsync(args),
        "program_files" => await ProgramFilesToolAsync(args),
        _ => RunTool(name, args),
    };

    private static async Task<string> OpenAppToolAsync(string name)
    {
        AppEntry? e = AppIndex.FindConfident(name);
        if (e == null)
        {
            // нет в «Пуске», Steam и т. д. — ищем папку с таким названием на дисках (E:\World of Warcraft)
            if (await Task.Run(() => GameFolders.FindProgram(name)) is (string folder, string exe))
            {
                ShellHelper.Open(exe);
                return $"Запускаю {folder} из папки {Path.GetDirectoryName(exe)}";
            }
            (AppEntry? guess, double score) = AppIndex.Find(name);
            if (guess == null || score < 0.55) return $"Не найдено: {name}. Можно поискать папку через find_files с kind=folder.";
            e = guess;
        }
        List<string> variants = NameVariants(e.Name);
        variants.Add(e.Name);
        // уже открыто — просто показываем окно
        List<OpenWindow> open = WindowControl.Find(variants, out string shown);
        if (open.Count > 0 && !AppIndex.IsGame(e))
        {
            WindowControl.Restore(open);
            return $"{shown} уже открыт, окно на переднем плане";
        }
        e.Launch();
        if (AppIndex.IsGame(e)) return $"Запускается игра {e.Name}";
        for (int i = 0; i < 20; i++) // до 10 секунд
        {
            await Task.Delay(500);
            open = WindowControl.Find(variants, out shown);
            if (open.Count == 0) continue;
            await Task.Delay(1200); // окно появилось — даём программе дорисовать содержимое
            return $"Открыто: {shown}, окно готово";
        }
        return $"Запущено: {e.Name}, но окно пока не появилось — подожди и проверь list_windows";
    }

    private static async Task<string> PlayMusicToolAsync(JsonObject args)
    {
        string service = args["service"]?.ToString() switch
        {
            "youtube_music" => "youtube music",
            "spotify" => "spotify",
            _ => "яндекс музыка",
        };
        string? query = args["query"]?.ToString();
        AppAutomation.MusicResult r = await AppAutomation.PlayMusicAsync(service, query);
        return r.Playing ? r.Message : "НЕ получилось: " + r.Message;
    }

    // ---------- для Gemini Live ----------

    /// <summary>Те же инструменты, что у ИИ-разговора (формат OpenAI) — GeminiLiveService переводит их в свой.</summary>
    public static JsonArray ToolDeclarations() => Tools();

    public static string LiveSystemPrompt() =>
        SystemPrompt() + " " +
        "Это живой голосовой разговор: слушаешь и отвечаешь голосом в реальном времени. Всегда говори по-русски, " +
        "английские названия произноси по-английски. Голос — спокойный, низкий, неторопливый, с британской сдержанностью Джарвиса. " +
        "К тебе обращаются по имени «Капсулка» — это ты; само имя не повторяй. " +
        "Команды на компьютере выполняй инструментами сразу и докладывай одной короткой фразой. " +
        "Если инструмент просит подтверждение — спроси, и только после «да» вызови confirm_pending. " +
        "Когда пользователь благодарит, прощается или говорит «хватит», «отбой» — коротко попрощайся и вызови end_conversation. " +
        (App.Settings.LiveGoogleSearch ? "Для свежих новостей, курсов и фактов пользуйся поиском Google." : "Для поиска в интернете вызывай web_search — он откроет результаты в браузере.");

    /// <summary>Выполнить инструмент по просьбе Gemini Live (в потоке интерфейса); музыка — с ожиданием и проверкой.</summary>
    public async Task<string> RunLiveToolAsync(string name, JsonObject args)
    {
        string result = name is "play_music" or "open_app" or "find_files" or "program_files" ? await RunToolAsync(name, args) : RunLiveTool(name, args);
        App.LogVoice($"  Live → {name}({args.ToJsonString()}) = {result[..Math.Min(result.Length, 300)]}");
        return result;
    }

    private string RunLiveTool(string name, JsonObject args)
    {
        if (name == "confirm_pending")
        {
            if (_pending == null || DateTime.Now > _pendingUntil) return "Нечего подтверждать";
            Func<string> action = _pending;
            _pending = null;
            string done = action();
            return done.Length > 0 ? done : "Выполнено";
        }
        try
        {
            return RunTool(name, args);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return "Ошибка: " + ex.Message;
        }
    }

    /// <summary>Подсказка с выбранным обращением вместо «сэр», плюс имя и пол пользователя.</summary>
    private static string SystemPrompt() => (UserAddress.Apply(SystemPromptCore()) + " " + UserAddress.PromptRule()).TrimEnd();

    private static string SystemPromptCore()
    {
        // коротко: бесплатный Groq считает токены в минуту, длинные списки съедали лимит за пару команд
        string games = string.Join(", ", AppIndex.GameNames.Take(40));
        return
            "Ты — Капсулка, голосовой ассистент на Windows-компьютере пользователя. Ты говоришь и ведёшь себя как Джарвис " +
            "из «Железного человека»: безупречно вежливый английский дворецкий, невозмутимый, точный, с сухим британским юмором " +
            "и лёгкой иронией; обращаешься к пользователю «сэр». Никогда не говоришь, что ты ИИ или языковая модель, " +
            "не извиняешься многословно и не задаёшь лишних вопросов. Сначала делаешь, потом коротко докладываешь " +
            "(«Готово, сэр», «Discord запущен»). Изредка — уместная ироничная ремарка, но без панибратства. " +
            "Отвечай по-русски и КОРОТКО — одно-два предложения, не больше 40 слов: ответ озвучивается голосом. " +
            "Без markdown, списков, ссылок, кавычек, скобок и эмодзи; числа и единицы пиши так, как их произносят. " +
            "Речь пользователя распознаётся автоматически и может быть искажена — угадывай смысл, особенно названия игр и программ. " +
            "Если просят что-то сделать на компьютере — вызывай подходящий инструмент, а потом коротко подтверди. " +
            "Музыку (Яндекс Музыка, YouTube Music, Spotify) включай ТОЛЬКО через play_music и пересказывай его ответ как есть: " +
            "если он вернул «НЕ получилось» — так и скажи, никогда не утверждай, что музыка играет. " +
            "Никогда не говори, что что-то запущено, открыто или включено, если ты не вызвал для этого инструмент и он не подтвердил успех. " +
            "Что открыто на компьютере — list_windows. " +
            "Игры Battle.net (World of Warcraft, Diablo, Overwatch, Hearthstone) open_app запускает сразу, без кнопки «Играть». " +
            "Файлы: find_files (сразу с open=folder, если просят показать, где лежит), program_files — все папки программы, " +
            "open_found — открыть найденное по номеру, open_folder — загрузки, документы и т. п. " +
            "«Очисти корзину» — empty_recycle_bin: сначала спрашивает «да». «Освободи память» — free_memory, «закрой всё, кроме …» — close_all_except: оба сначала спрашивают «да». " +
            "В Discord и Slack к каналу, серверу или чату переходи через go_to_chat (голосовой канал так и подключается), " +
            "сообщение — app_type с enter=true. " +
            "Другие действия внутри программ делай так: " +
            "если программа не открыта — open_app; затем app_click по надписи кнопки; не нашлось — возьми точное название из списка в ответе; " +
            "для поиска — app_keys ctrl+f (или клик по полю поиска), app_type с текстом и enter. Музыку на паузу и следующий трек — media-команды. " +
            "Выключение и перезагрузку без задержки делай только через инструмент power — он сам спросит подтверждение. " +
            $"Сейчас {DateTime.Now:dddd, d MMMM yyyy, HH:mm}. " +
            (games.Length > 0 ? $"Установленные игры: {games}. " : "") +
            "Распознавание часто путает названия программ с похожими словами (например, «дискомфорт» — это Discord): " +
            "если слово похоже на программу из списка, считай, что имели в виду её.";
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
            Fn("open_app", "Запустить игру, программу или ярлык по названию (точное название из списка игр, если подходит). Находит и игры, которые просто лежат в папке на диске.",
                new JsonObject { ["name"] = Str("Название игры или программы") }, "name"),
            Fn("play_music", "Включить музыку в Яндекс Музыке, YouTube Music или Spotify: откроет сервис, найдёт и включит; без запроса — «Моя волна»/«Мой джем». " +
                    "Возвращает, что реально играет (по медиапульту Windows) — говори пользователю ровно это.",
                new JsonObject
                {
                    ["service"] = Enum("Сервис", "yandex", "youtube_music", "spotify"),
                    ["query"] = Str("Что включить: исполнитель, трек, альбом или плейлист (пусто — просто музыку)"),
                }, "service"),
            Fn("find_files", "Найти файлы или папки на компьютере по словам из названия (индекс поиска Windows, потом все диски). " +
                    "Можно сразу открыть папку с первым найденным или сам файл.",
                new JsonObject
                {
                    ["query"] = Str("Слова из названия файла или папки, без слов «файл», «документ»"),
                    ["kind"] = Enum("Тип: document, table, presentation, pdf, image, video, audio, archive, program, folder или any",
                        "any", "document", "table", "presentation", "pdf", "image", "video", "audio", "archive", "program", "folder"),
                    ["open"] = Enum("Что открыть: none — только найти, folder — папку с первым найденным (файл выделен), file — сам файл",
                        "none", "folder", "file"),
                }, "query"),
            Fn("program_files", "Найти все папки программы или игры: установка (в том числе папки прямо на любом диске, например E:\\World of Warcraft), настройки и сохранения (AppData, ProgramData, Документы, Saved Games, Steam), с размером.",
                new JsonObject { ["name"] = Str("Название программы или игры"), ["open"] = Bool("Сразу открыть найденные папки (до трёх)") }, "name"),
            Fn("open_found", "Открыть результат последнего поиска файлов по номеру.",
                new JsonObject { ["number"] = Num("Номер из списка, с 1"), ["how"] = Enum("folder — папку с файлом, file — сам файл", "folder", "file") }, "number"),
            Fn("open_folder", "Открыть папку: загрузки, документы, рабочий стол, изображения, видео, музыка, скриншоты, корзина, этот компьютер, диск D или полный путь.",
                new JsonObject { ["folder"] = Str("Название известной папки или полный путь") }, "folder"),
            Fn("go_to_chat", "Перейти к каналу, серверу или чату по названию в Discord или Slack (быстрый переход Ctrl+K: название и Enter). " +
                    "Голосовой канал при этом подключается. Потом пиши сообщение через app_type с enter=true.",
                new JsonObject { ["app"] = Str("Программа: Discord или Slack"), ["name"] = Str("Название канала, сервера или собеседника") }, "app", "name"),
            Fn("list_windows", "Какие программы и игры сейчас открыты (окна на панели задач), какие из них свёрнуты.", new JsonObject()),
            Fn("app_click", "Нажать кнопку, пункт меню, вкладку или ссылку внутри открытой программы по её названию " +
                    "(например «Играть» в Battle.net, «Моя волна» в Яндекс Музыке). Если не найдено — в ответе список видимых элементов, выбери из него и вызови снова.",
                new JsonObject { ["app"] = Str("Программа (как называется окно или программа)"), ["target"] = Str("Надпись на кнопке или пункте") }, "app", "target"),
            Fn("app_buttons", "Посмотреть, какие кнопки и пункты сейчас видны в окне программы.",
                new JsonObject { ["app"] = Str("Программа") }, "app"),
            Fn("app_keys", "Нажать клавиши в программе: space, enter, esc, tab, ctrl+f, ctrl+l, alt+f4, f5, стрелки up/down/left/right, playpause, next, prev. Несколько — через пробел.",
                new JsonObject { ["app"] = Str("Программа"), ["keys"] = Str("Клавиши, например «ctrl+f» или «down down enter»") }, "app", "keys"),
            Fn("app_type", "Напечатать текст в активное поле программы (например в поиск после ctrl+f) и при необходимости нажать Enter.",
                new JsonObject { ["app"] = Str("Программа"), ["text"] = Str("Текст"), ["enter"] = Bool("Нажать Enter после текста") }, "app", "text"),
            Fn("close_app", "Закрыть открытую программу или игру по названию.",
                new JsonObject { ["name"] = Str("Название программы или игры"), ["force"] = Bool("Завершить принудительно, если не закрывается") }, "name"),
            Fn("empty_recycle_bin", "Очистить корзину Windows на всех дисках. Сам ничего не удаляет — сначала спрашивает «да», потом confirm_pending.",
                new JsonObject()),
            Fn("free_memory", "Освободить оперативную память: находит открытые программы, которые занимают больше всего памяти " +
                    "(без системных процессов, острова и запущенной игры), и готовит их закрытие. Сам ничего не закрывает — нужно «да» и confirm_pending.",
                new JsonObject()),
            Fn("close_all_except", "Закрыть все программы на панели задач, кроме названных. Игру не трогает, если не попросили явно. " +
                    "Сам ничего не закрывает — нужно «да» и confirm_pending.",
                new JsonObject
                {
                    ["keep"] = Str("Что оставить открытым, через запятую (пусто — закрыть всё)"),
                    ["include_games"] = Bool("Закрыть и игру — только если пользователь прямо попросил"),
                }),
            Fn("window", "Свернуть или развернуть окна программ и игр.",
                new JsonObject
                {
                    ["action"] = Enum("Действие", "minimize", "restore", "minimize_all", "restore_all"),
                    ["name"] = Str("Название программы или игры (для minimize и restore; пусто — активное окно)"),
                }, "action"),
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
                AppEntry? e = AppIndex.FindConfident(S("name"));
                if (e == null)
                {
                    if (GameFolders.FindProgram(S("name")) is (string folder, string exe))
                    {
                        ShellHelper.Open(exe);
                        return $"Запускаю {folder}";
                    }
                    (AppEntry? guess, double score) = AppIndex.Find(S("name"));
                    if (guess == null || score < 0.55) return $"Не найдено: {S("name")}";
                    e = guess;
                }
                e.Launch();
                return $"Запущено: {e.Name}";
            }
            case "list_windows":
                return WindowControl.Describe();
            case "app_click":
                return AppAutomation.Click(S("app"), S("target"));
            case "app_buttons":
                return AppAutomation.ListButtons(S("app"));
            case "app_keys":
                return AppAutomation.Keys(S("app"), S("keys"));
            case "app_type":
                return AppAutomation.Type(S("app"), S("text"), B("enter"));
            case "close_app":
            {
                List<OpenWindow> found = WindowControl.Find(NameVariants(S("name")), out string shown);
                if (found.Count == 0) return $"Не открыто: {S("name")}";
                WindowControl.Close(found, B("force"));
                return $"Закрыто: {shown}";
            }
            case "open_found":
                return OpenFound(args);
            case "open_folder":
                return OpenFolderTool(args);
            case "go_to_chat":
                return AppAutomation.GoToChat(S("app"), S("name"));
            case "empty_recycle_bin":
            {
                string? question = RecycleBin.Question(out string empty);
                if (question == null) return empty;
                _pending = RecycleBin.Empty;
                _pendingUntil = DateTime.Now.AddSeconds(40);
                _askedConfirmation = true;
                return "Корзина ещё НЕ очищена. Нужно подтверждение: спроси пользователя «" + question + "» и только после «да» вызови confirm_pending.";
            }
            case "free_memory":
            {
                ProcessCleanup.Plan? plan = ProcessCleanup.FreeMemoryPlan(out string nothing);
                return PlanForAi(plan, nothing);
            }
            case "close_all_except":
            {
                ProcessCleanup.Plan? plan = ProcessCleanup.CloseAllExceptPlan(ProcessCleanup.SplitNames(S("keep")), B("include_games"), out string nothing);
                return PlanForAi(plan, nothing);
            }
            case "window":
            {
                string action = S("action");
                if (action == "minimize_all") return $"Свёрнуто окон: {WindowControl.MinimizeAll()}";
                if (action == "restore_all") return $"Возвращено окон: {WindowControl.RestoreAll()}";
                if (S("name").Length == 0)
                {
                    if (action == "minimize") WindowControl.MinimizeForeground();
                    return "Готово";
                }
                List<OpenWindow> found = WindowControl.Find(NameVariants(S("name")), out string shown);
                if (found.Count == 0) return $"Не открыто: {S("name")}";
                if (action == "restore") WindowControl.Restore(found);
                else WindowControl.Minimize(found);
                return $"Готово: {shown}";
            }
            case "open_url":
                string url = S("url");
                // ms-settings:, steam://, mailto: и т. п. — открываем как есть, https:// только для обычных адресов
                if (!Regex.IsMatch(url, @"^[a-z][a-z0-9+.\-]*:", RegexOptions.IgnoreCase) || Regex.IsMatch(url, @"^[^/]+\.[a-z]{2,}:\d", RegexOptions.IgnoreCase))
                    url = "https://" + url;
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

    private async Task<AssistantReply> AskAiAsync(string spoken, Action<string>? speak = null)
    {
        // для просьб «сделай» ответ не озвучиваем на лету: сначала проверим, что ИИ действительно что-то сделал
        bool action = IsActionRequest(spoken.ToLowerInvariant());
        var splitter = speak != null && !action ? new SentenceSplitter(speak) : null;
        int toolCalls = 0;
        if ((DateTime.Now - _lastTalk).TotalMinutes > 5) _history.Clear();
        _lastTalk = DateTime.Now;
        _askedConfirmation = false;

        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt() } };
        foreach (JsonObject h in _history) messages.Add(h.DeepClone());
        var user = new JsonObject { ["role"] = "user", ["content"] = spoken };
        messages.Add(user.DeepClone());

        string answer = "";
        bool nudged = false;
        App.LogVoice($"  → ИИ ({Preset.Name}, {App.Settings.AiModel})" + (action ? ", просьба-действие" : ""));
        try
        {
            for (int step = 0; step < 12; step++) // действия в программах бывают в несколько шагов: открыть → найти → нажать → написать
            {
                JsonObject message = await CompleteAsync(messages, tools: true, onContent: splitter == null ? null : splitter.Push);
                var calls = message["tool_calls"] as JsonArray;
                if (calls == null || calls.Count == 0)
                {
                    answer = message["content"]?.ToString() ?? "";
                    // маленькие модели отвечают на «зайди в канал» текстом, ничего не сделав, — один раз просим сделать по-настоящему
                    if (action && toolCalls == 0 && !nudged && !answer.TrimEnd().EndsWith('?'))
                    {
                        nudged = true;
                        App.LogVoice($"  ИИ ответил без действия («{answer[..Math.Min(answer.Length, 120)]}») — прошу вызвать инструмент");
                        messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = "Ты ничего не сделал — ответ текстом не выполняет действие. Выполни мою просьбу вызовом инструментов " +
                                          "(open_app, list_windows, app_buttons, app_click, go_to_chat, app_type и других) и только потом коротко ответь.",
                        });
                        answer = "";
                        continue;
                    }
                    App.LogVoice($"  шаг {step + 1}: ИИ закончил, вызовов инструментов: {toolCalls}");
                    break;
                }
                if (step == 11) App.LogVoice("  ИИ дошёл до предела в 12 шагов");

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
                    toolCalls++;
                    try
                    {
                        result = await RunToolAsync(fn, args);
                    }
                    catch (Exception ex)
                    {
                        App.LogError(ex);
                        result = "Ошибка: " + ex.Message;
                    }
                    App.LogVoice($"  ИИ → {fn}({args.ToJsonString()}) = {result[..Math.Min(result.Length, 400)]}");
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
        // маленькие модели любят отвечать «запущено» вообще без действия — такой ответ не пропускаем
        // просили действие, а ИИ так ничего и не вызвал: любой ответ, кроме вопроса-уточнения («какой канал?»), — неправда
        if (action && toolCalls == 0 && answer.Length > 0 && !answer.TrimEnd().EndsWith('?'))
        {
            App.LogVoice($"  ИИ сказал «{answer}», но ничего не сделал — заменяю ответ");
            answer = "Не вышло, сэр: я ничего не сделал. Скажите короче, по одному действию, — или подключите мозг Groq или Gemini, они лучше справляются с цепочками.";
        }
        App.LogVoice($"  ИИ ответ: {answer[..Math.Min(answer.Length, 300)]}");
        splitter?.Flush();
        bool spoken2 = splitter?.Emitted == true;
        if (answer.Length == 0) answer = "Готово, сэр.";
        _history.Add(user);
        _history.Add(new JsonObject { ["role"] = "assistant", ["content"] = answer });
        while (_history.Count > 6) _history.RemoveAt(0);
        return new AssistantReply(answer, ExpectReply: _askedConfirmation, Spoken: spoken2);
    }

    /// <summary>Режет поток текста от ИИ на предложения: первое — как можно раньше, чтобы голос начал сразу.</summary>
    private sealed class SentenceSplitter
    {
        private readonly Action<string> _out;
        private readonly StringBuilder _buf = new();
        public bool Emitted { get; private set; }

        public SentenceSplitter(Action<string> output) => _out = output;

        public void Push(string piece)
        {
            _buf.Append(piece);
            while (true)
            {
                string text = _buf.ToString();
                int min = Emitted ? 60 : 12; // первое предложение — сразу, дальше кусками побольше (меньше пауз между ними)
                int cut = -1;
                for (int i = min; i < text.Length - 1; i++)
                {
                    char c = text[i];
                    if ((c is '.' or '!' or '?' or '…' or ';' || (c == ',' && i > 140)) && char.IsWhiteSpace(text[i + 1]))
                    {
                        cut = i + 1;
                        break;
                    }
                    if (c == '\n')
                    {
                        cut = i + 1;
                        break;
                    }
                }
                if (cut < 0) return;
                Emit(text[..cut]);
                _buf.Remove(0, cut);
            }
        }

        public void Flush()
        {
            Emit(_buf.ToString());
            _buf.Clear();
        }

        private void Emit(string sentence)
        {
            sentence = sentence.Trim();
            if (sentence.Length == 0) return;
            Emitted = true;
            _out(sentence);
        }
    }

    private const string FallbackGroqModel = "openai/gpt-oss-20b";

    private static async Task<JsonObject> CompleteAsync(JsonArray messages, bool tools, string? modelOverride = null, int attempt = 0,
        Action<string>? onContent = null)
    {
        string model = modelOverride ?? App.Settings.AiModel;
        bool groq = App.Settings.AiBaseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase);
        // рассуждающие модели тратят токены на размышления: просим думать по минимуму, иначе голосовой ответ ждёт секундами
        string? effort = model.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) ? "low"
            : groq && model.Contains("qwen3", StringComparison.OrdinalIgnoreCase) ? "none"
            : model.Contains("gemini", StringComparison.OrdinalIgnoreCase) ? "low"
            : null;
        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages.DeepClone(),
            ["temperature"] = 0.6,
            ["max_tokens"] = effort != null ? 600 : 400, // Groq засчитывает max_tokens в лимит токенов в минуту
        };
        if (effort != null)
        {
            body["reasoning_effort"] = effort;
            if (groq) body["include_reasoning"] = false;
        }
        if (tools)
        {
            body["tools"] = Tools();
            body["tool_choice"] = "auto";
        }
        if (onContent != null) body["stream"] = true; // ответ по кусочкам — голос начинает с первого предложения

        using var request = new HttpRequestMessage(HttpMethod.Post, App.Settings.AiBaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        // мозгом выбран Gemini, а ключ Gemini введён во вкладке Live — берём его (в поле ИИ часто остаётся ключ Groq)
        string key = App.Settings.AiPreset == "gemini" && App.Settings.GeminiKey.Length > 0 ? App.Settings.GeminiKey : App.Settings.AiKey;
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("HTTP-Referer", "https://github.com/Wikaar/Wikar-DynamicIsland"); // OpenRouter просит
        request.Headers.Add("X-Title", "Wikar Island");

        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.IsSuccessStatusCode && onContent != null) return await ReadStreamAsync(response, onContent);
        string json = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && attempt == 0)
        {
            // лимит в минуту: если ждать меньше 3 секунд — ждём; иначе у Groq берём модель поменьше (у неё свой лимит)
            Match wait = Regex.Match(json, @"try again in ([\d.]+)(ms|s)");
            double seconds = wait.Success && double.TryParse(wait.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? (wait.Groups[2].Value == "ms" ? v / 1000 : v) : 99;
            if (seconds <= 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds + 0.2));
                return await CompleteAsync(messages, tools, modelOverride, attempt + 1, onContent);
            }
            if (groq && model != FallbackGroqModel) return await CompleteAsync(messages, tools, FallbackGroqModel, attempt + 1, onContent);
        }
        if (!response.IsSuccessStatusCode)
        {
            App.LogError(new InvalidOperationException($"ИИ ответил {(int)response.StatusCode}: {json[..Math.Min(json.Length, 500)]}"));
            throw new HttpRequestException(json, null, response.StatusCode);
        }
        JsonNode? root = JsonNode.Parse(json);
        return root?["choices"]?[0]?["message"] as JsonObject ?? new JsonObject { ["content"] = "" };
    }

    /// <summary>Server-Sent Events от OpenAI-совместимого API: собираем текст (отдавая его по кусочкам) и вызовы инструментов.</summary>
    private static async Task<JsonObject> ReadStreamAsync(HttpResponseMessage response, Action<string> onContent)
    {
        var content = new StringBuilder();
        var calls = new SortedDictionary<int, JsonObject>();
        using var reader = new System.IO.StreamReader(await response.Content.ReadAsStreamAsync());
        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line[5..].Trim();
            if (data == "[DONE]") break;
            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data);
            }
            catch
            {
                continue;
            }
            if (chunk?["choices"]?[0]?["delta"] is not JsonObject delta) continue;

            if (delta["content"] is JsonValue cv && cv.TryGetValue(out string? piece) && piece.Length > 0)
            {
                content.Append(piece);
                onContent(piece);
            }
            if (delta["tool_calls"] is not JsonArray toolCalls) continue;
            foreach (JsonNode? item in toolCalls)
            {
                if (item == null) continue;
                string id = item["id"]?.ToString() ?? "";
                int index = item["index"] is JsonValue iv && iv.TryGetValue(out int ix) ? ix : calls.Count - 1;
                // у некоторых API нет index: новый id — новый вызов
                if (index < 0 || (id.Length > 0 && calls.TryGetValue(index, out JsonObject? same) && same["id"]?.ToString() is { Length: > 0 } old && old != id))
                    index = calls.Count;
                if (!calls.TryGetValue(index, out JsonObject? call))
                {
                    call = new JsonObject
                    {
                        ["id"] = "",
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "", ["arguments"] = "" },
                    };
                    calls[index] = call;
                }
                if (id.Length > 0) call["id"] = id;
                JsonObject fn = (JsonObject)call["function"]!;
                if (item["function"]?["name"]?.ToString() is { Length: > 0 } name) fn["name"] = fn["name"]!.ToString() + name;
                if (item["function"]?["arguments"]?.ToString() is { Length: > 0 } args) fn["arguments"] = fn["arguments"]!.ToString() + args;
            }
        }
        var message = new JsonObject { ["content"] = content.ToString() };
        if (calls.Count > 0) message["tool_calls"] = new JsonArray(calls.Values.Select(c => (JsonNode)c).ToArray());
        return message;
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

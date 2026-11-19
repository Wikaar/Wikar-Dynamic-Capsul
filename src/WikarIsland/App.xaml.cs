using System;
using System.IO;
using System.Threading;
using System.Windows;
using WikarIsland.Services;
using WikarIsland.Views;
using WF = System.Windows.Forms;

namespace WikarIsland;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = null!;
    public static PillWindow Pill { get; private set; } = null!;
    public static PanelWindow Panel { get; private set; } = null!;
    public static MediaService Media { get; private set; } = null!;
    public static MusicHub Music { get; private set; } = null!;
    public static WeatherService Weather { get; private set; } = null!;
    public static AudioService Audio { get; private set; } = null!;
    public static NotificationService Notifications { get; private set; } = null!;
    public static ClipboardService ClipboardHistory { get; private set; } = null!;
    public static SteamService Steam { get; private set; } = null!;
    public static PowerTimerService PowerTimer { get; private set; } = null!;
    public static NotesService Notes { get; private set; } = null!;
    public static RecorderService Recorder { get; private set; } = null!;
    public static AudioRecorderService AudioRecorder { get; private set; } = null!;
    public static SystemMonitorService SystemMonitor { get; private set; } = null!;
    public static VpnService Vpn { get; private set; } = null!;
    public static ZapretService Zapret { get; private set; } = null!;
    public static UpdateService Updater { get; private set; } = null!;
    public static FocusTimerService FocusTimer { get; private set; } = null!;
    public static ReminderService Reminders { get; private set; } = null!;
    public static GameService Game { get; private set; } = null!;
    public static DownloadService Downloads { get; private set; } = null!;
    public static SpeechService Speech { get; private set; } = null!;
    public static VoiceService Voice { get; private set; } = null!;
    public static AssistantService Assistant { get; private set; } = null!;
    public static SpectrumService Spectrum { get; } = new();
    public static DeviceBatteryService Batteries { get; private set; } = null!;
    public static GoogleService Google { get; private set; } = null!;

    private static Mutex? _instanceMutex;
    private static WF.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // проверка сборки в CI: WikarIsland.exe --selftest → selftest.txt и код выхода
        if (Array.IndexOf(e.Args, "--selftest") >= 0)
        {
            Environment.Exit(SelfTest.Run(online: Array.IndexOf(e.Args, "--online") >= 0));
            return;
        }

        // распакованная новая версия: заменить файлы острова и запустить его
        if (UpdateService.TryApplyUpdate(e.Args))
        {
            Shutdown();
            return;
        }

        // перезапуск (например, от администратора): ждём, пока старый экземпляр закроется
        int waitIndex = Array.IndexOf(e.Args, "--wait-for");
        if (waitIndex >= 0 && waitIndex + 1 < e.Args.Length && int.TryParse(e.Args[waitIndex + 1], out int oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(10000); }
            catch { /* уже закрылся */ }
        }

        // второй экземпляр не запускаем
        _instanceMutex = new Mutex(true, "WikarIsland.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // запасной режим для проблемных видеодрайверов: WikarIsland.exe --software
        if (Array.IndexOf(e.Args, "--software") >= 0)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogError(ex);
                ShowFatal(ex);
            }
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError(args.Exception);
            args.SetObserved();
        };

        try
        {
            StartApp();
        }
        catch (Exception ex)
        {
            LogError(ex);
            ShowFatal(ex);
            Shutdown();
        }
    }

    /// <summary>
    /// Запуск модуля, который может не работать в урезанных сборках Windows (нет нужных компонентов):
    /// ошибка пишется в лог, остальной остров продолжает работать.
    /// </summary>
    private static void Safe(string module, Action start)
    {
        try
        {
            start();
        }
        catch (Exception ex)
        {
            LogError(new InvalidOperationException($"Модуль «{module}» не запустился", ex));
        }
    }

    /// <summary>Понятное окно вместо «Unknown Hard Error»: что упало и где лог.</summary>
    private static void ShowFatal(Exception ex)
    {
        try
        {
            string log = System.IO.Path.Combine(AppSettings.DataDir, "error.log");
            MessageBox.Show(
                "Wikar Island не смог запуститься.\n\n" +
                $"{ex.GetType().Name}: {ex.Message}\n\n" +
                $"Windows {Environment.OSVersion.Version}\n" +
                $"Подробности записаны в:\n{log}",
                "Wikar Island — ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // даже окно показать не получилось
        }
    }

    private void StartApp()
    {
        // Windows 10 старше 1809 (сборка 17763) не умеет системные функции, на которых держится остров
        if (Environment.OSVersion.Version.Build < 17763)
            throw new PlatformNotSupportedException(
                $"Нужна Windows 10 версии 1809 (сборка 17763) или новее, а здесь сборка {Environment.OSVersion.Version.Build}. Обнови Windows.");

        Settings = AppSettings.Load();
        Theme.Install(Resources);
        MigrateAiModel();
        // ключ Groq переносим в отдельное поле слуха — чтобы смена «мозга» (Ollama, Gemini) не выключала Whisper
        if (Settings.GroqKey.Length == 0 && Settings.AiBaseUrl.Contains("groq.com", StringComparison.OrdinalIgnoreCase) && Settings.AiKey.Length > 0)
        {
            Settings.GroqKey = Settings.AiKey;
            Settings.Save();
        }
        System.Threading.Tasks.Task.Run(UpdateService.CleanupAfterUpdate);
        // обновить путь, если exe переехал, и починить задачу автозапуска (в фоне: schtasks/COM — до пары секунд)
        if (Settings.AutoStart)
        {
            bool elevated = Settings.AutoStartElevated;
            System.Threading.Tasks.Task.Run(() => AutoStart.Ensure(elevated));
        }

        Media = new MediaService();
        Music = new MusicHub();
        Weather = new WeatherService();
        Audio = new AudioService();
        Notifications = new NotificationService();
        ClipboardHistory = new ClipboardService();
        Steam = new SteamService();
        PowerTimer = new PowerTimerService();
        Notes = new NotesService();
        Recorder = new RecorderService();
        AudioRecorder = new AudioRecorderService();
        SystemMonitor = new SystemMonitorService();
        Vpn = new VpnService();
        Zapret = new ZapretService();
        Updater = new UpdateService();
        FocusTimer = new FocusTimerService();
        Reminders = new ReminderService();
        Game = new GameService();
        Downloads = new DownloadService();
        Speech = new SpeechService();
        Voice = new VoiceService();
        Assistant = new AssistantService();
        Batteries = new DeviceBatteryService();
        Google = new GoogleService();
        ZapretService.MigrateSettings();
        Safe("VPN", Vpn.RecoverAfterCrash);

        Panel = new PanelWindow();
        Pill = new PillWindow();
        Pill.Show();

        Media.Changed += () =>
        {
            Pill.UpdateMedia();
            Panel.UpdateMedia();
        };
        Weather.Changed += () =>
        {
            Pill.UpdateWeather();
            Panel.UpdateWeather();
        };
        Notifications.Arrived += item =>
        {
            if (FocusService.Active) Pill.CountMissed(); // «Не беспокоить»: только в истории
            else Pill.ShowNotification(item);
        };
        FocusTimer.Changed += () =>
        {
            Pill.UpdateFocus();
            Panel.UpdateFocus();
        };
        FocusTimer.Finished += (title, body) => Pill.ShowNotification(new NotificationItem
        {
            AppId = "wikar.timer",
            AppName = "Таймер",
            Title = title,
            Body = body,
            Time = DateTime.Now,
            Click = () => Panel.ShowFocus(),
        });
        Reminders.Changed += () => Panel.UpdateFocus();
        Reminders.Fired += r =>
        {
            FocusTimerService.PlayChime();
            Pill.ShowNotification(new NotificationItem
            {
                AppId = "wikar.reminder",
                AppName = "Напоминание",
                Title = r.Text,
                Body = (r.Repeat != "none" ? $"Повтор: {r.RepeatName} · " : "") + "нажми, чтобы отложить на 10 минут",
                Time = DateTime.Now,
                Click = () =>
                {
                    if (r.Repeat == "none") Reminders.Snooze(r, 10);
                    else Reminders.Add(r.Text, DateTime.Now.AddMinutes(10), snooze: true);
                },
            });
        };
        Notifications.HistoryChanged += () =>
        {
            Pill.UpdateDiscordBadge();
            Panel.UpdateNotifications();
        };

        ClipboardHistory.Changed += () => Panel.UpdateClipboard();
        Safe("буфер обмена", () => ClipboardHistory.Attach(Pill));

        Steam.Changed += () =>
        {
            Pill.UpdateSteam();
            Panel.UpdateSteam();
        };
        Steam.Finished += (id, name) => Pill.ShowNotification(new NotificationItem
        {
            AppId = "steam",
            AppName = "Steam",
            AppIcon = Steam.SteamIcon,
            Title = $"{name} — готово",
            Body = "Загрузка завершена. Нажми, чтобы запустить игру",
            Time = DateTime.Now,
            Click = () => SteamService.Launch(id),
        });

        PowerTimer.Changed += () =>
        {
            Pill.UpdateTimer();
            Panel.UpdateTimer();
        };

        Recorder.StateChanged += () =>
        {
            Pill.UpdateRecording();
            Panel.UpdateRecording();
        };
        Recorder.Finished += (path, error) => Pill.ShowNotification(path != null
            ? new NotificationItem
            {
                AppId = "recorder",
                AppName = "Запись экрана",
                AppIcon = ShellHelper.GetIcon(path),
                Title = "Запись сохранена",
                Body = System.IO.Path.GetFileName(path) + " — нажми, чтобы открыть",
                Time = DateTime.Now,
                Click = () => ShellHelper.Open(path),
            }
            : new NotificationItem
            {
                AppId = "recorder",
                AppName = "Запись экрана",
                Title = "Не получилось записать экран",
                Body = error ?? "Подробности в error.log",
                Time = DateTime.Now,
                Click = () => { },
            });

        AudioRecorder.StateChanged += () =>
        {
            Pill.UpdateRecording();
            Panel.UpdateRecording();
        };
        AudioRecorder.Finished += (path, error) => Pill.ShowNotification(path != null
            ? new NotificationItem
            {
                AppId = "audiorecorder",
                AppName = "Запись звука",
                AppIcon = ShellHelper.GetIcon(path),
                Title = "Звук записан",
                Body = System.IO.Path.GetFileName(path) + " — нажми, чтобы открыть",
                Time = DateTime.Now,
                Click = () => ShellHelper.Open(path),
            }
            : new NotificationItem
            {
                AppId = "audiorecorder",
                AppName = "Запись звука",
                Title = "Не получилось записать звук",
                Body = error ?? "Подробности в error.log",
                Time = DateTime.Now,
                Click = () => { },
            });

        SystemMonitor.Updated += () => Panel.Dispatcher.BeginInvoke(() => Panel.UpdateSystem());

        Vpn.StateChanged += () =>
        {
            Pill.UpdateVpn();
            Panel.UpdateVpn();
        };
        Vpn.AdminRequired += () =>
        {
            MessageBoxResult answer = MessageBox.Show(
                "Для режима TUN (весь трафик компьютера через VPN) острову нужны права администратора — без них Windows не даёт создать сетевой адаптер.\n\n" +
                "Перезапустить Wikar Island от администратора? VPN включится сам.\n\n" +
                "Чтобы не спрашивать каждый раз: Настройки → Основные → «Запуск» → «С правами администратора».",
                "Wikar Island — VPN", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes && VpnService.RestartForTun()) Quit();
        };
        Vpn.Connected += (exit, error) => Pill.ShowNotification(new NotificationItem
        {
            AppId = "vpn",
            AppName = "VPN",
            Title = exit != null ? $"VPN подключён · {exit.Country}" : "VPN не подключился",
            Body = exit != null ? $"{Vpn.ServerName} · IP {exit.Ip}" : error ?? "",
            Time = DateTime.Now,
            Click = () => { },
        });

        Safe("музыка", () => _ = Media.StartAsync());
        Safe("встроенная музыка", Music.Start);
        Music.Changed += () => Panel.UpdateMusic();
        Safe("Steam", Steam.Start);
        Game.Changed += () => Pill.UpdateGame();
        Downloads.Changed += () => Pill.UpdateDownloads();
        Downloads.FileFinished += (name, path, icon) =>
        {
            if (!Settings.DownloadFinishedCard) return;
            var item = new NotificationItem
            {
                AppId = "wikar.download",
                AppName = "Загрузки",
                AppIcon = icon,
                Title = $"Скачано: {name}",
                Body = "Нажми, чтобы показать в папке",
                Time = DateTime.Now,
                Click = () => ShellHelper.ShowInExplorer(path),
            };
            if (FocusService.Active) Pill.CountMissed();
            else Pill.ShowNotification(item);
        };
        Safe("загрузки", Downloads.Start);
        Voice.Changed += () =>
        {
            Pill.UpdateVoice();
            Panel.UpdateVoiceStatus();
        };
        if (Settings.VoiceEnabled) AppIndex.BuildInBackground(); // чтобы первая «запусти …» не ждала сканирования
        if (Settings.VoiceEnabled) Safe("голосовой помощник", Voice.Start);
        Safe("погода", Weather.Start);
        Safe("уведомления", Notifications.Start);

        Batteries.Changed += () => Panel.Dispatcher.BeginInvoke(() => Panel.UpdateBatteries());
        Batteries.Low += (device, threshold) => Pill.Dispatcher.BeginInvoke(() => OnBatteryLow(device, threshold));
        if (Settings.BatteryEnabled) Safe("заряд устройств", Batteries.Start);
        Google.Changed += () => Pill.Dispatcher.BeginInvoke(() =>
        {
            Pill.UpdateAgenda();
            Panel.UpdateAgenda();
        });
        Google.EventSoon += e => Pill.Dispatcher.BeginInvoke(() => OnEventSoon(e));
        Reminders.Added += r =>
        {
            if (!Settings.GoogleTasksSync || !Google.IsSignedIn) return;
            string notes = "Напоминание Капсулы: " + (r.Repeat != "none" ? r.RepeatName + ", " : "") + r.Due.ToString("dd.MM HH:mm");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try { await Google.AddTaskAsync(r.Text, r.Due.Date, notes); }
                catch (Exception ex) { LogError(new InvalidOperationException("Google Задачи: напоминание не добавилось", ex)); }
            });
        };
        Safe("Google", Google.Start);

        Safe("трей", CreateTray);

        Zapret.StateChanged += () => Pill.Dispatcher.BeginInvoke(() =>
        {
            Pill.UpdateZapret();
            Panel.UpdateZapret();
        });
        if (Settings.ZapretAutoStart && Zapret.Installed && SystemMonitorService.IsAdmin)
            Safe("zapret", () => Zapret.Start());

        // проверка обновлений — через полминуты после запуска, раз в сутки
        var updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(6);
            await Updater.AutoCheckAsync();
        };
        updateTimer.Start();
        Pill.UpdateZapret();
    }

    /// <summary>До события в Google Календаре 10 минут: карточка и (по желанию) голос.</summary>
    private static void OnEventSoon(CalendarEvent e)
    {
        int min = Math.Max(1, (int)Math.Round((e.Start - DateTime.Now).TotalMinutes));
        var item = new NotificationItem
        {
            AppId = "wikar.calendar",
            AppName = "Календарь",
            Title = e.Title,
            Body = $"Через {min} мин · {e.Start:HH:mm}–{e.End:HH:mm}" + (string.IsNullOrWhiteSpace(e.Location) ? "" : " · " + e.Location),
            Time = DateTime.Now,
            Click = () => Panel.ShowTab("home", activate: true),
        };
        if (FocusService.Active) { Pill.CountMissed(); return; }
        Pill.ShowNotification(item);
        if (Settings.GoogleEventVoice && !Speech.IsSpeaking && Voice.State != VoiceState.Speaking)
            _ = Speech.SpeakAsync($"Сэр, через {min} {Minutes(min)} — {e.Title}.");
    }

    private static string Minutes(int n)
    {
        int a = n % 100, b = n % 10;
        if (a is >= 11 and <= 14) return "минут";
        return b switch { 1 => "минуту", 2 or 3 or 4 => "минуты", _ => "минут" };
    }

    /// <summary>Заряд наушников/мыши дошёл до 20, 10 или 5%: карточка в капсуле и голосом «поставьте на зарядку».</summary>
    private static void OnBatteryLow(DeviceBattery device, int threshold)
    {
        var item = new NotificationItem
        {
            AppId = "wikar.battery",
            AppName = "Заряд",
            Title = $"{device.Name}: {device.Percent}%",
            Body = threshold <= 5 ? "Почти разряжено — поставьте на зарядку" : "Не забудьте поставить устройство на зарядку",
            Time = DateTime.Now,
            Click = () => Panel.ShowTab("system", activate: true),
        };
        if (FocusService.Active) Pill.CountMissed();
        else Pill.ShowNotification(item);

        if (!Settings.BatteryVoice || FocusService.Active || Speech.IsSpeaking) return;
        string what = device.Kind == BatteryDeviceKind.Other ? device.Name : $"{device.KindWord} {device.Name}";
        string text = threshold <= 5
            ? $"Сэр, {what} почти разряжены: {device.Percent} {DeviceBatteryService.Percent(device.Percent)}. Поставьте устройство на зарядку."
            : $"Сэр, у {Genitive(device.Kind, device.Name)} осталось {device.Percent} {DeviceBatteryService.Percent(device.Percent)}. Не забудьте поставить устройство на зарядку.";
        if (threshold <= 5 && device.Kind is BatteryDeviceKind.Mouse or BatteryDeviceKind.Keyboard)
            text = text.Replace("почти разряжены", "почти разряжена");
        if (threshold <= 5 && device.Kind is BatteryDeviceKind.Gamepad or BatteryDeviceKind.Other)
            text = text.Replace("почти разряжены", "почти разряжен");
        _ = Speech.SpeakAsync(text);
    }

    private static string Genitive(BatteryDeviceKind kind, string name) => kind switch
    {
        BatteryDeviceKind.Headset => "наушников " + name,
        BatteryDeviceKind.Mouse => "мыши " + name,
        BatteryDeviceKind.Keyboard => "клавиатуры " + name,
        BatteryDeviceKind.Gamepad => "геймпада " + name,
        _ => name,
    };

    private static void CreateTray()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Открыть остров", null, (_, _) => Panel.Toggle(activate: true));
        menu.Items.Add("Настройки", null, (_, _) => Panel.ShowTab("settings", activate: true));
        var dnd = new WF.ToolStripMenuItem("Не беспокоить") { CheckOnClick = true };
        dnd.Click += (_, _) => SetDnd(dnd.Checked);
        menu.Opening += (_, _) => dnd.Checked = Settings.DndManual;
        menu.Items.Add(dnd);
        menu.Items.Add("Вернуть пилюлю по центру", null, (_, _) => Pill.ResetPosition());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Quit());

        _tray = new WF.NotifyIcon
        {
            Icon = TrayIcon.Create(),
            Text = "Wikar Island",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, a) =>
        {
            if (a.Button == WF.MouseButtons.Left) Panel.Toggle(activate: true);
        };
    }

    /// <summary>До какого времени включён «Не беспокоить» из центра управления (null — без срока).</summary>
    public static DateTime? DndUntil { get; private set; }
    private static System.Windows.Threading.DispatcherTimer? _dndTimer;

    /// <summary>Включить «Не беспокоить» на время: 30 мин, 1 ч, 2 ч.</summary>
    public static void SetDndFor(TimeSpan duration)
    {
        SetDnd(true);
        DndUntil = DateTime.Now + duration;
        _dndTimer ??= CreateDndTimer();
        _dndTimer.Start();
        Panel?.UpdateDnd();
    }

    private static System.Windows.Threading.DispatcherTimer CreateDndTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        timer.Tick += (_, _) =>
        {
            if (DndUntil is DateTime until && DateTime.Now >= until) SetDnd(false);
        };
        return timer;
    }

    public static void SetDnd(bool on)
    {
        DndUntil = null;
        _dndTimer?.Stop();
        Settings.DndManual = on;
        Settings.Save();
        Pill?.UpdateDnd();
        Panel?.UpdateDnd();
    }

    private static bool _skipSave;

    /// <summary>Перезапуск острова; saveSettings=false — когда настройки только что подменили из копии.</summary>
    public static void Restart(bool saveSettings = true)
    {
        if (Environment.ProcessPath is not { } exe) return;
        _skipSave = !saveSettings;
        if (!saveSettings) AppSettings.Frozen = true;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = true });
        Quit();
    }

    public static void Quit()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        if (!_skipSave)
        {
            Settings.Save();
            Notes?.Save();
        }
        Recorder?.Stop();
        AudioRecorder?.Stop();
        Spectrum.Dispose();
        Game?.Dispose();
        Speech?.Stop();
        Voice?.Dispose();
        Music?.Dispose();
        SystemMonitor?.Dispose();
        Vpn?.Disconnect();
        Zapret?.Stop();
        Current.Shutdown();
    }

    /// <summary>Сервисы убирают старые модели: меняем их на актуальные, чтобы ИИ не отвечал 404.</summary>
    private static void MigrateAiModel()
    {
        string model = Settings.AiModel;
        string url = Settings.AiBaseUrl;
        string? replacement =
            url.Contains("groq.com", StringComparison.OrdinalIgnoreCase) && model is "llama-3.3-70b-versatile" or "llama-3.1-8b-instant" or "llama3-70b-8192"
                ? "openai/gpt-oss-120b"
            : url.Contains("generativelanguage", StringComparison.OrdinalIgnoreCase) && model.StartsWith("gemini-2", StringComparison.OrdinalIgnoreCase)
                ? "gemini-3.8-flash"
            : null;
        if (replacement == null) return;
        Settings.AiModel = replacement;
        Settings.Save();
    }

    /// <summary>
    /// Журнал голосовых команд (voice.log): что услышали и что сделали. Нужен, чтобы понять, почему команда не сработала.
    /// Держим не больше ~300 КБ — старое обрезается.
    /// </summary>
    public static void LogVoice(string line)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            string path = Path.Combine(AppSettings.DataDir, "voice.log");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 300_000)
            {
                string[] lines = File.ReadAllLines(path);
                File.WriteAllLines(path, lines[(lines.Length / 2)..]);
            }
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}\n");
        }
        catch
        {
            // журнал не записался — не страшно
        }
    }

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.AppendAllText(Path.Combine(AppSettings.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch
        {
            // лог не записался — ничего не поделать
        }
    }
}

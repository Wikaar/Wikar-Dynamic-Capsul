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
    public static SpectrumService Spectrum { get; } = new();

    private static Mutex? _instanceMutex;
    private static WF.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
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
        if (Settings.AutoStart && !Settings.AutoStartElevated) AutoStart.Apply(true, false); // обновить путь, если exe переехал

        Media = new MediaService();
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
                    else Reminders.Add(r.Text, DateTime.Now.AddMinutes(10));
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
        Safe("Steam", Steam.Start);
        Safe("погода", Weather.Start);
        Safe("уведомления", Notifications.Start);

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
        SystemMonitor?.Dispose();
        Vpn?.Disconnect();
        Zapret?.Stop();
        Current.Shutdown();
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

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

    private static Mutex? _instanceMutex;
    private static WF.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        // второй экземпляр не запускаем
        _instanceMutex = new Mutex(true, "WikarIsland.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };

        Settings = AppSettings.Load();
        if (Settings.AutoStart) AutoStart.Set(true); // обновить путь, если exe переехал

        Media = new MediaService();
        Weather = new WeatherService();
        Audio = new AudioService();
        Notifications = new NotificationService();
        ClipboardHistory = new ClipboardService();
        Steam = new SteamService();
        PowerTimer = new PowerTimerService();
        Notes = new NotesService();

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
        Notifications.Arrived += item => Pill.ShowNotification(item);
        Notifications.HistoryChanged += () =>
        {
            Pill.UpdateDiscordBadge();
            Panel.UpdateNotifications();
        };

        ClipboardHistory.Changed += () => Panel.UpdateClipboard();
        ClipboardHistory.Attach(Pill);

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

        _ = Media.StartAsync();
        Steam.Start();
        Weather.Start();
        Notifications.Start();

        CreateTray();
    }

    private static void CreateTray()
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add("Открыть остров", null, (_, _) => Panel.Toggle(activate: true));
        menu.Items.Add("Настройки", null, (_, _) => Panel.ShowTab("settings", activate: true));
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

    public static void Quit()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        Settings.Save();
        Notes?.Save();
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

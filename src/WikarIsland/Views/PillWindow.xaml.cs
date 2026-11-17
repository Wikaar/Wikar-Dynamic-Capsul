using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WikarIsland.Services;
using WF = System.Windows.Forms;

namespace WikarIsland.Views;

/// <summary>
/// Пилюля поверх всех окон. Не забирает фокус, раскрывает панель при наведении,
/// показывает музыку, погоду, громкость; умеет открепляться, перетаскиваться,
/// прилипать к краям и прятаться за верхний край экрана.
/// </summary>
public partial class PillWindow : Window
{
    private const double SnapDistance = 28;
    private const double SideMargin = 8; // поле вокруг пилюли внутри окна (под тень)

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private HotkeyService? _hotkey;
    private HotkeyService? _commandHotkey;
    private HotkeyService? _voiceHotkey;
    private CommandWindow? _commandBar;
    private bool _hidden;
    private DateTime? _awaySince;
    private DateTime _volumeUntil;
    private DateTime _lastPlaying;
    private readonly Queue<NotificationItem> _cards = new();
    private NotificationItem? _card;
    private DateTime _cardUntil;
    private double _pillHeight = 34;

    public PillWindow()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            UpdateTime();
            FocusService.Poll();
            App.Game.Tick();
            App.FocusTimer.Tick();
            App.Reminders.Tick();
            if (App.FocusTimer.Active) UpdateFocus();
            UpdateDnd();
            UpdateContent();
            CheckDiscordFocused();
        };

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            App.Panel.ShowNearPill(activate: false);
        };

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _autoHideTimer.Tick += (_, _) =>
        {
            CardTick();
            AutoHideTick();
        };

        BuildContextMenu();

        Liquid.Glow = PillGlow;
        // пока Капсулка слушает — капсула дышит в такт голосу, иначе — в такт музыке
        Liquid.AudioLevel = () => App.Voice?.State == VoiceState.Listening ? App.Voice.MicLevel : App.Audio.Peak;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExStyle(handle, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        _hotkey = new HotkeyService(this);
        _hotkey.Pressed += () => App.Panel.Toggle(activate: true);
        _hotkey.Register(App.Settings.Hotkey, out _);
        _commandHotkey = new HotkeyService(this, 0xB002);
        _commandHotkey.Pressed += OpenCommandBar;
        _commandHotkey.Register(App.Settings.CommandHotkey, out _);
        _voiceHotkey = new HotkeyService(this, 0xB003);
        _voiceHotkey.Pressed += () => App.Voice.ListenNow();
        if (App.Settings.VoiceEnabled) _voiceHotkey.Register(App.Settings.VoiceHotkey, out _);

        RestorePosition();
        ApplySettings();
        UpdateTime();
        _clockTimer.Start();
        _autoHideTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkey?.Dispose();
        _commandHotkey?.Dispose();
        _voiceHotkey?.Dispose();
        base.OnClosed(e);
    }

    public bool SetCommandHotkey(string text, out string error)
    {
        error = "";
        if (_commandHotkey == null) return false;
        if (!_commandHotkey.Register(text, out error))
        {
            _commandHotkey.Register(App.Settings.CommandHotkey, out _);
            return false;
        }
        App.Settings.CommandHotkey = text;
        App.Settings.Save();
        return true;
    }

    /// <summary>Быстрая строка под капсулой.</summary>
    public void OpenCommandBar()
    {
        if (_commandBar != null)
        {
            _commandBar.Activate();
            return;
        }
        if (_hidden) SlideIn();
        _commandBar = new CommandWindow();
        _commandBar.Closed += (_, _) => _commandBar = null;
        _commandBar.ShowUnder(PillRect());
    }

    /// <summary>Клавиша «говорить без слова». Пустая строка — снять.</summary>
    public bool SetVoiceHotkey(string text, out string error)
    {
        error = "";
        if (_voiceHotkey == null) return false;
        if (string.IsNullOrWhiteSpace(text) || !App.Settings.VoiceEnabled)
        {
            _voiceHotkey.Unregister();
            return true;
        }
        if (!_voiceHotkey.Register(text, out error))
        {
            _voiceHotkey.Register(App.Settings.VoiceHotkey, out _);
            return false;
        }
        App.Settings.VoiceHotkey = text;
        App.Settings.Save();
        return true;
    }

    public bool SetHotkey(string text, out string error)
    {
        error = "";
        if (_hotkey == null) return false;
        if (!_hotkey.Register(text, out error))
        {
            _hotkey.Register(App.Settings.Hotkey, out _); // вернуть прежнюю
            return false;
        }
        App.Settings.Hotkey = text;
        App.Settings.Save();
        return true;
    }

    /// <summary>Применить вид пилюли: глаза/часы/погода и рамку открепления.</summary>
    public void ApplySettings()
    {
        AppSettings s = App.Settings;
        string mode = s.EyesEnabled ? s.PillMode : "clock";

        Eyes.Visibility = mode == "clock" ? Visibility.Collapsed : Visibility.Visible;
        Eyes.EyeStyle = s.EyeStyle;
        TimeText.Visibility = mode == "eyes" ? Visibility.Collapsed : Visibility.Visible;
        TimeText.Margin = new Thickness(mode == "clock" && PillWeather.Visibility != Visibility.Visible ? 2 : 10, 0, 2, 0);

        Color? neon = s.NeonPill ? PanelWindow.NeonColor() : null;
        Liquid.NeonColor = neon;
        Liquid.Outline = s.Unlocked
            ? new Pen((Brush)FindResource("Accent"), 1.6)
            : neon is Color n
                ? new Pen(new SolidColorBrush(n), 1.6)
                : new Pen(new SolidColorBrush(Theme.Argb(0x26, 0xFF, 0xFF, 0xFF)), 1);
        Liquid.GlowEnabled = s.MusicGlow;
        ApplyPillFill();
        if (App.Media != null) UpdateMedia();
        Liquid.InvalidateVisual();
        UpdateFlush();
        Pill.Cursor = s.Unlocked ? Cursors.SizeAll : Cursors.Arrow;

        UpdateWeather();
        if (App.Vpn != null) UpdateVpn();
        UpdateContent();
    }

    private void UpdateTime() => TimeText.Text = DateTime.Now.ToString("HH:mm");

    // ---------- содержимое: громкость > музыка > обычное ----------

    public void UpdateMedia()
    {
        MediaInfo? m = App.Media.Current;
        if (m != null)
        {
            PillCover.Source = m.Cover ?? m.AppIcon;
            PillAppIcon.Source = m.Cover != null ? m.AppIcon : null;
            PillTrack.Text = string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} — {m.Artist}";
            Eq.SetPlaying(m.IsPlaying && App.Settings.MusicInPill);
            Liquid.MusicPlaying = m.IsPlaying && App.Settings.MusicInPill;
            Liquid.AccentColor = m.Accent ?? Colors.Black;
            Eq.BarBrush = new SolidColorBrush(Theme.Map(Lighten(m.Accent ?? Colors.White)));
            if (m.IsPlaying) _lastPlaying = DateTime.Now;
            ApplyPillFill();
        }
        else
        {
            Eq.SetPlaying(false);
            Liquid.MusicPlaying = false;
            ApplyPillFill();
        }
        UpdateContent();
    }

    public static readonly (string key, string name, Color color)[] Themes =
    {
        ("black", "Чёрная", Theme.Rgb(0x0B, 0x0B, 0x0E)),
        ("graphite", "Графит", Theme.Rgb(0x25, 0x26, 0x2D)),
        ("navy", "Ночная синяя", Theme.Rgb(0x10, 0x1A, 0x36)),
        ("plum", "Слива", Theme.Rgb(0x26, 0x14, 0x36)),
        ("forest", "Хвоя", Theme.Rgb(0x0F, 0x28, 0x22)),
        ("wine", "Вино", Theme.Rgb(0x2E, 0x10, 0x18)),
    };

    /// <summary>Цвет капсулы: тема + прозрачность + лёгкий оттенок обложки, пока играет музыка.</summary>
    private void ApplyPillFill()
    {
        AppSettings s = App.Settings;
        Color baseColor = Array.Find(Themes, t => t.key == s.PillTheme).color;
        if (baseColor == default) baseColor = Themes[0].color;
        MediaInfo? m = App.Media?.Current;
        if (s.MusicTint && m is { IsPlaying: true, Accent: Color accent } && s.MusicInPill)
            baseColor = Blend(baseColor, accent, 0.22);
        byte alpha = (byte)Math.Round(Math.Clamp(s.PillOpacity, 0.4, 1.0) * 255);
        var target = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);

        if (Liquid.Fill is SolidColorBrush current && !current.IsFrozen)
        {
            if (current.Color != target)
                current.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(target, TimeSpan.FromMilliseconds(600)));
        }
        else
        {
            Liquid.Fill = new SolidColorBrush(target);
        }
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>Тёмные обложки дают почти чёрные столбики — подсветляем.</summary>
    private static Color Lighten(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;
        return lum >= 0.55 ? c : Blend(c, Colors.White, 0.55 - lum);
    }

    public void UpdateWeather()
    {
        WeatherData? w = App.Weather.Current;
        bool show = App.Settings.WeatherInPill && w != null && App.Settings.PillMode != "eyes";
        PillWeather.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (w != null)
        {
            PillWeatherIcon.Symbol = w.Symbol;
            PillWeatherText.Text = w.TempText;
        }
        PillWeather.Margin = new Thickness(Eyes.Visibility == Visibility.Visible ? 10 : 2, 0, 0, 0);
    }

    private void UpdateContent()
    {
        // приоритет: таймер (последняя минута) > уведомление > громкость > игра (FPS) > музыка > загрузки > обычное
        MediaInfo? m = App.Media?.Current;
        bool timer = App.PowerTimer?.InWarning == true;
        bool recording = !timer && (App.Recorder?.IsRecording == true || App.AudioRecorder?.IsRecording == true);
        bool card = !timer && _card != null;
        bool volume = !timer && !card && DateTime.Now < _volumeUntil;
        if (card || volume) recording = false;
        bool voice = !timer && App.Voice?.State is VoiceState.Listening or VoiceState.Thinking or VoiceState.Speaking;
        if (voice)
        {
            card = false;
            volume = false;
            recording = false;
        }
        bool game = !timer && !voice && !recording && !card && !volume && App.Settings.GameHud && App.Game?.Active == true;
        bool music = !timer && !voice && !recording && !card && !volume && !game && App.Settings.MusicInPill && m != null
                     && (m.IsPlaying || (DateTime.Now - _lastPlaying).TotalSeconds < 15);
        bool steam = !timer && !voice && !recording && !card && !volume && !game && !music
                     && App.Settings.DownloadsInPill && App.Downloads?.Items.Count > 0;
        bool chips = !timer && !voice && !recording && !card && !volume && !game; // в игре — только самое нужное

        TimerPart.Visibility = timer ? Visibility.Visible : Visibility.Collapsed;
        RecordPart.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        if (recording)
        {
            bool video = App.Recorder!.IsRecording;
            TimeSpan t = DateTime.Now - (video ? App.Recorder.StartedAt : App.AudioRecorder!.StartedAt);
            RecordTime.Text = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
            RecordLabel.Text = video ? "Запись экрана" : "Запись звука";
        }
        NotifyPart.Visibility = card ? Visibility.Visible : Visibility.Collapsed;
        VoicePart.Visibility = voice ? Visibility.Visible : Visibility.Collapsed;
        VolumePart.Visibility = volume ? Visibility.Visible : Visibility.Collapsed;
        MusicPart.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        DownloadPart.Visibility = steam ? Visibility.Visible : Visibility.Collapsed;
        GamePart.Visibility = game ? Visibility.Visible : Visibility.Collapsed;
        IdlePart.Visibility = !timer && !voice && !recording && !card && !volume && !game && !music && !steam ? Visibility.Visible : Visibility.Collapsed;
        VpnChip.Visibility = chips && App.Settings.VpnServers.Count > 0
                             && (App.Settings.VpnShowInPill || App.Vpn?.IsConnected == true)
            ? Visibility.Visible : Visibility.Collapsed;
        FocusChip.Visibility = chips && App.FocusTimer?.Active == true
            ? Visibility.Visible : Visibility.Collapsed;
        DndIcon.Visibility = !timer && !voice && !recording && !card && !volume && FocusService.Active
            ? Visibility.Visible : Visibility.Collapsed;
        ZapretChip.Visibility = chips && App.Zapret?.Installed == true
                                && (App.Settings.ZapretShowInPill || App.Zapret.IsRunning)
            ? Visibility.Visible : Visibility.Collapsed;

        if (timer)
            TimerTitle.Text = $"{PowerTimerService.ActionName(App.PowerTimer!.Action)} через {Math.Ceiling(App.PowerTimer.Remaining.TotalSeconds):0} с";

        double height = timer || card ? 58 : 34;
        if (height != _pillHeight)
        {
            _pillHeight = height;
            if (_hidden && height > 34) SlideIn();
            AnimatePillHeight(height, height > 34 ? 22 : 17);
        }
    }

    private void ShowVolume()
    {
        float level = App.Audio.Volume;
        if (level < 0) return;
        bool muted = App.Audio.Muted;
        int percent = (int)Math.Round(level * 100);

        VolumeText.Text = muted ? "0" : percent.ToString();
        VolumeFill.Width = 90 * (muted ? 0 : level);
        VolumeIcon.Text = muted || percent == 0 ? "" : percent < 34 ? "" : percent < 67 ? "" : "";

        _volumeUntil = DateTime.Now.AddSeconds(1.4);
        UpdateContent();
        var hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            UpdateContent();
        };
        hide.Start();
    }

    private void Pill_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        App.Audio.Step(e.Delta > 0 ? 0.02f : -0.02f);
        ShowVolume();
        App.Panel.RefreshSound();
        e.Handled = true;
    }

    // ---------- таймер / Pomodoro ----------

    public void UpdateFocus()
    {
        FocusTimerService f = App.FocusTimer;
        if (!f.Active)
        {
            UpdateContent();
            return;
        }
        Color color = f.Paused ? Theme.Rgb(0x8A, 0x8A, 0x94)
            : f.Pomodoro && f.IsBreak ? Theme.Rgb(0x6E, 0xE0, 0x9A)
            : f.Pomodoro ? Theme.Rgb(0xFF, 0x7A, 0x6B)
            : Theme.Rgb(0x9C, 0xB0, 0xFF);
        var brush = new SolidColorBrush(color);
        FocusText.Text = FocusTimerService.Format(f.Remaining);
        FocusText.Foreground = brush;
        FocusIcon.Foreground = brush;
        FocusIcon.Text = f.Paused ? "\uE769" : f.Pomodoro ? (f.IsBreak ? "\uEC32" : "\uE945") : "\uE916";
        FocusRing.Stroke = brush;
        double p = f.Progress;
        FocusRing.StrokeDashArray = new DoubleCollection { p * 44.0 / FocusRing.StrokeThickness, 1000 };
        FocusChip.ToolTip = $"{f.Label} · осталось {FocusTimerService.Format(f.Remaining)}\nНажми — {(f.Paused ? "продолжить" : "пауза")}, правой кнопкой — остановить";
        UpdateContent();
    }

    private void FocusChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        App.FocusTimer.TogglePause();
    }

    private void FocusChip_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        App.FocusTimer.Stop();
    }

    // ---------- «Не беспокоить» ----------

    private int _missed;
    private bool _dndWas;
    private bool _pillHiddenForFullscreen;

    /// <summary>Уведомление пришло в режиме «Не беспокоить» — запоминаем, покажем итог потом.</summary>
    public void CountMissed()
    {
        _missed++;
        UpdateDiscordBadge();
    }

    public void UpdateDnd()
    {
        bool active = FocusService.Active;
        if (_dndWas && !active && _missed > 0)
        {
            int n = _missed;
            _missed = 0;
            ShowNotification(new NotificationItem
            {
                AppId = "wikar.dnd",
                AppName = "Wikar Island",
                Title = "Пока было «Не беспокоить»",
                Body = $"Пришло уведомлений: {n}. Нажми, чтобы посмотреть",
                Time = DateTime.Now,
                Click = () => App.Panel.ShowTab("control", activate: true),
            });
        }
        _dndWas = active;

        // игра или видео на весь экран — прячем саму капсулу (кроме предупреждения таймера выключения)
        bool hide = FocusService.HidePill && App.PowerTimer?.InWarning != true && !App.Panel.IsVisible;
        if (hide != _pillHiddenForFullscreen)
        {
            _pillHiddenForFullscreen = hide;
            BeginAnimation(OpacityProperty, new DoubleAnimation(hide ? 0 : 1, TimeSpan.FromMilliseconds(250)));
        }
    }

    private void DndIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (App.Settings.DndManual) App.SetDnd(false);
        else App.Panel.ShowTab("settings", activate: true);
    }

    /// <summary>Вышла новая версия острова — карточка, по клику открываем настройки с кнопкой обновления.</summary>
    public void ShowUpdateAvailable(string tag) => ShowNotification(new NotificationItem
    {
        AppId = "wikar.update",
        AppName = "Wikar Island",
        Title = $"Доступна версия {tag}",
        Body = "Нажми, чтобы обновить остров",
        Time = DateTime.Now,
        Click = () => App.Panel.ShowTab("settings", activate: true),
    });

    // ---------- уведомления ----------

    public void ShowNotification(NotificationItem item)
    {
        _cards.Enqueue(item);
        while (_cards.Count > 5) _cards.Dequeue();
        if (_card == null) ShowNextCard();
        UpdateDiscordBadge();
    }

    private void ShowNextCard()
    {
        if (_cards.Count == 0) return;
        _card = _cards.Dequeue();
        NotifyIcon.Source = _card.AppIcon;
        NotifyTitle.Text = _card.Title;
        NotifyBody.Text = string.IsNullOrEmpty(_card.Body) ? _card.AppName : _card.Body;
        NotifyPart.ToolTip = $"{_card.AppName}: нажми, чтобы открыть, или ✕, чтобы закрыть";
        _cardUntil = DateTime.Now.AddSeconds(6);

        _hoverTimer.Stop();
        Liquid.Pulse();
        UpdateContent();
    }

    private void CloseCard()
    {
        _card = null;
        if (_cards.Count > 0)
        {
            ShowNextCard();
            return;
        }
        UpdateContent();
    }

    /// <summary>Крестик на карточке: закрыть, ничего не открывая. Остальные уведомления в очереди тоже убираем.</summary>
    private void NotifyClose_Click(object sender, RoutedEventArgs e)
    {
        if (_card != null) App.Notifications.Dismiss(_card);
        _cards.Clear();
        CloseCard();
        e.Handled = true;
    }

    private void CardTick()
    {
        if (_card == null) return;
        if (Pill.IsMouseOver) _cardUntil = DateTime.Now.AddSeconds(2); // читают — не убираем
        if (DateTime.Now > _cardUntil) CloseCard();
    }

    private void AnimatePillHeight(double height, double radius)
    {
        Liquid.CornerRadius = radius;
        Pill.BeginAnimation(HeightProperty, new DoubleAnimation(height, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    public void UpdateDiscordBadge()
    {
        int count = App.Notifications.DiscordUnread;
        DiscordBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DiscordCount.Text = count > 99 ? "99+" : count.ToString();
        foreach (NotificationItem n in App.Notifications.History)
        {
            if (!n.IsDiscord) continue;
            DiscordIcon.Source = n.AppIcon;
            break;
        }
    }

    /// <summary>Steam сообщил о загрузке — сама строка собирается в UpdateDownloads.</summary>
    public void UpdateSteam() => UpdateContent();

    private bool _shimmer;

    /// <summary>Строка загрузки: первая из списка, остальные — «+N» и подсказкой.</summary>
    public void UpdateDownloads()
    {
        List<DownloadItem> items = App.Downloads.Items;
        if (items.Count > 0)
        {
            // сначала те, у которых известен прогресс, потом самые быстрые
            DownloadItem d = items.OrderByDescending(i => i.Progress.HasValue).ThenByDescending(i => i.Speed).First();
            DownloadIcon.Source = d.Icon;
            DownloadGlyph.Visibility = d.Icon == null ? Visibility.Visible : Visibility.Collapsed;
            DownloadName.Text = items.Count > 1 ? $"{d.Name} +{items.Count - 1}" : d.Name;
            DownloadFill.Background = new SolidColorBrush(d.Accent);

            if (d.Progress is double p)
            {
                SetShimmer(false);
                DownloadFill.Width = 60 * p;
                DownloadText.Text = d.Speed > 64 * 1024 ? $"{p * 100:0}% · {DownloadService.FormatSpeed(d.Speed)}" : $"{p * 100:0}%";
            }
            else
            {
                SetShimmer(true);
                DownloadText.Text = d.Speed > 0 ? DownloadService.FormatSpeed(d.Speed)
                    : d.Done > 0 ? DownloadService.FormatSize(d.Done) : "…";
            }

            DownloadPart.ToolTip = string.Join("\n", items.Select(i =>
                $"{i.Source}: {i.Name}" +
                (i.Progress is double ip ? $" — {ip * 100:0}%" : i.Done > 0 ? $" — {DownloadService.FormatSize(i.Done)}" : "") +
                (i.Speed > 0 ? $", {DownloadService.FormatSpeed(i.Speed)}" : "")));
        }
        else
        {
            SetShimmer(false);
        }
        UpdateContent();
    }

    /// <summary>Размер неизвестен — по полоске бегает короткий отрезок.</summary>
    private void SetShimmer(bool on)
    {
        if (on == _shimmer) return;
        _shimmer = on;
        if (on)
        {
            DownloadFill.Width = 18;
            DownloadShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-18, 60, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            DownloadShift.BeginAnimation(TranslateTransform.XProperty, null);
            DownloadShift.X = 0;
        }
    }

    private VoiceState _voiceShown = VoiceState.Off;

    /// <summary>Строка Капсулки: что услышала, что делает, что отвечает.</summary>
    public void UpdateVoice()
    {
        VoiceService v = App.Voice;
        switch (v.State)
        {
            case VoiceState.Listening:
                VoiceIcon.Text = "\uE720"; // микрофон
                VoiceHalo.Fill = new SolidColorBrush(Theme.Rgb(0x5E, 0x5C, 0xE6));
                VoiceText.Text = v.Text.Length > 0 ? v.Text : "Слушаю…";
                VoiceText.Foreground = new SolidColorBrush(v.Text.Length > 0 ? Theme.Rgb(0xF2, 0xF2, 0xF5) : Theme.Rgb(0x9C, 0x9C, 0xA8));
                break;
            case VoiceState.Thinking:
                VoiceIcon.Text = "\uE9F5"; // обработка
                VoiceHalo.Fill = new SolidColorBrush(Theme.Rgb(0x0A, 0x84, 0xFF));
                VoiceText.Text = v.Text;
                VoiceText.Foreground = new SolidColorBrush(Theme.Rgb(0x9C, 0x9C, 0xA8));
                break;
            case VoiceState.Speaking:
                VoiceIcon.Text = "\uE767"; // динамик
                VoiceHalo.Fill = new SolidColorBrush(Theme.Rgb(0x30, 0xD1, 0x58));
                VoiceText.Text = v.Text;
                VoiceText.Foreground = new SolidColorBrush(Theme.Rgb(0xF2, 0xF2, 0xF5));
                break;
        }
        VoicePart.ToolTip = v.Text.Length > 0 ? v.Text + "\n\nНажми, чтобы остановить" : "Нажми, чтобы остановить";

        // ореол дышит, пока слушаем и говорим; при «думаю» — ровный
        if (v.State != _voiceShown)
        {
            _voiceShown = v.State;
            if (v.State is VoiceState.Listening or VoiceState.Speaking)
            {
                var pulse = new DoubleAnimation(0.55, 1.0, TimeSpan.FromMilliseconds(v.State == VoiceState.Speaking ? 420 : 700))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                VoiceHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                VoiceHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
            else
            {
                VoiceHaloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                VoiceHaloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                VoiceHaloScale.ScaleX = VoiceHaloScale.ScaleY = 0.85;
            }
            if (_hidden && v.State is not (VoiceState.Waiting or VoiceState.Off)) SlideIn();
        }
        UpdateContent();
    }

    /// <summary>FPS и нагрузка, пока идёт игра.</summary>
    public void UpdateGame()
    {
        GameService g = App.Game;
        if (g.Active)
        {
            if (g.Fps is double fps)
            {
                GameFps.Text = $"{fps:0}";
                GameFps.Foreground = new SolidColorBrush(fps >= 55 ? Theme.Rgb(0x6E, 0xE0, 0x9A)
                    : fps >= 30 ? Theme.Rgb(0xF5, 0xD7, 0x5A) : Theme.Rgb(0xFF, 0x6B, 0x61));
            }
            else
            {
                GameFps.Text = "—";
                GameFps.Foreground = new SolidColorBrush(Theme.Rgb(0x8A, 0x8A, 0x94));
            }

            SystemSnapshot? snap = App.SystemMonitor.Current;
            GameCpu.Text = snap?.Cpu?.Load is double cpu ? $"{cpu:0}%" : "—";
            GameGpu.Text = snap?.Gpu?.Load is double gpu ? $"{gpu:0}%" : "—";
            double? temp = snap?.Gpu?.Temperature ?? snap?.Cpu?.Temperature;
            GameTemp.Text = temp is double t ? $"{t:0}°" : "";
            GameTemp.Foreground = new SolidColorBrush(temp is >= 85 ? Theme.Rgb(0xFF, 0x6B, 0x61)
                : temp is >= 75 ? Theme.Rgb(0xF5, 0xC8, 0x5A) : Theme.Rgb(0xF2, 0xF2, 0xF5));

            GamePart.ToolTip = g.Name + (g.FpsProblem != null ? $"\nFPS: {g.FpsProblem}" : "")
                               + (snap?.Gpu != null ? $"\n{snap.Gpu.Name}" : "");
        }
        UpdateContent();
    }

    public void UpdateVpn()
    {
        VpnService vpn = App.Vpn;
        Color color = vpn.IsBusy ? Theme.Rgb(0xF5, 0xC8, 0x5A)
            : vpn.IsConnected ? Theme.Rgb(0x6E, 0xE0, 0x9A)
            : Theme.Rgb(0x8A, 0x8A, 0x94);
        var brush = new SolidColorBrush(color);
        VpnIcon.Foreground = brush;
        VpnText.Foreground = brush;
        VpnIcon.Text = vpn.IsConnected ? "\uEA18" : "\uEA18";
        VpnText.Text = vpn.IsBusy ? "…" : vpn.IsConnected ? (vpn.Exit?.CountryCode is { Length: > 0 } cc ? cc : "VPN") : "VPN";
        VpnChip.Background = new SolidColorBrush(vpn.IsConnected ? Theme.Argb(0x30, 0x6E, 0xE0, 0x9A) : Theme.Argb(0x1E, 0xFF, 0xFF, 0xFF));
        VpnChip.ToolTip = vpn.IsConnected
            ? $"VPN включён: {vpn.ServerName}{(vpn.Exit != null ? $" · {vpn.Exit.Country}, {vpn.Exit.Ip}" : "")}\nНажми, чтобы отключить"
            : $"VPN выключен{(vpn.ActiveServer != null ? $" · {vpn.ActiveServer.Name}" : "")}\nНажми, чтобы подключить";
        UpdateContent();
    }

    public void UpdateZapret()
    {
        ZapretService z = App.Zapret;
        Color color = z.IsBusy ? Theme.Rgb(0xF5, 0xC8, 0x5A)
            : z.IsRunning ? Theme.Rgb(0x7C, 0x9C, 0xFF)
            : Theme.Rgb(0x8A, 0x8A, 0x94);
        var brush = new SolidColorBrush(color);
        ZapretIcon.Foreground = brush;
        ZapretText.Foreground = brush;
        ZapretText.Text = z.IsBusy ? "…" : "zapret";
        ZapretChip.Background = new SolidColorBrush(z.IsRunning ? Theme.Argb(0x30, 0x7C, 0x9C, 0xFF) : Theme.Argb(0x1E, 0xFF, 0xFF, 0xFF));
        ZapretChip.ToolTip = (z.IsRunning ? "zapret работает" : "zapret выключен") +
                             (z.Status.Length > 0 ? $"\n{z.Status}" : "") + "\nНажми, чтобы " + (z.IsRunning ? "выключить" : "включить");
        UpdateContent();
    }

    private async void ZapretChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        bool wanted = !App.Zapret.IsRunning;
        await App.Zapret.ToggleAsync();
        if (wanted && !App.Zapret.IsRunning) ShowZapretProblem();
    }

    /// <summary>Не включился — показываем причину карточкой (нет прав, антивирус и т. п.).</summary>
    public void ShowZapretProblem() => ShowNotification(new NotificationItem
    {
        AppId = "zapret",
        AppName = "zapret",
        Title = "zapret не включился",
        Body = App.Zapret.Status,
        Time = DateTime.Now,
        Click = () => App.Panel.ShowTab("zapret", activate: true),
    });

    private void VpnChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // не открываем панель
        _ = App.Vpn.ToggleAsync();
    }

    public void UpdateRecording()
    {
        if (App.Recorder.IsRecording || App.AudioRecorder.IsRecording)
        {
            if (_hidden) SlideIn();
            RecordDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromSeconds(0.7))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            });
        }
        else
        {
            RecordDot.BeginAnimation(OpacityProperty, null);
        }
        UpdateContent();
    }

    public void UpdateTimer()
    {
        PowerTimerService timer = App.PowerTimer;
        TimerChip.Visibility = timer.Active && !timer.InWarning ? Visibility.Visible : Visibility.Collapsed;
        if (timer.AfterTrack) TimerChipText.Text = "после трека";
        else if (timer.Target != null)
        {
            TimeSpan r = timer.Remaining;
            TimerChipText.Text = r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes}:{r.Seconds:00}";
        }
        UpdateContent();
    }

    /// <summary>Открыли Discord сами — счётчик сбрасываем.</summary>
    private void CheckDiscordFocused()
    {
        if (App.Notifications == null || App.Notifications.DiscordUnread == 0) return;
        try
        {
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
            using Process p = Process.GetProcessById((int)pid);
            if (p.ProcessName.Equals("Discord", StringComparison.OrdinalIgnoreCase))
                App.Notifications.ResetDiscord();
        }
        catch
        {
            // процесс уже закрылся
        }
    }

    // ---------- позиция ----------

    /// <summary>Пилюля у нижнего края растёт вверх, у верхнего — вниз.</summary>
    private void UpdateVerticalAlign()
    {
        Rect b = ScreenBounds();
        Pill.VerticalAlignment = Top + Height / 2 > b.Top + b.Height / 2
            ? VerticalAlignment.Bottom
            : VerticalAlignment.Top;
        UpdateFlush();
    }

    /// <summary>У верхнего края экрана пилюля срастается с рамкой монитора.</summary>
    private void UpdateFlush()
    {
        bool flush = !App.Settings.Unlocked && IsDockedTop() && Pill.VerticalAlignment == VerticalAlignment.Top;
        Liquid.FlushTop = flush;
        Pill.RenderTransformOrigin = flush ? new Point(0.5, 0) : new Point(0.5, 0.5);
    }

    /// <summary>Пилюля сейчас срослась с верхней рамкой экрана.</summary>
    public bool IsFlush => Liquid.FlushTop;

    /// <summary>Прямоугольник самой пилюли на экране (DIP), без прозрачных полей окна.</summary>
    public Rect PillRect()
    {
        Point p = Pill.TranslatePoint(new Point(0, 0), this);
        return new Rect(Left + p.X, Top + p.Y, Pill.ActualWidth, Pill.ActualHeight);
    }

    private void SetAlign(string align)
    {
        Pill.HorizontalAlignment = align switch
        {
            "left" => HorizontalAlignment.Left,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
    }

    private string CurrentAlign() => Pill.HorizontalAlignment switch
    {
        HorizontalAlignment.Left => "left",
        HorizontalAlignment.Right => "right",
        _ => "center",
    };

    private void RestorePosition()
    {
        AppSettings s = App.Settings;
        if (s.LastScreen != null && s.Positions.TryGetValue(s.LastScreen, out PillPosition? pos))
        {
            foreach (WF.Screen screen in WF.Screen.AllScreens)
            {
                if (screen.DeviceName != s.LastScreen) continue;
                Rect b = ScreenHelper.ToDip(screen.Bounds, this);
                if (b.IntersectsWith(new Rect(pos.Left, pos.Top, Width, Height)))
                {
                    Left = pos.Left;
                    Top = pos.Top;
                    SetAlign(pos.Align);
                    UpdateVerticalAlign();
                    return;
                }
            }
        }
        ResetPosition(save: false);
    }

    /// <summary>Вернуть пилюлю по центру верхнего края основного экрана.</summary>
    public void ResetPosition(bool save = true)
    {
        Rect b = ScreenHelper.ToDip(WF.Screen.PrimaryScreen!.Bounds, this);
        BeginAnimation(TopProperty, null);
        SetAlign("center");
        Left = b.Left + (b.Width - Width) / 2;
        Top = b.Top - 4;
        _hidden = false;
        UpdateVerticalAlign();

        if (save)
        {
            App.Settings.Positions.Clear();
            App.Settings.LastScreen = null;
            App.Settings.Save();
        }
    }

    private Rect ScreenBounds() => ScreenHelper.ToDip(ScreenHelper.ScreenOf(this).Bounds, this);

    private double DockedTop() => ScreenBounds().Top - 4;

    private bool IsDockedTop() => _hidden || Top <= ScreenBounds().Top + 2;

    /// <summary>Прилипание к краям и центру, затем сохранение позиции для этого монитора.</summary>
    private void SnapAndSave()
    {
        WF.Screen screen = ScreenHelper.ScreenOf(this);
        Rect b = ScreenHelper.ToDip(screen.Bounds, this);
        Rect wa = ScreenHelper.ToDip(screen.WorkingArea, this);
        Rect pill = PillRect();
        double pillCenter = pill.Left + pill.Width / 2;

        if (pill.Left - wa.Left < SnapDistance)
        {
            SetAlign("left");
            Left = wa.Left - SideMargin;
        }
        else if (wa.Right - pill.Right < SnapDistance)
        {
            SetAlign("right");
            Left = wa.Right - Width + SideMargin;
        }
        else
        {
            SetAlign("center");
            Left = Math.Abs(pillCenter - (b.Left + b.Width / 2)) < SnapDistance
                ? b.Left + (b.Width - Width) / 2
                : pillCenter - Width / 2;
        }

        if (pill.Top - b.Top < SnapDistance) Top = DockedTop();
        else if (wa.Bottom - pill.Bottom < SnapDistance) Top = wa.Bottom - Height + 4;
        UpdateVerticalAlign();

        App.Settings.Positions[screen.DeviceName] = new PillPosition { Left = Left, Top = Top, Align = CurrentAlign() };
        App.Settings.LastScreen = screen.DeviceName;
        App.Settings.Save();
    }

    // ---------- мышь ----------

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (App.Voice?.State is VoiceState.Listening or VoiceState.Thinking or VoiceState.Speaking)
        {
            App.Voice.Cancel();
            e.Handled = true;
            return;
        }
        if (App.Settings.Unlocked)
        {
            App.Panel.HideIsland();
            try { DragMove(); } catch (InvalidOperationException) { }
            SnapAndSave();
        }
        else if ((App.Recorder.IsRecording || App.AudioRecorder.IsRecording) && _card == null)
        {
            App.Recorder.Stop();
            App.AudioRecorder.Stop();
        }
        else if (App.PowerTimer.InWarning)
        {
            App.PowerTimer.Cancel();
        }
        else if (_card != null)
        {
            NotificationItem item = _card;
            if (item.IsDiscord) App.Notifications.ResetDiscord();
            item.OpenApp();
            CloseCard();
        }
        else
        {
            _hoverTimer.Stop();
            App.Panel.TogglePill();
        }
    }

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateScale(1.06);
        if (App.Settings.OpenOnHover && !App.Settings.Unlocked && _card == null) _hoverTimer.Start();
    }

    private void Pill_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateScale(1.0);
        _hoverTimer.Stop();
    }

    private void AnimateScale(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase() };
        PillScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        PillScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void BuildContextMenu()
    {
        var lockItem = new MenuItem();
        lockItem.Click += (_, _) =>
        {
            App.Settings.Unlocked = !App.Settings.Unlocked;
            if (!App.Settings.Unlocked) SnapAndSave();
            else App.Panel.HideIsland();
            App.Settings.Save();
            ApplySettings();
        };

        var center = new MenuItem { Header = "Вернуть по центру сверху" };
        center.Click += (_, _) => ResetPosition();

        var settings = new MenuItem { Header = "Настройки" };
        settings.Click += (_, _) => App.Panel.ShowTab("settings", activate: true);

        var quit = new MenuItem { Header = "Выход" };
        quit.Click += (_, _) => App.Quit();

        var menu = new ContextMenu();
        menu.Items.Add(lockItem);
        menu.Items.Add(center);
        menu.Items.Add(new Separator());
        menu.Items.Add(settings);
        menu.Items.Add(quit);
        menu.Opened += (_, _) =>
            lockItem.Header = App.Settings.Unlocked ? "Закрепить здесь" : "Открепить и переместить";

        Pill.ContextMenu = menu;
    }

    // ---------- автоскрытие ----------

    private void AutoHideTick()
    {
        bool allowed = App.Settings.AutoHide && !App.Settings.Unlocked && IsDockedTop();
        if (!allowed)
        {
            if (_hidden) SlideIn();
            _awaySince = null;
            return;
        }

        Rect b = ScreenBounds();
        Point c = ScreenHelper.CursorDip(this);
        Rect pill = PillRect();

        if (_hidden)
        {
            bool atEdge = c.Y <= b.Top + 2 && c.X >= pill.Left - 40 && c.X <= pill.Right + 40;
            if (atEdge) SlideIn();
            return;
        }

        var near = new Rect(pill.Left - 30, b.Top - 2, pill.Width + 60, pill.Bottom - b.Top + 30);
        if (near.Contains(c) || App.Panel.IsVisible || _card != null || App.PowerTimer.InWarning || App.Recorder.IsRecording || App.AudioRecorder.IsRecording || Pill.ContextMenu?.IsOpen == true)
        {
            _awaySince = null;
            return;
        }

        _awaySince ??= DateTime.Now;
        if ((DateTime.Now - _awaySince.Value).TotalSeconds > 1.2) SlideOut();
    }

    private void SlideOut()
    {
        _hidden = true;
        _awaySince = null;
        // пилюля 34 px с полем 6 px сверху: оставляем видимой полоску в ~3 px
        AnimateTop(ScreenBounds().Top - 37);
    }

    private void SlideIn()
    {
        _hidden = false;
        AnimateTop(DockedTop());
    }

    private void AnimateTop(double to)
    {
        double from = Top;
        Top = to; // базовое значение, после анимации останется оно
        BeginAnimation(TopProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
    }
}

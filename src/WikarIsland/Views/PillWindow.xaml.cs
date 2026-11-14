using System;
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
/// умеет открепляться, перетаскиваться, прилипать к краям и прятаться за край экрана.
/// </summary>
public partial class PillWindow : Window
{
    private const double SnapDistance = 28;

    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _autoHideTimer;
    private HotkeyService? _hotkey;
    private bool _hidden;
    private DateTime? _awaySince;

    public PillWindow()
    {
        InitializeComponent();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateTime();

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            App.Panel.ShowNearPill(activate: false);
        };

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _autoHideTimer.Tick += (_, _) => AutoHideTick();

        BuildContextMenu();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExStyle(handle, NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        _hotkey = new HotkeyService(this);
        _hotkey.Pressed += () => App.Panel.Toggle(activate: true);
        _hotkey.Register(App.Settings.Hotkey, out _);

        RestorePosition();
        ApplySettings();
        UpdateTime();
        _clockTimer.Start();
        _autoHideTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotkey?.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Перерегистрировать горячую клавишу из настроек.</summary>
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

    /// <summary>Применить вид пилюли: глаза/часы и рамку открепления.</summary>
    public void ApplySettings()
    {
        AppSettings s = App.Settings;
        string mode = s.EyesEnabled ? s.PillMode : "clock";

        Eyes.Visibility = mode == "clock" ? Visibility.Collapsed : Visibility.Visible;
        TimeText.Visibility = mode == "eyes" ? Visibility.Collapsed : Visibility.Visible;
        Eyes.HorizontalAlignment = mode == "eyes" ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        TimeText.HorizontalAlignment = mode == "clock" ? HorizontalAlignment.Center : HorizontalAlignment.Right;

        Pill.BorderBrush = s.Unlocked
            ? (Brush)FindResource("Accent")
            : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        Pill.BorderThickness = new Thickness(s.Unlocked ? 1.6 : 1);
        Pill.Cursor = s.Unlocked ? Cursors.SizeAll : Cursors.Arrow;
    }

    private void UpdateTime() => TimeText.Text = DateTime.Now.ToString("HH:mm");

    // ---------- позиция ----------

    private void RestorePosition()
    {
        AppSettings s = App.Settings;
        if (s.LastScreen != null && s.Positions.TryGetValue(s.LastScreen, out PillPosition? pos))
        {
            foreach (WF.Screen screen in WF.Screen.AllScreens)
            {
                if (screen.DeviceName != s.LastScreen) continue;
                Rect b = ScreenHelper.ToDip(screen.Bounds, this);
                if (b.Contains(new Point(pos.Left + Width / 2, pos.Top + Height / 2)))
                {
                    Left = pos.Left;
                    Top = pos.Top;
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
        Left = b.Left + (b.Width - Width) / 2;
        Top = b.Top - 2;
        _hidden = false;

        if (save)
        {
            App.Settings.Positions.Clear();
            App.Settings.LastScreen = null;
            App.Settings.Save();
        }
    }

    private Rect ScreenBounds() => ScreenHelper.ToDip(ScreenHelper.ScreenOf(this).Bounds, this);

    private bool IsDockedTop() => _hidden || Top <= ScreenBounds().Top + 4;

    /// <summary>Прилипание к краям и центру, затем сохранение позиции для этого монитора.</summary>
    private void SnapAndSave()
    {
        WF.Screen screen = ScreenHelper.ScreenOf(this);
        Rect b = ScreenHelper.ToDip(screen.Bounds, this);
        Rect wa = ScreenHelper.ToDip(screen.WorkingArea, this);

        double centerX = Left + Width / 2;
        if (Math.Abs(centerX - (b.Left + b.Width / 2)) < SnapDistance) Left = b.Left + (b.Width - Width) / 2;
        if (Left - wa.Left < SnapDistance) Left = wa.Left;
        if (wa.Right - (Left + Width) < SnapDistance) Left = wa.Right - Width;
        if (Top - b.Top < SnapDistance) Top = b.Top - 2;
        else if (wa.Bottom - (Top + Height) < SnapDistance) Top = wa.Bottom - Height + 2;

        App.Settings.Positions[screen.DeviceName] = new PillPosition { Left = Left, Top = Top };
        App.Settings.LastScreen = screen.DeviceName;
        App.Settings.Save();
    }

    // ---------- мышь ----------

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (App.Settings.Unlocked)
        {
            App.Panel.HideIsland();
            try { DragMove(); } catch (InvalidOperationException) { }
            SnapAndSave();
        }
        else
        {
            _hoverTimer.Stop();
            App.Panel.Toggle(activate: false);
        }
    }

    private void Pill_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateScale(1.06);
        if (!App.Settings.Unlocked) _hoverTimer.Start();
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
        var near = new Rect(Left - 30, b.Top - 2, Width + 60, Height + 30);

        if (_hidden)
        {
            bool atEdge = c.Y <= b.Top + 2 && c.X >= Left - 40 && c.X <= Left + Width + 40;
            if (atEdge) SlideIn();
            return;
        }

        if (near.Contains(c) || App.Panel.IsVisible || Pill.ContextMenu?.IsOpen == true)
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
        // пилюля 34 px по центру окна 46 px: оставляем видимой полоску в ~3 px
        AnimateTop(ScreenBounds().Top - 37);
    }

    private void SlideIn()
    {
        _hidden = false;
        AnimateTop(ScreenBounds().Top - 2);
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

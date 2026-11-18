using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>Как сейчас показано окно встроенного YouTube.</summary>
public enum VideoMode
{
    Hidden,
    Normal,
    Full,
    Mini,
}

/// <summary>
/// Окно встроенного YouTube: обычное окно, весь экран и мини-плеер поверх всех окон.
/// Пока видео не запущено, окно стоит за пределами экрана: страница уже загружена и может искать,
/// но пользователю не мешает. Крестик не закрывает окно, а прячет его и ставит видео на паузу.
/// </summary>
public sealed class VideoWindow : Window
{
    private const double MiniWidth = 480;
    private const double MiniHeight = 290;

    private Rect _normalBounds = new(double.NaN, double.NaN, 1120, 700);
    private VideoMode _beforeFull = VideoMode.Normal;
    private bool _forceClose;

    public WebView2 Web { get; }
    public VideoMode Mode { get; private set; } = VideoMode.Hidden;

    /// <summary>Режим поменялся (для слоя «на весь экран» на странице).</summary>
    public event Action<VideoMode>? ModeChanged;

    /// <summary>Окно закрыли крестиком.</summary>
    public event Action? ClosedByUser;

    public VideoWindow()
    {
        Title = "YouTube — Wikar Island";
        Width = 1120;
        Height = 700;
        MinWidth = 320;
        MinHeight = 200;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
        Content = new Grid { Children = { Web } };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.SetDwmInt(new WindowInteropHelper(this).Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_forceClose)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        SetMode(VideoMode.Hidden);
        ClosedByUser?.Invoke();
    }

    /// <summary>Закрыть по-настоящему (выход из острова).</summary>
    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    /// <summary>Режим, в который вернуться из полноэкранного.</summary>
    public VideoMode BeforeFull => _beforeFull;

    public void SetMode(VideoMode mode)
    {
        if (mode == Mode && mode != VideoMode.Hidden) return;
        VideoMode old = Mode;
        if (old == VideoMode.Normal && IsOnScreen) _normalBounds = new Rect(Left, Top, Width, Height);
        if (mode == VideoMode.Full && old != VideoMode.Full) _beforeFull = old is VideoMode.Mini ? VideoMode.Mini : VideoMode.Normal;
        WindowState = WindowState.Normal;
        Mode = mode;

        switch (mode)
        {
            case VideoMode.Hidden:
                Topmost = false;
                ShowInTaskbar = false;
                Hide();
                break;

            case VideoMode.Normal:
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                Topmost = false;
                ShowInTaskbar = true;
                Rect work = WorkArea();
                double w = Math.Min(_normalBounds.Width, work.Width - 40);
                double h = Math.Min(_normalBounds.Height, work.Height - 40);
                bool known = !double.IsNaN(_normalBounds.X) && work.Contains(new Point(_normalBounds.X + 40, _normalBounds.Y + 20));
                Left = known ? _normalBounds.X : work.Left + (work.Width - w) / 2;
                Top = known ? _normalBounds.Y : work.Top + (work.Height - h) / 2;
                Width = w;
                Height = h;
                Reveal();
                break;

            case VideoMode.Full:
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                Topmost = true;
                ShowInTaskbar = true;
                Rect screen = ScreenBounds();
                Left = screen.Left;
                Top = screen.Top;
                Width = screen.Width;
                Height = screen.Height;
                Reveal();
                break;

            case VideoMode.Mini:
                WindowStyle = WindowStyle.ToolWindow;
                ResizeMode = ResizeMode.CanResizeWithGrip;
                Topmost = true;
                ShowInTaskbar = true;
                Rect area = WorkArea();
                Width = MiniWidth;
                Height = MiniHeight;
                Left = area.Right - MiniWidth - 16;
                Top = area.Bottom - MiniHeight - 16;
                Reveal(activate: false);
                break;
        }
        ModeChanged?.Invoke(mode);
    }

    /// <summary>Показать окно (из невидимого) и, если нужно, вывести вперёд.</summary>
    private void Reveal(bool activate = true)
    {
        if (!IsVisible) Show();
        if (!activate) return;
        Activate();
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) NativeMethods.SetForegroundWindow(handle);
    }

    /// <summary>Окно загружено, но пользователю не показано: за пределами экрана.</summary>
    public void ShowOffscreen()
    {
        Left = -32000;
        Top = -32000;
        if (!IsVisible) Show();
    }

    private bool IsOnScreen => IsVisible && Left > -30000;

    private Rect WorkArea()
    {
        try
        {
            System.Windows.Forms.Screen s = IsOnScreen ? ScreenHelper.ScreenOf(this) : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            return ScreenHelper.ToDip(s.WorkingArea, App.Pill);
        }
        catch
        {
            return SystemParameters.WorkArea;
        }
    }

    private Rect ScreenBounds()
    {
        try
        {
            System.Windows.Forms.Screen s = IsOnScreen ? ScreenHelper.ScreenOf(this) : System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            return ScreenHelper.ToDip(s.Bounds, App.Pill);
        }
        catch
        {
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }
    }
}

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WikarIsland.Services;
using WF = System.Windows.Forms;

namespace WikarIsland.Views;

/// <summary>
/// Пипетка: почти прозрачное окно поверх всех экранов ловит клик,
/// рядом с курсором — образец цвета и его код. Клик копирует #RRGGBB, Esc — отмена.
/// </summary>
public sealed class ColorPickerWindow : Window
{
    private readonly Border _badge;
    private readonly Border _swatch;
    private readonly TextBlock _hex;
    private readonly Canvas _canvas = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private Color _color;

    public event Action<Color>? Picked;

    public ColorPickerWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // почти прозрачно, но клики ловим
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Cursor = Cursors.Cross;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5) };
        _hex = new TextBlock
        {
            Foreground = Brushes.White, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_swatch);
        row.Children.Add(_hex);
        _badge = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 18, 18, 22)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 6, 10, 6),
            Child = row,
            IsHitTestVisible = false,
        };
        _canvas.Children.Add(_badge);
        Content = _canvas;

        _timer.Tick += (_, _) => Track();
        Loaded += (_, _) =>
        {
            // накрываем все мониторы
            System.Drawing.Rectangle v = WF.SystemInformation.VirtualScreen;
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            Left = v.Left / dpi.DpiScaleX;
            Top = v.Top / dpi.DpiScaleY;
            Width = v.Width / dpi.DpiScaleX;
            Height = v.Height / dpi.DpiScaleY;
            Activate();
            Focus();
            _timer.Start();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            _timer.Stop();
            Close();
            Picked?.Invoke(_color);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _timer.Stop(); Close(); }
        };
        MouseRightButtonUp += (_, _) => { _timer.Stop(); Close(); };
    }

    private void Track()
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT p);
        _color = ScreenTools.PixelAt(p.X, p.Y);
        _swatch.Background = new SolidColorBrush(_color);
        _hex.Text = $"#{_color.R:X2}{_color.G:X2}{_color.B:X2}";

        Point local = PointFromScreen(new Point(p.X, p.Y));
        Canvas.SetLeft(_badge, local.X + 18);
        Canvas.SetTop(_badge, local.Y + 18);
    }
}

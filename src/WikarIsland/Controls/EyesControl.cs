using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using WikarIsland.Services;

namespace WikarIsland.Controls;

/// <summary>
/// Два глаза, которые следят за курсором по всему экрану, моргают,
/// удивляются, когда курсор рядом, и засыпают, если мышь долго не двигается.
/// </summary>
public sealed class EyesControl : Grid
{
    private const double EyeSize = 13;
    private const double Gap = 5;
    private const double PupilSize = 5.5;
    private const double MaxOffset = 3.1;

    public double SleepAfterSeconds { get; set; } = 90;

    private readonly FrameworkElement[] _eyes = new FrameworkElement[2];
    private readonly TranslateTransform[] _pupils = { new(), new() };
    private readonly Vector[] _current = new Vector[2];
    private readonly ScaleTransform _lid = new(1, 1);
    private readonly ScaleTransform _size = new(1, 1);
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();

    private NativeMethods.POINT _lastCursor;
    private DateTime _lastMove = DateTime.Now;
    private DateTime _nextBlink;
    private bool _asleep;

    public EyesControl()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var transforms = new TransformGroup();
        transforms.Children.Add(_lid);
        transforms.Children.Add(_size);
        row.RenderTransform = transforms;

        for (int i = 0; i < 2; i++)
        {
            var eye = new Grid
            {
                Width = EyeSize,
                Height = EyeSize,
                Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0),
            };
            eye.Children.Add(new Ellipse { Fill = Brushes.White });
            eye.Children.Add(new Ellipse
            {
                Width = PupilSize,
                Height = PupilSize,
                Fill = new SolidColorBrush(Color.FromRgb(14, 14, 18)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _pupils[i],
            });
            _eyes[i] = eye;
            row.Children.Add(eye);
        }

        Children.Add(row);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
        ScheduleBlink();
    }

    private void Tick()
    {
        if (PresentationSource.FromVisual(this) is null) return;

        NativeMethods.GetCursorPos(out NativeMethods.POINT p);
        DateTime now = DateTime.Now;

        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
        {
            _lastCursor = p;
            _lastMove = now;
            if (_asleep) Wake();
        }
        else if (!_asleep && (now - _lastMove).TotalSeconds > SleepAfterSeconds)
        {
            Sleep();
        }

        double nearest = double.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            // центр глаза в физических пикселях — в тех же координатах, что и курсор
            Point c = _eyes[i].PointToScreen(new Point(EyeSize / 2, EyeSize / 2));
            double dx = p.X - c.X;
            double dy = p.Y - c.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            nearest = Math.Min(nearest, dist);

            Vector target = dist < 0.5
                ? new Vector()
                : new Vector(dx / dist, dy / dist) * MaxOffset * Math.Min(1, dist / 120);

            _current[i] += (target - _current[i]) * 0.22; // инерция, чтобы взгляд «доводился»
            _pupils[i].X = _current[i].X;
            _pupils[i].Y = _current[i].Y;
        }

        // курсор вплотную — глаза удивлённо расширяются
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double want = !_asleep && nearest < 60 * dpi ? 1.18 : 1.0;
        _size.ScaleX += (want - _size.ScaleX) * 0.15;
        _size.ScaleY = _size.ScaleX;

        if (!_asleep && now >= _nextBlink)
        {
            Blink();
            ScheduleBlink();
        }
    }

    private void ScheduleBlink() =>
        _nextBlink = DateTime.Now.AddSeconds(2.5 + _rng.NextDouble() * 4.5);

    private void Blink()
    {
        var anim = new DoubleAnimation(1, 0.08, TimeSpan.FromMilliseconds(70)) { AutoReverse = true };
        if (_rng.NextDouble() < 0.15)
            anim.RepeatBehavior = new RepeatBehavior(2); // иногда моргает дважды
        _lid.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Sleep()
    {
        _asleep = true;
        _lid.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.1, TimeSpan.FromMilliseconds(900)) { EasingFunction = new SineEase() });
    }

    private void Wake()
    {
        _asleep = false;
        _lid.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new BackEase { Amplitude = 0.4 } });
        ScheduleBlink();
    }
}

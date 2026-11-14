using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using WikarIsland.Services;

namespace WikarIsland.Controls;

/// <summary>
/// «Жидкая» подложка пилюли.
/// У верхнего края экрана верх срастается с рамкой монитора вогнутыми скруглениями (как вырез),
/// нижний край тянется каплей к курсору, рябит в такт звуку, вздрагивает от событий и провисает, когда тебя нет.
/// </summary>
public sealed class LiquidPill : FrameworkElement
{
    private const double Fillet = 11;       // вогнутые «плечи» у рамки
    private const double TopExtend = 16;    // уходит за край экрана, чтобы не было щели
    private const double ReachDistance = 230;
    private const double SleepAfterSeconds = 120;

    private readonly DispatcherTimer _timer;
    private NativeMethods.POINT _lastCursor;
    private DateTime _lastMove = DateTime.Now;

    private double _bumpX = -1, _bumpA, _pulse, _level, _phase, _sag;
    private bool _flushTop;
    private double _cornerRadius = 17;

    public Brush Fill { get; set; } = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E));
    public Pen? Outline { get; set; }

    /// <summary>Тень/свечение пилюли — окрашиваем её под обложку, пока играет музыка.</summary>
    public DropShadowEffect? Glow { get; set; }
    public Color AccentColor { get; set; } = Colors.Black;
    public bool MusicPlaying { get; set; }

    /// <summary>Свечение вокруг капсулы цветом обложки, пока играет музыка.</summary>
    public bool GlowEnabled { get; set; } = true;
    public Func<float>? AudioLevel { get; set; }

    public bool FlushTop
    {
        get => _flushTop;
        set { _flushTop = value; InvalidateVisual(); }
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = value; InvalidateVisual(); }
    }

    public LiquidPill()
    {
        IsHitTestVisible = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
    }

    /// <summary>Вздрогнуть: капля вниз и обратно (новое уведомление, событие).</summary>
    public void Pulse(double amount = 9) => _pulse = Math.Max(_pulse, amount);

    private void Tick()
    {
        if (PresentationSource.FromVisual(this) is null || ActualWidth <= 0) return;

        double w = ActualWidth, h = ActualHeight;
        double r = Math.Min(_cornerRadius, Math.Min(h / 2, w / 2));

        // курсор
        NativeMethods.GetCursorPos(out NativeMethods.POINT p);
        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
        {
            _lastCursor = p;
            _lastMove = DateTime.Now;
        }
        Point local = PointFromScreen(new Point(p.X, p.Y));

        double targetA = 0, targetX = w / 2;
        double below = local.Y - h;
        bool inX = local.X > -ReachDistance / 2 && local.X < w + ReachDistance / 2;
        if (below > 0 && below < ReachDistance && inX)
        {
            double horizontal = local.X < 0 ? -local.X : local.X > w ? local.X - w : 0;
            double d = Math.Sqrt(below * below + horizontal * horizontal);
            targetA = Math.Max(0, 6.5 * (1 - d / ReachDistance));
            targetX = Math.Clamp(local.X, r + 10, w - r - 10);
        }

        if (_bumpX < 0) _bumpX = w / 2;
        _bumpX += (targetX - _bumpX) * 0.16;
        _bumpA += (targetA - _bumpA) * 0.14;

        // провисает, когда тебя долго нет
        bool away = (DateTime.Now - _lastMove).TotalSeconds > SleepAfterSeconds;
        _sag += ((away ? 3.5 : 0) - _sag) * 0.03;

        // музыка: реальный уровень звука
        float raw = MusicPlaying ? (AudioLevel?.Invoke() ?? 0f) : 0f;
        _level += (raw - _level) * (raw > _level ? 0.55 : 0.18);
        _phase += 0.33;
        _pulse *= 0.88;

        UpdateGlow();
        InvalidateVisual();
    }

    private void UpdateGlow()
    {
        if (Glow == null) return;
        if (GlowEnabled && MusicPlaying && AccentColor != Colors.Black)
        {
            Glow.Color = AccentColor;
            Glow.BlurRadius = 10 + _level * 18;
            Glow.Opacity = 0.35 + _level * 0.55;
        }
        else
        {
            Glow.Color = Colors.Black;
            Glow.BlurRadius = 10;
            Glow.Opacity = 0.45;
        }
    }

    /// <summary>Насколько нижний край опущен в точке x.</summary>
    private double BottomOffset(double x, double w)
    {
        double y = Bump(x, _bumpX, _bumpA + _pulse, 15);
        y += Bump(x, w / 2, _sag, w * 0.28);
        if (_level > 0.01)
        {
            for (int i = 0; i < 4; i++)
            {
                double cx = w * (0.22 + 0.19 * i);
                double a = _level * 7 * (0.55 + 0.45 * Math.Sin(_phase * (1 + i * 0.27) + i * 1.7));
                y += Bump(x, cx, Math.Max(0, a), 11);
            }
        }
        return y;
    }

    private static double Bump(double x, double center, double amplitude, double width)
    {
        if (amplitude <= 0.01) return 0;
        double t = (x - center) / width;
        return amplitude * Math.Exp(-t * t);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double r = Math.Min(_cornerRadius, Math.Min(h / 2, w / 2));

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            if (_flushTop)
            {
                // верх срастается с рамкой экрана: прямой верх + вогнутые скругления
                ctx.BeginFigure(new Point(-Fillet, -TopExtend), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(w + Fillet, -TopExtend), true, false);
                ctx.LineTo(new Point(w + Fillet, 0), true, false);
                ctx.ArcTo(new Point(w, Fillet), new Size(Fillet, Fillet), 0, false, SweepDirection.Counterclockwise, true, false);
                ctx.LineTo(new Point(w, h - r), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(0, r), isFilled: true, isClosed: true);
                ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(w - r, 0), true, false);
                ctx.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(w, h - r), true, false);
            }

            // правый нижний угол
            ctx.ArcTo(new Point(w - r, h), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);

            // живой нижний край: справа налево
            var points = new List<Point>();
            for (double x = w - r; x > r; x -= 3)
                points.Add(new Point(x, h + BottomOffset(x, w)));
            points.Add(new Point(r, h + BottomOffset(r, w)));
            ctx.PolyLineTo(points, true, true);

            // левый нижний угол
            ctx.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);

            if (_flushTop)
            {
                ctx.LineTo(new Point(0, Fillet), true, false);
                ctx.ArcTo(new Point(-Fillet, 0), new Size(Fillet, Fillet), 0, false, SweepDirection.Counterclockwise, true, false);
            }
        }
        geometry.Freeze();

        dc.DrawGeometry(Fill, _flushTop ? null : Outline, geometry);
    }
}

using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using WikarIsland.Services;

namespace WikarIsland.Controls;

/// <summary>
/// Пара глаз, которые следят за курсором по всему экрану, моргают, удивляются, когда курсор рядом,
/// и засыпают, если мышь долго не двигается. Стиль меняется в настройках.
/// </summary>
public sealed class EyesControl : FrameworkElement
{
    public static readonly string[] Styles = { "classic", "cat", "robot", "cute", "sleepy", "angry", "pixel" };

    public static string StyleName(string style) => style switch
    {
        "cat" => "Кошачьи",
        "robot" => "Робот",
        "cute" => "Мультяшные",
        "sleepy" => "Сонные",
        "angry" => "Сердитые",
        "pixel" => "Пиксельные",
        _ => "Классические",
    };

    private const double EyeSize = 13;
    private const double Gap = 5;
    private const double MaxOffset = 3.1;

    public static readonly DependencyProperty EyeStyleProperty = DependencyProperty.Register(
        nameof(EyeStyle), typeof(string), typeof(EyesControl),
        new FrameworkPropertyMetadata("classic", FrameworkPropertyMetadataOptions.AffectsRender));

    public string EyeStyle
    {
        get => (string)GetValue(EyeStyleProperty);
        set => SetValue(EyeStyleProperty, value);
    }

    public double SleepAfterSeconds { get; set; } = 90;

    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private readonly Vector[] _pupil = new Vector[2];
    private NativeMethods.POINT _lastCursor;
    private DateTime _lastMove = DateTime.Now;
    private DateTime _nextBlink;
    private DateTime _blinkStart = DateTime.MinValue;
    private bool _doubleBlink;
    private bool _asleep;
    private double _lid = 1;     // 1 — открыты, 0.08 — закрыты
    private double _surprise = 1;

    public EyesControl()
    {
        Width = EyeSize * 2 + Gap;
        Height = EyeSize;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _timer.Start();
            else _timer.Stop();
        };
        ScheduleBlink();
    }

    private Point EyeCenter(int i) => new(EyeSize / 2 + i * (EyeSize + Gap), EyeSize / 2);

    private void Tick()
    {
        if (PresentationSource.FromVisual(this) is null) return;

        NativeMethods.GetCursorPos(out NativeMethods.POINT p);
        DateTime now = DateTime.Now;

        if (p.X != _lastCursor.X || p.Y != _lastCursor.Y)
        {
            _lastCursor = p;
            _lastMove = now;
            if (_asleep) { _asleep = false; ScheduleBlink(); }
        }
        else if (!_asleep && (now - _lastMove).TotalSeconds > SleepAfterSeconds)
        {
            _asleep = true;
        }

        double nearest = double.MaxValue;
        for (int i = 0; i < 2; i++)
        {
            Point c = PointToScreen(EyeCenter(i)); // физические пиксели — как и курсор
            double dx = p.X - c.X, dy = p.Y - c.Y;
            double dist = Math.Sqrt(dx * dx + dy * dy);
            nearest = Math.Min(nearest, dist);
            Vector target = dist < 0.5 ? new Vector() : new Vector(dx / dist, dy / dist) * MaxOffset * Math.Min(1, dist / 120);
            _pupil[i] += (target - _pupil[i]) * 0.22;
        }

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double wantSurprise = !_asleep && nearest < 60 * dpi ? 1.18 : 1.0;
        _surprise += (wantSurprise - _surprise) * 0.15;

        // веки: сон, моргание
        double wantLid = _asleep ? 0.08 : 1;
        if (!_asleep && now >= _nextBlink)
        {
            _blinkStart = now;
            _doubleBlink = _rng.NextDouble() < 0.15;
            ScheduleBlink();
        }
        double sinceBlink = (now - _blinkStart).TotalMilliseconds;
        double blinkLength = _doubleBlink ? 300 : 150;
        if (!_asleep && sinceBlink < blinkLength)
        {
            double phase = sinceBlink % 150 / 150; // 0..1 на одно моргание
            wantLid = 1 - Math.Sin(phase * Math.PI) * 0.92;
            _lid = wantLid;
        }
        else
        {
            _lid += (wantLid - _lid) * (_asleep ? 0.05 : 0.25);
        }

        InvalidateVisual();
    }

    private void ScheduleBlink() => _nextBlink = DateTime.Now.AddSeconds(2.5 + _rng.NextDouble() * 4.5);

    protected override void OnRender(DrawingContext dc) => Draw(dc, EyeStyle, _pupil, _lid, _surprise);

    private static readonly Brush White = Theme.Brush(Colors.White);
    private static readonly Brush Pupil = Frozen(Theme.Rgb(14, 14, 18));
    private static readonly Brush CatIris = Frozen(new RadialGradientBrush(Theme.Rgb(0xE4, 0xF0, 0x6A), Theme.Rgb(0x8C, 0xC0, 0x2A)));
    private static readonly Brush RobotBody = Frozen(Theme.Rgb(0x0E, 0x26, 0x30));
    private static readonly Pen RobotRim = FrozenPen(Theme.Rgb(0x3C, 0xD8, 0xF0), 1.2);
    private static readonly Brush RobotLed = Frozen(new RadialGradientBrush(Theme.Rgb(0xC8, 0xFA, 0xFF), Theme.Rgb(0x3C, 0xD8, 0xF0)));
    private static readonly Brush CuteBody = Frozen(Theme.Rgb(0x18, 0x18, 0x22));
    private static readonly Brush LidBrush = Frozen(Theme.Rgb(0x0B, 0x0B, 0x0E));

    private static Brush Frozen(Color c) => Frozen(new SolidColorBrush(c));
    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
    private static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    /// <summary>Рисует пару глаз. Используется и в пилюле, и в превью стилей в настройках.</summary>
    public static void Draw(DrawingContext dc, string style, Vector[] pupils, double lid, double surprise)
    {
        double total = EyeSize * 2 + Gap;
        dc.PushTransform(new ScaleTransform(surprise, surprise, total / 2, EyeSize / 2));

        for (int i = 0; i < 2; i++)
        {
            var c = new Point(EyeSize / 2 + i * (EyeSize + Gap), EyeSize / 2);
            Vector o = pupils[i];
            double r = EyeSize / 2;

            // веко: сжимаем глаз по вертикали вокруг центра
            dc.PushTransform(new ScaleTransform(1, Math.Max(0.06, lid), c.X, c.Y));

            switch (style)
            {
                case "cat":
                    dc.DrawEllipse(CatIris, null, c, r, r);
                    dc.DrawEllipse(Pupil, null, c + o * 0.8, 1.3 + (1 - lid) * 0.5, r * 0.78);
                    break;

                case "robot":
                    dc.DrawRoundedRectangle(RobotBody, RobotRim, new Rect(c.X - r, c.Y - r * 0.85, r * 2, r * 1.7), 3, 3);
                    dc.DrawRoundedRectangle(RobotLed, null, new Rect(c.X - 2.6 + o.X, c.Y - 2.6 + o.Y * 0.7, 5.2, 5.2), 1.2, 1.2);
                    break;

                case "cute":
                    dc.DrawEllipse(CuteBody, null, c + o * 0.35, r * 0.9, r);
                    dc.DrawEllipse(White, null, c + o * 0.6 + new Vector(-1.8, -2.4), 2.1, 2.1);
                    dc.DrawEllipse(White, null, c + o * 0.6 + new Vector(1.9, 1.6), 1.0, 1.0);
                    break;

                case "pixel":
                {
                    double cell = EyeSize / 3;
                    dc.DrawRectangle(White, null, new Rect(c.X - r, c.Y - r, EyeSize, EyeSize));
                    int px = (int)Math.Round(Math.Clamp(o.X / MaxOffset, -1, 1));
                    int py = (int)Math.Round(Math.Clamp(o.Y / MaxOffset, -1, 1));
                    dc.DrawRectangle(Pupil, null, new Rect(c.X - cell / 2 + px * cell, c.Y - cell / 2 + py * cell, cell, cell));
                    break;
                }

                default: // classic, sleepy, angry
                    dc.DrawEllipse(White, null, c, r, r);
                    dc.DrawEllipse(Pupil, null, c + o, 2.75, 2.75);
                    break;
            }
            dc.Pop();

            // характер: верхние веки
            if (style == "sleepy")
            {
                dc.DrawRectangle(LidBrush, null, new Rect(c.X - r - 1, c.Y - r - 1, EyeSize + 2, r * 0.95));
            }
            else if (style == "angry")
            {
                var brow = new StreamGeometry();
                using (StreamGeometryContext g = brow.Open())
                {
                    bool left = i == 0;
                    g.BeginFigure(new Point(c.X - r - 1, c.Y - r - 1), true, true);
                    g.LineTo(new Point(c.X + r + 1, c.Y - r - 1), false, false);
                    g.LineTo(left ? new Point(c.X + r + 1, c.Y - 0.5) : new Point(c.X - r - 1, c.Y - 0.5), false, false);
                }
                brow.Freeze();
                dc.DrawGeometry(LidBrush, null, brow);
            }
        }

        dc.Pop();
    }
}

/// <summary>Маленькое превью стиля глаз для настроек (тоже следит за курсором).</summary>
public sealed class EyesPreview : FrameworkElement
{
    private readonly EyesControl _inner = new();

    public EyesPreview(string style)
    {
        _inner.EyeStyle = style;
        AddVisualChild(_inner);
        Width = _inner.Width;
        Height = _inner.Height;
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _inner;

    protected override Size MeasureOverride(Size availableSize)
    {
        _inner.Measure(availableSize);
        return new Size(_inner.Width, _inner.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _inner.Arrange(new Rect(finalSize));
        return finalSize;
    }
}

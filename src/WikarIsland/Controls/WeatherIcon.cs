using System;
using System.Windows;
using System.Windows.Media;
using WikarIsland.Services;

namespace WikarIsland.Controls;

/// <summary>Цветной значок погоды, нарисованный векторно по коду MET Norway (symbol_code).</summary>
public sealed class WeatherIcon : FrameworkElement
{
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(string), typeof(WeatherIcon),
        new FrameworkPropertyMetadata("cloudy", FrameworkPropertyMetadataOptions.AffectsRender));

    public string Symbol
    {
        get => (string)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }

    private static readonly Brush Sun = Frozen(new RadialGradientBrush(Color.FromRgb(0xFF, 0xE0, 0x6B), Color.FromRgb(0xFF, 0xB2, 0x1E)));
    private static readonly Pen SunRay = FrozenPen(Color.FromRgb(0xFF, 0xC4, 0x3D), 0.07);
    private static readonly Brush Moon = Frozen(new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xF5)));
    private static readonly Brush CloudLight = Frozen(new LinearGradientBrush(Color.FromRgb(0xFA, 0xFB, 0xFF), Color.FromRgb(0xC9, 0xD1, 0xE0), 90));
    private static readonly Brush CloudDark = Frozen(new LinearGradientBrush(Color.FromRgb(0xA7, 0xB0, 0xC2), Color.FromRgb(0x6E, 0x78, 0x8C), 90));
    private static readonly Pen Rain = FrozenPen(Color.FromRgb(0x4D, 0xA8, 0xFF), 0.075);
    private static readonly Brush Snow = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xFF)));
    private static readonly Brush Bolt = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)));
    private static readonly Pen Fog = FrozenPen(Color.FromRgb(0xB8, 0xC0, 0xCE), 0.07);

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static Pen FrozenPen(Color c, double width)
    {
        var p = new Pen(new SolidColorBrush(c), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        p.Freeze();
        return p;
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsNaN(Width) ? 20 : Width, double.IsNaN(Height) ? 20 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;

        // рисуем в квадрате 1×1, масштабируем под размер
        dc.PushTransform(new TranslateTransform((ActualWidth - s) / 2, (ActualHeight - s) / 2));
        dc.PushTransform(new ScaleTransform(s, s));

        string symbol = Symbol ?? "cloudy";
        string b = WeatherSymbols.Base(symbol);
        bool night = WeatherSymbols.IsNight(symbol);
        bool thunder = b.Contains("thunder");
        bool showers = b.Contains("showers");
        bool heavy = b.StartsWith("heavy", StringComparison.Ordinal);
        bool light = b.StartsWith("light", StringComparison.Ordinal);
        bool rain = b.Contains("rain");
        bool sleet = b.Contains("sleet");
        bool snow = b.Contains("snow");
        bool precip = rain || sleet || snow;

        switch (b)
        {
            case "clearsky":
                if (night) DrawMoon(dc, 0.5, 0.5, 0.34); else DrawSun(dc, 0.5, 0.5, 0.22, 0.4);
                break;
            case "fair":
                if (night) DrawMoon(dc, 0.42, 0.42, 0.28); else DrawSun(dc, 0.42, 0.42, 0.19, 0.34);
                DrawCloud(dc, 0.6, 0.72, 0.5, CloudLight);
                break;
            case "partlycloudy":
                if (night) DrawMoon(dc, 0.36, 0.36, 0.26); else DrawSun(dc, 0.36, 0.36, 0.17, 0.3);
                DrawCloud(dc, 0.56, 0.66, 0.72, CloudLight);
                break;
            case "cloudy":
                DrawCloud(dc, 0.38, 0.48, 0.5, CloudDark);
                DrawCloud(dc, 0.56, 0.62, 0.78, CloudLight);
                break;
            case "fog":
                DrawCloud(dc, 0.5, 0.42, 0.7, CloudLight);
                dc.DrawLine(Fog, new Point(0.16, 0.7), new Point(0.84, 0.7));
                dc.DrawLine(Fog, new Point(0.24, 0.84), new Point(0.76, 0.84));
                break;
            default:
                if (showers)
                {
                    if (night) DrawMoon(dc, 0.32, 0.3, 0.22); else DrawSun(dc, 0.32, 0.3, 0.14, 0.25);
                }
                DrawCloud(dc, 0.52, 0.44, 0.78, heavy || thunder ? CloudDark : CloudLight);
                if (precip) DrawPrecipitation(dc, rain, sleet, snow, light ? 2 : heavy ? 4 : 3);
                if (thunder) DrawBolt(dc);
                break;
        }

        dc.Pop();
        dc.Pop();
    }

    private static void DrawSun(DrawingContext dc, double cx, double cy, double r, double rayR)
    {
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4;
            dc.DrawLine(SunRay,
                new Point(cx + Math.Cos(a) * (r + 0.07), cy + Math.Sin(a) * (r + 0.07)),
                new Point(cx + Math.Cos(a) * rayR, cy + Math.Sin(a) * rayR));
        }
        dc.DrawEllipse(Sun, null, new Point(cx, cy), r, r);
    }

    private static void DrawMoon(DrawingContext dc, double cx, double cy, double r)
    {
        var full = new EllipseGeometry(new Point(cx, cy), r, r);
        var cut = new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.35), r * 0.85, r * 0.85);
        dc.DrawGeometry(Moon, null, new CombinedGeometry(GeometryCombineMode.Exclude, full, cut));
    }

    /// <summary>Облако шириной w с центром низа в (cx, cy).</summary>
    private static void DrawCloud(DrawingContext dc, double cx, double cy, double w, Brush fill)
    {
        double h = w * 0.36;
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new RectangleGeometry(new Rect(cx - w / 2, cy - h, w, h), h / 2, h / 2));
        g.Children.Add(new EllipseGeometry(new Point(cx - w * 0.16, cy - h * 0.95), w * 0.22, w * 0.22));
        g.Children.Add(new EllipseGeometry(new Point(cx + w * 0.12, cy - h * 1.15), w * 0.27, w * 0.27));
        dc.DrawGeometry(fill, null, g);
    }

    private static void DrawPrecipitation(DrawingContext dc, bool rain, bool sleet, bool snow, int count)
    {
        double start = 0.5 - (count - 1) * 0.1;
        for (int i = 0; i < count; i++)
        {
            double x = start + i * 0.2;
            bool flake = snow || (sleet && i % 2 == 1);
            if (flake) dc.DrawEllipse(Snow, null, new Point(x - 0.02, 0.76 + (i % 2) * 0.08), 0.055, 0.055);
            else dc.DrawLine(Rain, new Point(x + 0.03, 0.62 + (i % 2) * 0.06), new Point(x - 0.04, 0.8 + (i % 2) * 0.06));
        }
    }

    private static void DrawBolt(DrawingContext dc)
    {
        var bolt = new StreamGeometry();
        using (StreamGeometryContext c = bolt.Open())
        {
            c.BeginFigure(new Point(0.56, 0.5), true, true);
            c.LineTo(new Point(0.42, 0.74), true, false);
            c.LineTo(new Point(0.52, 0.74), true, false);
            c.LineTo(new Point(0.46, 0.94), true, false);
            c.LineTo(new Point(0.66, 0.66), true, false);
            c.LineTo(new Point(0.55, 0.66), true, false);
            c.LineTo(new Point(0.62, 0.5), true, false);
        }
        bolt.Freeze();
        dc.DrawGeometry(Bolt, null, bolt);
    }
}

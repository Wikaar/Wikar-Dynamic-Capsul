using System;
using System.Windows;
using System.Windows.Media;

namespace WikarIsland.Services;

/// <summary>
/// Тёмная и светлая тема. Все цвета острова нарисованы под тёмный фон; в светлой теме каждый цвет
/// пересчитывается: серые и фоны выворачиваются по яркости (чёрное ↔ белое), яркие акценты темнеют,
/// чтобы читаться на светлом. Разметка берёт цвета из ресурсов ThB_/ThC_, код — через Rgb/Argb/Map.
/// Смена темы применяется после перезапуска острова.
/// </summary>
public static class Theme
{
    /// <summary>Тема выбрана при запуске и до перезапуска не меняется — чтобы окна не оказались наполовину светлыми.</summary>
    private static bool? _light;

    public static bool IsLight
    {
        get
        {
            if (_light is bool v) return v;
            if (App.Settings == null) return false;
            _light = App.Settings.UiTheme == "light";
            return _light.Value;
        }
    }

    public static Color Rgb(byte r, byte g, byte b) => Map(Color.FromRgb(r, g, b));

    public static Color Argb(byte a, byte r, byte g, byte b) => Map(Color.FromArgb(a, r, g, b));

    public static Color Map(Color c) => IsLight ? ToLight(c) : c;

    /// <summary>То же для WinForms/WebView2 (System.Drawing.Color).</summary>
    public static System.Drawing.Color DrawingArgb(byte a, byte r, byte g, byte b)
    {
        Color c = Argb(a, r, g, b);
        return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(Map(c));
        b.Freeze();
        return b;
    }

    /// <summary>Положить в ресурсы приложения все цвета, на которые ссылается разметка.</summary>
    public static void Install(ResourceDictionary res)
    {
        foreach (string hex in Keys)
        {
            uint v = Convert.ToUInt32(hex, 16);
            Color c = Map(Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v));
            res["ThC_" + hex] = c;
            var b = new SolidColorBrush(c);
            b.Freeze();
            res["ThB_" + hex] = b;
        }
    }

    /// <summary>Цвет для светлой темы.</summary>
    public static Color ToLight(Color c)
    {
        if (c.A == 0) return c;
        ToHsl(c, out double h, out double s, out double l);
        if (s < 0.22 || l < 0.06 || l > 0.97)
        {
            // серые, фоны и текст: чёрное ↔ белое. Полупрозрачный белый «блик» становится полупрозрачным чёрным.
            l = 1 - l;
            if (s >= 0.22) s = 0.15;
        }
        else if (l > 0.55)
        {
            // яркий акцент (рассчитан на тёмный фон) — темнее, чтобы читался на светлом
            l = 0.32 + (l - 0.55) * 0.45;
        }
        else if (l < 0.42)
        {
            // тёмный цветной фон (кнопки, плашки) — светлый оттенок того же цвета, текст на нём станет тёмным
            l = 1 - l * 0.45;
        }
        return FromHsl(c.A, h, s, l);
    }

    private static void ToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (max - min < 1e-6)
        {
            h = 0;
            s = 0;
            return;
        }
        double d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
    }

    private static Color FromHsl(byte a, double h, double s, double l)
    {
        l = Math.Clamp(l, 0, 1);
        if (s <= 0)
        {
            byte v = (byte)Math.Round(l * 255);
            return Color.FromArgb(a, v, v, v);
        }
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromArgb(a,
            (byte)Math.Round(Hue(p, q, h + 1.0 / 3) * 255),
            (byte)Math.Round(Hue(p, q, h) * 255),
            (byte)Math.Round(Hue(p, q, h - 1.0 / 3) * 255));
    }

    private static double Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6) return p + (q - p) * 6 * t;
        if (t < 0.5) return q;
        if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
        return p;
    }

    /// <summary>Цвета из разметки (App.xaml, Views/*.xaml) в виде AARRGGBB.</summary>
    private static readonly string[] Keys =
    {
        "0FFFFFFF", "14FFFFFF", "1AFFFFFF", "1EFFFFFF", "1FFFFFFF", "248EA2FF", "26FFFFFF", "2EFFFFFF",
        "30FFFFFF", "33FFFFFF", "40FFFFFF", "44FFFFFF", "FF0B0B0E", "FF0F0F13", "FF141417", "FF141418",
        "FF16161C", "FF17171D", "FF1A1A21", "FF1E1E25", "FF1F1F24", "FF222229", "FF22222A", "FF24242A",
        "FF24242C", "FF2A1A1C", "FF2A2A31", "FF2E2E38", "FF34343C", "FF3A4580", "FF5E5CE6", "FF66C0F4",
        "FF6EE09A", "FF6F6F7A", "FF7C7C86", "FF8A8A94", "FF8C8C97", "FF8EA2FF", "FF9CB0FF", "FFA0A0AA",
        "FFB3261E", "FFB8B8C2", "FFB9A6FF", "FFC8C8D0", "FFC9C9D2", "FFD8D8DE", "FFD8D8E0", "FFF23F43",
        "FFF2F2F5", "FFF5F5F7", "FFFF453A", "FFFF6B61", "FFFF8A80", "FFFFB4AD", "FFFFFFFF",
    };
}

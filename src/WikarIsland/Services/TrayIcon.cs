using System.Drawing;
using System.Drawing.Drawing2D;

namespace WikarIsland.Services;

/// <summary>Иконка для трея рисуется в коде: чёрная пилюля с двумя глазами.</summary>
internal static class TrayIcon
{
    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using GraphicsPath pill = RoundedRect(new RectangleF(1, 8, 30, 16), 8);
            using var fill = new SolidBrush(Color.FromArgb(255, 14, 14, 18));
            using var outline = new Pen(Color.FromArgb(160, 255, 255, 255), 1.2f);
            g.FillPath(fill, pill);
            g.DrawPath(outline, pill);

            g.FillEllipse(Brushes.White, 8, 11.5f, 7, 9);
            g.FillEllipse(Brushes.White, 17, 11.5f, 7, 9);
            g.FillEllipse(fill, 10.5f, 14.5f, 3.5f, 3.5f);
            g.FillEllipse(fill, 19.5f, 14.5f, 3.5f, 3.5f);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

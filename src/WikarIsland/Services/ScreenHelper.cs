using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WF = System.Windows.Forms;

namespace WikarIsland.Services;

/// <summary>Экраны и курсор в единицах WPF (DIP), а не в физических пикселях.</summary>
internal static class ScreenHelper
{
    public static WF.Screen ScreenOf(Window window) =>
        WF.Screen.FromHandle(new WindowInteropHelper(window).EnsureHandle());

    public static Rect ToDip(System.Drawing.Rectangle r, Visual reference)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(reference);
        return new Rect(r.X / dpi.DpiScaleX, r.Y / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);
    }

    public static Point CursorDip(Visual reference)
    {
        NativeMethods.GetCursorPos(out NativeMethods.POINT p);
        DpiScale dpi = VisualTreeHelper.GetDpi(reference);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }
}

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using WF = System.Windows.Forms;

namespace WikarIsland.Services;

/// <summary>Скриншоты, запись экрана через «Ножницы», цвет пикселя.</summary>
internal static class ScreenTools
{
    private const byte VK_LWIN = 0x5B, VK_SHIFT = 0x10;
    private const uint KEYEVENTF_KEYUP = 0x2;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>Нажать Win+Shift+буква: S — снимок области, R — запись экрана («Ножницы» Windows 11).</summary>
    public static void WinShift(char letter)
    {
        byte key = (byte)char.ToUpperInvariant(letter);
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static string ScreenshotFolder
    {
        get
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Снимок всех мониторов: в файл (Изображения\Screenshots) и в буфер обмена.</summary>
    public static string TakeFullScreenshot()
    {
        Rectangle area = WF.SystemInformation.VirtualScreen;
        using var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
            g.CopyFromScreen(area.Left, area.Top, 0, 0, area.Size, CopyPixelOperation.SourceCopy);

        string path = Path.Combine(ScreenshotFolder, $"Скриншот {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
        bmp.Save(path, ImageFormat.Png);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        try { System.Windows.Clipboard.SetImage(image); } catch { /* буфер занят */ }
        return path;
    }

    /// <summary>Цвет пикселя экрана (физические координаты).</summary>
    public static System.Windows.Media.Color PixelAt(int x, int y)
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        uint c = GetPixel(dc, x, y);
        ReleaseDC(IntPtr.Zero, dc);
        return System.Windows.Media.Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
    }
}

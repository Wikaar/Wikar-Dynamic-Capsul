using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WikarIsland.Services;

/// <summary>Значки файлов и папок, запуск ярлыков, значки рабочего стола.</summary>
internal static class ShellHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_DISPLAYNAME = 0x200;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Значок файла, папки или ярлыка (32×32), как в Проводнике.</summary>
    public static ImageSource? GetIcon(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (info.hIcon == IntPtr.Zero) return null;
            BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            DestroyIcon(info.hIcon);
            src.Freeze();
            return src;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Имя для подписи: «Проводник», «Документы», «Steam» и т. п.</summary>
    public static string DisplayName(string path)
    {
        try
        {
            var info = new SHFILEINFO();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_DISPLAYNAME);
            if (!string.IsNullOrWhiteSpace(info.szDisplayName))
                return Directory.Exists(path) ? info.szDisplayName : Path.GetFileNameWithoutExtension(info.szDisplayName);
        }
        catch
        {
            // ниже — имя файла
        }
        return Path.GetFileNameWithoutExtension(path.TrimEnd('\\'));
    }

    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            MessageBox.Show($"Не удалось открыть:\n{path}\n\n{ex.Message}", "Wikar Dynamic Capsul", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void ShowInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ---------- значки рабочего стола ----------

    private static IntPtr DesktopView()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr view = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (view != IntPtr.Zero) return view;

        // после смены обоев вид может жить в одном из окон WorkerW
        IntPtr worker = IntPtr.Zero;
        while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
        {
            view = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (view != IntPtr.Zero) return view;
        }
        return IntPtr.Zero;
    }

    public static bool DesktopIconsVisible
    {
        get
        {
            IntPtr view = DesktopView();
            IntPtr list = view == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
            return list == IntPtr.Zero || NativeMethods.IsWindowVisible(list);
        }
    }

    /// <summary>То же, что «Вид → Отображать значки рабочего стола» в меню рабочего стола.</summary>
    public static void SetDesktopIcons(bool visible)
    {
        if (DesktopIconsVisible == visible) return;
        IntPtr view = DesktopView();
        if (view != IntPtr.Zero) SendMessage(view, 0x0111 /* WM_COMMAND */, new IntPtr(0x7402), IntPtr.Zero);
    }
}

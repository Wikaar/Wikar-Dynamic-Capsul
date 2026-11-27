using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WikarIsland.Services;

/// <summary>Режим «Не беспокоить»: вручную, по расписанию или пока открыта игра/видео на весь экран.</summary>
public static class FocusService
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Сейчас на переднем плане что-то на весь экран (игра, видео, презентация)?</summary>
    public static bool FullscreenAppActive()
    {
        try
        {
            if (SHQueryUserNotificationState(out int state) == 0 && state is 3 or 4) return true; // D3D-игра, презентация

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == GetShellWindow() || fg == GetDesktopWindow()) return false;
            if (NativeMethods.ForegroundIsOurs()) return false;

            var cls = new StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            string c = cls.ToString();
            if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

            if (!GetWindowRect(fg, out RECT r)) return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref info)) return false;
            RECT m = info.rcMonitor;
            return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        }
        catch
        {
            return false;
        }
    }

    private static bool _fullscreen;

    /// <summary>Обновляется таймером пилюли раз в секунду.</summary>
    public static void Poll() => _fullscreen = App.Settings.DndFullscreen && FullscreenAppActive();

    public static bool InSchedule()
    {
        AppSettings s = App.Settings;
        if (!s.DndSchedule) return false;
        if (!TimeSpan.TryParse(s.DndFrom, out TimeSpan from) || !TimeSpan.TryParse(s.DndTo, out TimeSpan to)) return false;
        TimeSpan now = DateTime.Now.TimeOfDay;
        return from <= to ? now >= from && now < to : now >= from || now < to; // через полночь: 23:00–08:00
    }

    /// <summary>Не показывать всплывающие уведомления.</summary>
    public static bool Active => App.Settings.DndManual || InSchedule() || _fullscreen;

    /// <summary>Прятать саму капсулу (только для полноэкранных приложений).</summary>
    /// <summary>Игровая строка с FPS важнее: в игре капсулу не прячем.</summary>
    /// <summary>С «Поверх всех окон и игр» капсулу не прячем никогда.</summary>
    public static bool HidePill => _fullscreen && App.Settings.DndHidePill && !App.Settings.AlwaysOnTop
                                   && !(App.Settings.GameHud && App.Game?.Active == true);
}

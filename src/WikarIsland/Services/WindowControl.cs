using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace WikarIsland.Services;

/// <summary>Открытое окно программы или игры.</summary>
public sealed record OpenWindow(IntPtr Handle, int ProcessId, string ProcessName, string Title, string Product);

/// <summary>
/// Голосом: закрыть, свернуть, развернуть программу или игру по названию, свернуть все окна.
/// Название сравнивается с именем процесса, заголовком окна и названием продукта (транслитом, как при запуске).
/// </summary>
public static class WindowControl
{
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;
    private const uint WM_CLOSE = 0x0010;
    private const uint GW_OWNER = 4;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_CLOAKED = 14;

    // своё и системное не трогаем
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "WikarIsland", "dwm", "csrss", "winlogon", "LogonUI", "ShellExperienceHost", "StartMenuExperienceHost",
        "SearchHost", "SearchApp", "TextInputHost", "LockApp", "SystemSettingsBroker",
    };

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    /// <summary>Все обычные окна верхнего уровня (видимые или свёрнутые), кроме острова и оболочки Windows.</summary>
    public static List<OpenWindow> All()
    {
        var result = new List<OpenWindow>();
        var products = new Dictionary<int, (string name, string product)>();
        int own = Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
                if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0) return true;
                if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                string title = Text(hwnd);
                if (title.Length == 0 || ShellClasses.Contains(ClassName(hwnd))) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == own) return true;
                if (!products.TryGetValue((int)pid, out var info))
                {
                    info = Describe((int)pid);
                    products[(int)pid] = info;
                }
                if (info.name.Length == 0 || Protected.Contains(info.name)) return true;
                result.Add(new OpenWindow(hwnd, (int)pid, info.name, title, info.product));
            }
            catch
            {
                // окно закрылось, пока перебирали
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Окна, подходящие под название (все окна лучшего процесса). Пусто — ничего похожего не открыто.</summary>
    public static List<OpenWindow> Find(IEnumerable<string> queries, out string shownName)
    {
        shownName = "";
        List<OpenWindow> windows = All();
        OpenWindow? best = null;
        double bestScore = 0;
        foreach (string query in queries.Where(q => q.Trim().Length >= 2).Distinct())
        {
            foreach (OpenWindow w in windows)
            {
                double s = Math.Max(AppIndex.Similarity(query, w.ProcessName),
                           Math.Max(AppIndex.Similarity(query, w.Title), AppIndex.Similarity(query, w.Product)));
                if (s > bestScore)
                {
                    bestScore = s;
                    best = w;
                }
            }
        }
        if (best == null || bestScore < 0.75) return new List<OpenWindow>();
        shownName = best.Product.Length > 0 ? best.Product : best.ProcessName;
        int pid = best.ProcessId;
        // ApplicationFrameHost держит все окна приложений Store — для них берём только найденное окно
        if (best.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            shownName = best.Title;
            return new List<OpenWindow> { best };
        }
        return windows.Where(w => w.ProcessId == pid).ToList();
    }

    /// <summary>Вежливо закрыть (как крестик). force — завершить процесс, если программа не отвечает или держит окно.</summary>
    public static void Close(List<OpenWindow> windows, bool force)
    {
        foreach (OpenWindow w in windows) PostMessage(w.Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        if (!force) return;
        foreach (int pid in windows.Select(w => w.ProcessId).Distinct())
        {
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (Protected.Contains(p.ProcessName) || p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) continue;
                if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                App.LogError(ex); // нет прав (игра запущена от администратора) или уже закрылась
            }
        }
    }

    public static void Minimize(List<OpenWindow> windows)
    {
        foreach (OpenWindow w in windows) ShowWindow(w.Handle, SW_MINIMIZE);
    }

    /// <summary>Развернуть и вывести на передний план.</summary>
    public static void Restore(List<OpenWindow> windows)
    {
        foreach (OpenWindow w in windows)
            if (IsIconic(w.Handle)) ShowWindow(w.Handle, SW_RESTORE);
        if (windows.Count > 0) SetForegroundWindow(windows[0].Handle);
    }

    /// <summary>Свернуть все окна (как Win+M). Остров не сворачивается — он поверх всего.</summary>
    public static int MinimizeAll()
    {
        List<OpenWindow> windows = All().Where(w => !IsIconic(w.Handle)).ToList();
        Minimize(windows);
        _lastMinimized = windows.Select(w => w.Handle).ToList();
        return windows.Count;
    }

    private static List<IntPtr> _lastMinimized = new();

    /// <summary>Вернуть окна, свёрнутые командой «сверни всё».</summary>
    public static int RestoreAll()
    {
        int n = 0;
        foreach (IntPtr h in _lastMinimized)
        {
            if (!IsWindow(h) || !IsIconic(h)) continue;
            ShowWindow(h, SW_RESTORE);
            n++;
        }
        _lastMinimized = new List<IntPtr>();
        return n;
    }

    public static void MinimizeForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || NativeMethods.ForegroundIsOurs() || ShellClasses.Contains(ClassName(fg))) return;
        ShowWindow(fg, SW_MINIMIZE);
    }

    private static (string name, string product) Describe(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            string name = p.ProcessName;
            string product = "";
            try
            {
                FileVersionInfo? v = p.MainModule?.FileVersionInfo;
                product = (v?.FileDescription is { Length: > 0 } d ? d : v?.ProductName) ?? "";
            }
            catch
            {
                // процесс от администратора или 32-битный — модуль не прочитать, хватит имени
            }
            return (name, product.Trim());
        }
        catch
        {
            return ("", "");
        }
    }

    private static string Text(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Trim();
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Automation;

namespace WikarIsland.Services;

/// <summary>
/// Запасной вариант: YouTube, открытый в обычном браузере (Chrome, Edge, Яндекс Браузер, Firefox, Opera, Brave).
/// Капсула находит окно и вкладку с YouTube (через UI Automation — как экранные дикторы), выводит их вперёд
/// и нажимает клавиши самого YouTube: K — пауза, Shift+N — следующее видео, L/J — ±10 секунд, F — весь экран.
/// </summary>
public static class BrowserYouTube
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "browser", "firefox", "opera", "brave", "vivaldi", "yandex", "chromium", "arc",
    };

    private static bool IsYouTube(string title) =>
        title.Contains("YouTube", StringComparison.OrdinalIgnoreCase) && !title.Contains("YouTube Music", StringComparison.OrdinalIgnoreCase);

    /// <summary>Окна браузеров, первым — то, где YouTube на активной вкладке.</summary>
    private static List<OpenWindow> BrowserWindows() =>
        WindowControl.All().Where(w => Browsers.Contains(w.ProcessName))
            .OrderByDescending(w => IsYouTube(w.Title))
            .ToList();

    /// <summary>YouTube на активной вкладке какого-нибудь браузера (быстро, без перебора вкладок).</summary>
    public static bool HasActiveTab => WindowControl.All().Any(w => Browsers.Contains(w.ProcessName) && IsYouTube(w.Title));

    /// <summary>
    /// Найти YouTube и вывести его вперёд: окно с ним на активной вкладке или окно, где он открыт в другой вкладке.
    /// </summary>
    public static bool Focus(out string browser)
    {
        browser = "";
        List<OpenWindow> windows = BrowserWindows();
        OpenWindow? active = windows.FirstOrDefault(w => IsYouTube(w.Title));
        if (active != null)
        {
            browser = active.Product.Length > 0 ? active.Product : active.ProcessName;
            WindowControl.Restore(new List<OpenWindow> { active });
            Thread.Sleep(250);
            return true;
        }
        foreach (OpenWindow w in windows)
        {
            if (!SelectTab(w)) continue;
            browser = w.Product.Length > 0 ? w.Product : w.ProcessName;
            WindowControl.Restore(new List<OpenWindow> { w });
            Thread.Sleep(350);
            return true;
        }
        return false;
    }

    /// <summary>Вкладка с YouTube в окне браузера: выбрать её.</summary>
    private static bool SelectTab(OpenWindow w)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(w.Handle);
            AutomationElementCollection tabs = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            foreach (AutomationElement tab in tabs)
            {
                string name;
                try { name = tab.Current.Name; } catch { continue; }
                if (!IsYouTube(name)) continue;
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object sel))
                {
                    ((SelectionItemPattern)sel).Select();
                    return true;
                }
                if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out object inv))
                {
                    ((InvokePattern)inv).Invoke();
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return false;
    }

    /// <summary>Нажать клавиши в YouTube в браузере. null — YouTube в браузере не открыт.</summary>
    public static string? Keys(params string[] combos)
    {
        if (!Focus(out string browser)) return null;
        foreach (string c in combos)
        {
            AppAutomation.Press(c);
            Thread.Sleep(70);
        }
        return browser;
    }

    /// <summary>Открыть ролик в той же вкладке YouTube (адресная строка: Ctrl+L, адрес, Enter) или новой, если YouTube не открыт.</summary>
    public static string Open(string url)
    {
        if (Focus(out string browser))
        {
            AppAutomation.Press("ctrl+l");
            Thread.Sleep(150);
            AppAutomation.TypeText(url);
            Thread.Sleep(60);
            AppAutomation.Press("enter");
            return browser;
        }
        AppIndex.OpenUri(url);
        return "";
    }

    /// <summary>Закрыть вкладку с YouTube (Ctrl+W), не трогая остальные вкладки браузера.</summary>
    public static bool CloseTab()
    {
        if (!Focus(out _)) return false;
        AppAutomation.Press("ctrl+w");
        return true;
    }
}

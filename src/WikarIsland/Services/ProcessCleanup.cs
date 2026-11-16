using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WikarIsland.Services;

/// <summary>
/// «Освободи память» и «закрой всё, кроме …». Только программы с окнами на панели задач:
/// фоновые службы, системные процессы, сам остров и запущенную игру не трогаем.
/// Закрываем вежливо (как крестиком), чтобы программа успела спросить о несохранённом.
/// </summary>
public static class ProcessCleanup
{
    /// <summary>Программа на панели задач: все её окна и сколько памяти занимают все её процессы.</summary>
    public sealed record AppUsage(string ProcessName, string Shown, long Bytes, List<OpenWindow> Windows, bool IsGame);

    /// <summary>Что закрыть и как сказать об этом пользователю.</summary>
    public sealed record Plan(string Question, List<OpenWindow> Windows, string Done);

    private static readonly string[] Launchers = { "steam", "steamwebhelper", "EpicGamesLauncher", "Battle.net", "Agent", "EADesktop", "upc", "GalaxyClient", "RiotClientServices", "RiotClientUx" };

    private const long MinBytes = 250L * 1024 * 1024;

    // ---------- «освободи память» ----------

    public static Plan? FreeMemoryPlan(out string nothing)
    {
        List<AppUsage> apps = Apps();
        bool gameRunning = apps.Any(a => a.IsGame);
        List<AppUsage> heavy = apps
            .Where(a => !a.IsGame && !IsExplorer(a.ProcessName) && !(gameRunning && IsLauncher(a.ProcessName)) && a.Bytes >= MinBytes)
            .OrderByDescending(a => a.Bytes)
            .Take(5)
            .ToList();
        (long free, long total) = Ram();
        if (heavy.Count == 0)
        {
            nothing = total > 0
                ? $"Тяжёлых программ не открыто, сэр. Свободно {Size(free)} из {Size(total)}."
                : "Тяжёлых программ не открыто, сэр.";
            return null;
        }
        nothing = "";
        long sum = heavy.Sum(a => a.Bytes);
        string list = string.Join(", ", heavy.Select(a => $"{a.Shown} — {Size(a.Bytes)}"));
        string names = JoinNames(heavy.Select(a => a.Shown).ToList());
        string game = gameRunning ? " Игру не трогаю." : "";
        return new Plan(
            $"Больше всего памяти занимают: {list}. Закрыть их? Освободится около {Size(sum)}.{game}",
            heavy.SelectMany(a => a.Windows).ToList(),
            $"Закрываю {names}.");
    }

    // ---------- «закрой всё, кроме …» ----------

    /// <param name="keep">Названия программ, которые оставить (как сказал пользователь).</param>
    /// <param name="includeGames">Закрыть и игру — только по явной просьбе.</param>
    public static Plan? CloseAllExceptPlan(IEnumerable<string> keep, bool includeGames, out string nothing)
    {
        List<AppUsage> apps = Apps(withMemory: false);
        var keepHandles = new HashSet<IntPtr>();
        var kept = new List<string>();
        var missing = new List<string>();
        foreach (string raw in keep.Select(k => k.Trim()).Where(k => k.Length >= 2))
        {
            List<OpenWindow> found = WindowControl.Find(Variants(raw), out string shown);
            if (found.Count == 0)
            {
                missing.Add(raw);
                continue;
            }
            foreach (OpenWindow w in found) keepHandles.Add(w.Handle);
            kept.Add(shown);
        }

        bool gameRunning = apps.Any(a => a.IsGame);
        var close = new List<AppUsage>();
        foreach (AppUsage a in apps)
        {
            if (a.IsGame && !includeGames) continue;
            if (gameRunning && !includeGames && IsLauncher(a.ProcessName)) continue; // лаунчер игры держит её вход в аккаунт
            List<OpenWindow> rest = a.Windows.Where(w => !keepHandles.Contains(w.Handle)).ToList();
            if (rest.Count > 0) close.Add(a with { Windows = rest });
        }

        string missingText = missing.Count > 0 ? $" «{string.Join("», «", missing)}» не нашёл среди открытых." : "";
        if (close.Count == 0)
        {
            nothing = "Закрывать нечего, сэр." + missingText;
            return null;
        }
        nothing = "";
        string names = JoinNames(close.Select(a => a.Shown).Distinct().ToList());
        string keepText = kept.Count > 0 ? $" Оставлю {JoinNames(kept.Distinct().ToList())}." : "";
        string gameText = !includeGames && gameRunning ? " Игру не трогаю." : "";
        return new Plan(
            $"Закрою {names}.{keepText}{gameText}{missingText} Закрыть?",
            close.SelectMany(a => a.Windows).ToList(),
            $"Закрываю {names}.");
    }

    public static void Execute(Plan plan) => WindowControl.Close(plan.Windows, force: false);

    /// <summary>«дискорда и телеграма» → «дискорда», «телеграма».</summary>
    public static List<string> SplitNames(string text) =>
        Regex.Split(text, @"\s*(?:,|\bи\b|\bа также\b)\s*").Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    // ---------- что открыто ----------

    public static List<AppUsage> Apps(bool withMemory = true)
    {
        var result = new List<AppUsage>();
        List<OpenWindow> windows = WindowControl.All();
        int gamePid = App.Game?.Active == true ? App.Game.Pid : -1;
        foreach (IGrouping<string, OpenWindow> g in windows.GroupBy(w =>
                     w.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) ? "uwp:" + w.Title : w.ProcessName,
                     StringComparer.OrdinalIgnoreCase))
        {
            OpenWindow first = g.First();
            bool uwp = g.Key.StartsWith("uwp:", StringComparison.Ordinal);
            string shown = uwp ? first.Title : first.Product.Length > 0 ? first.Product : first.ProcessName;
            if (shown.Length > 40) shown = shown[..40];
            bool game = g.Any(w => w.ProcessId == gamePid) || IsGameProcess(first.ProcessId, first.ProcessName);
            long bytes = withMemory && !uwp ? Memory(first.ProcessName) : 0;
            result.Add(new AppUsage(first.ProcessName, shown, bytes, g.ToList(), game));
        }
        return result;
    }

    /// <summary>Игра: процесс из папки игр Steam, Epic или игра Battle.net.</summary>
    public static bool IsGameProcess(int pid, string processName)
    {
        if (BattleNetService.GameProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase)) return true;
        string? path = ImagePath(pid);
        if (path == null) return false;
        if (path.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase)) return true;
        return EpicFolders().Any(f => path.StartsWith(f, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string>? _epicFolders;

    private static List<string> EpicFolders()
    {
        if (_epicFolders != null) return _epicFolders;
        var list = new List<string>();
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (Directory.Exists(dir))
                foreach (string file in Directory.EnumerateFiles(dir, "*.item"))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (doc.RootElement.TryGetProperty("InstallLocation", out JsonElement loc) && loc.GetString() is { Length: > 3 } p)
                            list.Add(p.TrimEnd('\\') + "\\");
                    }
                    catch
                    {
                        // битый манифест
                    }
                }
        }
        catch
        {
            // нет Epic
        }
        return _epicFolders = list;
    }

    private static bool IsExplorer(string name) => name.Equals("explorer", StringComparison.OrdinalIgnoreCase);

    private static bool IsLauncher(string name) => Launchers.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static List<string> Variants(string name)
    {
        var list = new List<string>(AssistantService.NameVariants(name));
        // «дискорда», «телеграму», «хрома» — без падежного окончания
        string stem = Regex.Replace(name, @"(ами|ями|ов|ей|ом|ем|ой|ую|а|я|у|ю|ы|и|е)$", "");
        if (stem.Length >= 3 && stem != name) list.AddRange(AssistantService.NameVariants(stem));
        return list.Distinct().ToList();
    }

    /// <summary>Сколько памяти занимают все процессы программы (как «Рабочий набор» в диспетчере задач).</summary>
    private static long Memory(string processName)
    {
        long sum = 0;
        foreach (Process p in Process.GetProcessesByName(processName))
        {
            try
            {
                if (p.SessionId != 0) sum += p.WorkingSet64;
            }
            catch
            {
                // закрылся
            }
            finally
            {
                p.Dispose();
            }
        }
        return sum;
    }

    // ---------- текст ----------

    public static string Size(long bytes)
    {
        double gb = bytes / 1024d / 1024 / 1024;
        if (gb >= 1)
        {
            double r = Math.Round(gb, gb >= 10 ? 0 : 1);
            // дробное — всегда «гигабайта»: «2,1 гигабайта»
            return r % 1 != 0
                ? r.ToString("0.#", new System.Globalization.CultureInfo("ru-RU")) + " гигабайта"
                : (long)r + " " + Plural((long)r, "гигабайт", "гигабайта", "гигабайт");
        }
        long mb = Math.Max(10, (long)Math.Round(bytes / 1024d / 1024 / 10) * 10);
        return mb + " " + Plural(mb, "мегабайт", "мегабайта", "мегабайт");
    }

    private static string Plural(long n, string one, string few, string many)
    {
        long d = n % 100;
        if (d is >= 11 and <= 14) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    private static string JoinNames(List<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => string.Join(", ", names.Take(names.Count - 1)) + " и " + names[^1],
    };

    // ---------- система ----------

    public static (long free, long total) Ram()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref s) ? ((long)s.ullAvailPhys, (long)s.ullTotalPhys) : (0, 0);
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

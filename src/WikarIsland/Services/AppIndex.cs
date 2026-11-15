using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WikarIsland.Services;

public sealed record AppEntry(string Name, string Kind, Action Launch);

/// <summary>
/// Что можно запустить голосом: ярлыки острова, игры Steam и Epic, программы из меню «Пуск»,
/// приложения из Microsoft Store и ярлыки рабочего стола.
/// Распознавание пишет названия кириллицей («вальхейм», «майнкрафт»), поэтому сравниваем
/// транслитом с поправками на типичные расхождения.
/// </summary>
public static class AppIndex
{
    private static List<AppEntry> _entries = new();
    private static DateTime _builtAt;

    private static readonly string[] Junk =
    {
        "uninstall", "удал", "readme", "help", "справк", "documentation", "release notes", "website", "сайт", "license", "лиценз",
    };

    private static int _building;

    /// <summary>
    /// Список для поиска. Устаревший обновляется в фоне — голосовая команда не ждёт пересканирования
    /// Steam, «Пуска» и Store (это секунды). Синхронно строим только самый первый раз.
    /// </summary>
    public static List<AppEntry> Entries
    {
        get
        {
            if (_entries.Count == 0) Build();
            else if ((DateTime.Now - _builtAt).TotalMinutes > 10) BuildInBackground();
            return _entries;
        }
    }

    /// <summary>Пересобрать список в фоне (при старте острова и когда он устарел).</summary>
    public static void BuildInBackground()
    {
        if (System.Threading.Interlocked.Exchange(ref _building, 1) == 1) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Build();
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _building, 0);
            }
        });
    }

    /// <summary>Названия игр для подсказки ИИ (чтобы он угадывал искажённые распознаванием).</summary>
    public static IEnumerable<string> GameNames => Entries.Where(e => e.Kind is "Steam" or "Epic").Select(e => e.Name);

    public static void Build()
    {
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string kind, Action launch)
        {
            name = name.Trim();
            if (name.Length == 0 || !seen.Add(name)) return;
            list.Add(new AppEntry(name, kind, launch));
        }

        foreach (LauncherItem l in App.Settings.Launcher)
        {
            string path = l.Path;
            Add(l.Name, "Ярлык", () => ShellHelper.Open(path));
        }

        foreach ((int id, string name) in App.Steam.InstalledGames())
            Add(name, "Steam", () => SteamService.Launch(id));

        foreach ((string name, string uri) in EpicGames())
            Add(name, "Epic", () => OpenUri(uri));

        foreach (string lnk in StartMenuShortcuts())
        {
            string name = Path.GetFileNameWithoutExtension(lnk);
            if (Junk.Any(j => name.Contains(j, StringComparison.OrdinalIgnoreCase))) continue;
            Add(name, "Пуск", () => ShellHelper.Open(lnk));
        }

        // приложения из Microsoft Store (Spotify, WhatsApp, Telegram, Xbox…): ярлыков .lnk у них нет
        foreach ((string name, Windows.ApplicationModel.Core.AppListEntry entry) in StoreApps())
        {
            if (Junk.Any(j => name.Contains(j, StringComparison.OrdinalIgnoreCase))) continue;
            Windows.ApplicationModel.Core.AppListEntry app = entry;
            Add(name, "Store", () => _ = app.LaunchAsync());
        }

        foreach (string lnk in DesktopShortcuts())
        {
            string name = Path.GetFileNameWithoutExtension(lnk);
            if (Junk.Any(j => name.Contains(j, StringComparison.OrdinalIgnoreCase))) continue;
            Add(name, "Рабочий стол", () => ShellHelper.Open(lnk));
        }

        _entries = list;
        _builtAt = DateTime.Now;
    }

    public static (AppEntry? entry, double score) Find(string query)
    {
        string q = Fold(query);
        if (q.Length < 2) return (null, 0);
        AppEntry? best = null;
        double bestScore = 0;
        foreach (AppEntry e in Entries)
        {
            double s = Score(q, Fold(e.Name));
            // игры и ярлыки важнее одноимённых пунктов «Пуска»
            if (e.Kind != "Пуск") s += 0.02;
            if (s > bestScore)
            {
                bestScore = s;
                best = e;
            }
        }
        return (best, bestScore);
    }

    private static double Score(string q, string name)
    {
        if (name.Length == 0) return 0;
        if (q == name) return 1;
        if (name.StartsWith(q, StringComparison.Ordinal) && q.Length >= 4) return 0.93;
        if (name.Contains(q, StringComparison.Ordinal) && q.Length >= 4) return 0.88;
        if (q.Contains(name, StringComparison.Ordinal) && name.Length >= 4) return 0.85;
        double whole = 1 - (double)Levenshtein(q, name) / Math.Max(q.Length, name.Length);
        // «контр страйк» против «counterstrike2»: сравниваем и с началом названия той же длины
        string head = name.Length > q.Length ? name[..q.Length] : name;
        double prefix = 1 - (double)Levenshtein(q, head) / Math.Max(q.Length, head.Length);
        return Math.Max(whole, prefix * 0.92);
    }

    /// <summary>Нижний регистр, транслит, только буквы и цифры, сглаживание похожих звуков.</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.ToLowerInvariant())
        {
            sb.Append(ch switch
            {
                'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'д' => "d", 'е' => "e", 'ё' => "e", 'ж' => "zh",
                'з' => "z", 'и' => "i", 'й' => "i", 'к' => "k", 'л' => "l", 'м' => "m", 'н' => "n", 'о' => "o",
                'п' => "p", 'р' => "r", 'с' => "s", 'т' => "t", 'у' => "u", 'ф' => "f", 'х' => "h", 'ц' => "ts",
                'ч' => "ch", 'ш' => "sh", 'щ' => "sh", 'ъ' => "", 'ы' => "i", 'ь' => "", 'э' => "e", 'ю' => "iu",
                'я' => "ia",
                _ when char.IsLetterOrDigit(ch) => ch.ToString(),
                _ => "",
            });
        }
        string r = sb.ToString();
        r = r.Replace("ch", "\u0001").Replace("sh", "\u0002")
             .Replace("ph", "f").Replace("ck", "k").Replace("kh", "h").Replace("qu", "kv").Replace("q", "k")
             .Replace("x", "ks").Replace("c", "k").Replace("w", "v").Replace("y", "i").Replace("j", "i")
             .Replace("ee", "i").Replace("oo", "u").Replace("ea", "i").Replace("ou", "au")
             .Replace("\u0001", "ch").Replace("\u0002", "sh");
        // двойные буквы — одна
        var dedup = new StringBuilder();
        foreach (char ch in r)
            if (dedup.Length == 0 || dedup[^1] != ch) dedup.Append(ch);
        return dedup.ToString();
    }

    private static int Levenshtein(string a, string b)
    {
        var d = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) d[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            int prev = d[0];
            d[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int temp = d[j];
                d[j] = Math.Min(Math.Min(d[j] + 1, d[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = temp;
            }
        }
        return d[b.Length];
    }

    // ---------- источники ----------

    private static IEnumerable<(string name, string uri)> EpicGames()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (string file in Directory.EnumerateFiles(dir, "*.item"))
        {
            string? name = null, uri = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                JsonElement r = doc.RootElement;
                name = r.GetProperty("DisplayName").GetString();
                string app = r.GetProperty("AppName").GetString() ?? "";
                string ns = r.TryGetProperty("CatalogNamespace", out JsonElement n) ? n.GetString() ?? "" : "";
                string item = r.TryGetProperty("CatalogItemId", out JsonElement i) ? i.GetString() ?? "" : "";
                uri = ns.Length > 0 && item.Length > 0
                    ? $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true"
                    : $"com.epicgames.launcher://apps/{app}?action=launch&silent=true";
            }
            catch
            {
                // битый манифест
            }
            if (name != null && uri != null) yield return (name, uri);
        }
    }

    private static List<(string name, Windows.ApplicationModel.Core.AppListEntry entry)> StoreApps()
    {
        var result = new List<(string, Windows.ApplicationModel.Core.AppListEntry)>();
        try
        {
            var manager = new Windows.Management.Deployment.PackageManager();
            foreach (Windows.ApplicationModel.Package package in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || package.IsBundle) continue;
                    foreach (Windows.ApplicationModel.Core.AppListEntry entry in package.GetAppListEntriesAsync().AsTask().GetAwaiter().GetResult())
                    {
                        string name = entry.DisplayInfo?.DisplayName ?? "";
                        // у системных пакетов бывают служебные имена вида «ms-resource:…» или «@{…}»
                        if (name.Length == 0 || name.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase) || name.StartsWith("@", StringComparison.Ordinal)) continue;
                        result.Add((name, entry));
                    }
                }
                catch
                {
                    // повреждённый или недоступный пакет — пропускаем
                }
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return result;
    }

    private static IEnumerable<string> DesktopShortcuts()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (string root in roots)
        {
            if (root.Length == 0 || !Directory.Exists(root)) continue;
            List<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch
            {
                continue;
            }
            foreach (string f in files) yield return f;
        }
    }

    private static IEnumerable<string> StartMenuShortcuts()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };
        foreach (string root in roots)
        {
            string programs = Path.Combine(root, "Programs");
            if (!Directory.Exists(programs)) continue;
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(programs, "*.*", SearchOption.AllDirectories)
                    .Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch
            {
                continue;
            }
            foreach (string f in files) yield return f;
        }
    }

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}

/// <summary>Числа словами из распознавания: «тридцать пять» → 35, «полчаса», «полтора».</summary>
public static class RuNumbers
{
    private static readonly Dictionary<string, int> Words = new()
    {
        ["ноль"] = 0, ["один"] = 1, ["одну"] = 1, ["одна"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4,
        ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
        ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14, ["пятнадцать"] = 15,
        ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19,
        ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60,
        ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90, ["сто"] = 100, ["двести"] = 200,
    };

    /// <summary>Заменить в тексте числа словами на цифры: «таймер на пять минут» → «таймер на 5 минут».</summary>
    public static string Digitize(string text)
    {
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        int i = 0;
        while (i < tokens.Length)
        {
            string t = tokens[i].ToLowerInvariant();
            if (t == "полчаса") { result.Add("30 минут"); i++; continue; }
            if (t is "полтора" or "полторы") { result.Add("1,5"); i++; continue; }
            if (Words.TryGetValue(t, out int n))
            {
                int sum = n;
                i++;
                // «сто двадцать пять», «тридцать пять»
                while (i < tokens.Length && Words.TryGetValue(tokens[i].ToLowerInvariant(), out int next) && next < LastUnit(sum))
                {
                    sum += next;
                    i++;
                }
                result.Add(sum.ToString());
                continue;
            }
            result.Add(tokens[i]);
            i++;
        }
        return string.Join(' ', result);
    }

    private static int LastUnit(int sum) => sum % 10 != 0 ? 1 : sum % 100 != 0 ? 10 : 100;
}

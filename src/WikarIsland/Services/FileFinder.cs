using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WikarIsland.Services;

/// <summary>
/// Поиск файлов и папок голосом: «найди документ отчёт и открой папку», «найди все файлы Valheim», «открой загрузки».
/// Сначала спрашиваем индекс поиска Windows (мгновенно, папки пользователя), если он ничего не дал —
/// сами обходим папки пользователя и другие диски с ограничением по времени.
/// </summary>
public static class FileFinder
{
    public sealed record Found(string Path, bool IsFolder, DateTime Modified, long Size);

    /// <summary>Последние найденные — для «открой второй», «открой папку первого».</summary>
    public static List<Found> Last { get; private set; } = new();

    private static readonly Dictionary<string, string[]> Kinds = new()
    {
        ["document"] = new[] { ".doc", ".docx", ".pdf", ".odt", ".rtf", ".txt", ".md" },
        ["table"] = new[] { ".xls", ".xlsx", ".csv", ".ods" },
        ["presentation"] = new[] { ".ppt", ".pptx", ".odp" },
        ["pdf"] = new[] { ".pdf" },
        ["image"] = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic" },
        ["video"] = new[] { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv" },
        ["audio"] = new[] { ".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac" },
        ["archive"] = new[] { ".zip", ".rar", ".7z", ".tar", ".gz" },
        ["program"] = new[] { ".exe", ".msi", ".lnk" },
    };

    /// <summary>Слово из речи → тип файла.</summary>
    public static string? KindFromWords(string text)
    {
        string t = text.ToLowerInvariant();
        if (Regex.IsMatch(t, @"\bpdf\b|\bпдф\b")) return "pdf";
        if (Regex.IsMatch(t, @"документ|текстов|ворд|word")) return "document";
        if (Regex.IsMatch(t, @"таблиц|эксель|excel")) return "table";
        if (Regex.IsMatch(t, @"презентац")) return "presentation";
        if (Regex.IsMatch(t, @"фото|картинк|изображен|скриншот|снимок")) return "image";
        if (Regex.IsMatch(t, @"видео|фильм|ролик")) return "video";
        if (Regex.IsMatch(t, @"песн|музык|трек|аудио")) return "audio";
        if (Regex.IsMatch(t, @"архив")) return "archive";
        if (Regex.IsMatch(t, @"установщик|инсталлятор")) return "program";
        return null;
    }

    // ---------- поиск по имени ----------

    public static async Task<List<Found>> SearchAsync(string query, string? kind, CancellationToken token = default)
    {
        List<string> words = Words(query);
        if (words.Count == 0) return Last = new List<Found>();
        string[] ext = kind != null && Kinds.TryGetValue(kind, out string[]? e) ? e : Array.Empty<string>();

        List<Found> result = await Task.Run(() => SearchIndex(words, ext), token);
        if (result.Count == 0) result = await Task.Run(() => SearchIndex(words.Select(AppIndex.Fold).ToList(), ext), token);
        if (result.Count == 0) result = await Task.Run(() => Scan(words, ext, token), token);
        return Last = result
            .GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderByDescending(f => Score(f, words))
            .ThenByDescending(f => f.Modified)
            .Take(20)
            .ToList();
    }

    /// <summary>Слова запроса без служебных и окончаний: «отчёта за март» → «отчет», «март».</summary>
    private static List<string> Words(string query)
    {
        string q = query.ToLowerInvariant().Replace('ё', 'е');
        q = Regex.Replace(q, @"[«»""'.,!?]", " ");
        var stop = new HashSet<string> { "файл", "файлы", "файла", "папку", "папка", "документ", "документы", "на", "в", "во", "за", "с", "и", "мой", "мои", "моя", "компьютере", "компе", "который", "которая", "под", "названием", "называется" };
        return q.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !stop.Contains(w))
            .Select(w => w.Length > 5 && Regex.IsMatch(w, "[а-я]") ? Regex.Replace(w, @"(ами|ями|ого|его|ому|ему|ах|ях|ов|ей|ом|ем|ой|ую|а|я|у|ю|ы|и|е)$", "") : w)
            .Where(w => w.Length >= 2)
            .Distinct()
            .Take(5)
            .ToList();
    }

    private static int Score(Found f, List<string> words)
    {
        string name = AppIndex.Fold(Path.GetFileNameWithoutExtension(f.Path));
        int s = words.Count(w => name.Contains(AppIndex.Fold(w)));
        if (f.Path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) s -= 2;
        return s;
    }

    /// <summary>Индекс поиска Windows (тот же, что у поиска в «Пуске»). Без него — пустой список.</summary>
    private static List<Found> SearchIndex(List<string> words, string[] ext)
    {
        var result = new List<Found>();
        dynamic? conn = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("ADODB.Connection");
            if (type == null) return result;
            conn = Activator.CreateInstance(type);
            conn!.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");
            string where = string.Join(" AND ", words.Select(w => $"System.FileName LIKE '%{Sql(w)}%'"));
            if (ext.Length > 0) where += " AND System.FileExtension IN (" + string.Join(",", ext.Select(x => $"'{x}'")) + ")";
            string sql = $"SELECT TOP 40 System.ItemPathDisplay, System.DateModified, System.Size FROM SystemIndex WHERE SCOPE='file:' AND {where} ORDER BY System.DateModified DESC";
            dynamic rs = conn.Execute(sql);
            while (!(bool)rs.EOF)
            {
                string path = Convert.ToString(rs.Fields.Item(0).Value) ?? "";
                if (path.Length > 0 && !path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
                {
                    object mod = rs.Fields.Item(1).Value;
                    object size = rs.Fields.Item(2).Value;
                    result.Add(new Found(path, Directory.Exists(path),
                        mod is DateTime d ? d : DateTime.MinValue,
                        size is null or DBNull ? 0 : Convert.ToInt64(size)));
                }
                rs.MoveNext();
            }
            rs.Close();
        }
        catch (Exception ex)
        {
            App.LogVoice("  поиск Windows недоступен: " + ex.Message);
        }
        finally
        {
            try
            {
                conn?.Close();
            }
            catch
            {
                // уже закрыто
            }
        }
        return result;
    }

    private static string Sql(string s) => s.Replace("'", "''").Replace("%", "").Replace("[", "").Replace("_", "[_]");

    private static readonly string[] SkipDirs =
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "AppData", "node_modules", ".git",
        "WinSxS", "Recovery", "PerfLogs", "$WinREAgent", "Config.Msi",
    };

    /// <summary>Свой обход: папки пользователя и корни других дисков, не дольше 8 секунд.</summary>
    private static List<Found> Scan(List<string> words, string[] ext, CancellationToken token)
    {
        var result = new List<Found>();
        List<string> folded = words.Select(AppIndex.Fold).Where(w => w.Length > 0).ToList();
        if (folded.Count == 0) return result;
        var watch = Stopwatch.StartNew();
        var roots = new List<string>();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots.Add(profile);
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady && !profile.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                    roots.Add(d.RootDirectory.FullName);
            }
            catch
            {
                // диск недоступен
            }
        }

        var queue = new Queue<string>(roots);
        while (queue.Count > 0 && watch.Elapsed < TimeSpan.FromSeconds(8) && result.Count < 40 && !token.IsCancellationRequested)
        {
            string dir = queue.Dequeue();
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith('.') || SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    try
                    {
                        if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    }
                    catch
                    {
                        continue;
                    }
                    queue.Enqueue(sub);
                    if (ext.Length == 0 && Matches(name, folded))
                        result.Add(new Found(sub, true, Directory.GetLastWriteTime(sub), 0));
                }
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    if (ext.Length > 0 && !ext.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
                    if (!Matches(Path.GetFileNameWithoutExtension(file), folded)) continue;
                    var fi = new FileInfo(file);
                    result.Add(new Found(file, false, fi.LastWriteTime, fi.Length));
                }
            }
            catch
            {
                // нет доступа к папке
            }
        }
        return result;
    }

    private static bool Matches(string name, List<string> folded)
    {
        string f = AppIndex.Fold(name);
        return folded.All(w => f.Contains(w, StringComparison.Ordinal));
    }

    // ---------- файлы программы ----------

    /// <summary>Папки программы: установка, настройки и сохранения в AppData, ProgramData, Документах, Saved Games, библиотеках Steam.</summary>
    public static async Task<List<Found>> ProgramFoldersAsync(string program)
    {
        var names = new List<string> { program };
        AppEntry? entry = AppIndex.FindConfident(program);
        if (entry != null) names.Add(entry.Name);
        names.AddRange(AssistantService.NameVariants(program));
        List<string> keys = names.Select(AppIndex.Fold).Where(k => k.Length >= 3).Distinct().ToList();
        if (keys.Count == 0) return Last = new List<Found>();

        List<Found> found = await Task.Run(() =>
        {
            var list = new List<Found>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Check(string dir)
            {
                string name = AppIndex.Fold(Path.GetFileName(dir.TrimEnd('\\')));
                if (name.Length < 3 || !keys.Any(k => name.Contains(k) || (k.Length >= 5 && k.Contains(name) && name.Length >= 5))) return;
                if (seen.Add(dir)) list.Add(new Found(dir, true, Directory.GetLastWriteTime(dir), 0));
            }
            void ScanLevel(string? root, int depth)
            {
                if (root == null || !Directory.Exists(root)) return;
                try
                {
                    foreach (string sub in Directory.EnumerateDirectories(root))
                    {
                        Check(sub);
                        if (depth > 1) ScanLevel(sub, depth - 1); // «Издатель\Программа»
                    }
                }
                catch
                {
                    // нет доступа
                }
            }

            string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
            ScanLevel(Env(Environment.SpecialFolder.ProgramFiles), 2);
            ScanLevel(Env(Environment.SpecialFolder.ProgramFilesX86), 2);
            ScanLevel(Env(Environment.SpecialFolder.ApplicationData), 2);
            ScanLevel(Env(Environment.SpecialFolder.LocalApplicationData), 2);
            ScanLevel(Path.Combine(Path.GetDirectoryName(Env(Environment.SpecialFolder.LocalApplicationData)) ?? "", "LocalLow"), 2);
            ScanLevel(Env(Environment.SpecialFolder.CommonApplicationData), 2);
            ScanLevel(Env(Environment.SpecialFolder.MyDocuments), 2);
            ScanLevel(Path.Combine(Env(Environment.SpecialFolder.UserProfile), "Saved Games"), 2);
            foreach (string lib in App.Steam?.Libraries ?? (IReadOnlyList<string>)Array.Empty<string>())
                ScanLevel(Path.Combine(lib, "steamapps", "common"), 1);
            foreach (string dir in InstallLocations(keys)) if (Directory.Exists(dir) && seen.Add(dir)) list.Add(new Found(dir, true, Directory.GetLastWriteTime(dir), 0));

            // размер — не дольше 4 секунд на всё
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < list.Count; i++)
                if (watch.Elapsed < TimeSpan.FromSeconds(4)) list[i] = list[i] with { Size = FolderSize(list[i].Path, watch) };
            return list;
        });
        return Last = found.OrderByDescending(f => f.Size).ToList();
    }

    /// <summary>Папки установки из «Программ и компонентов».</summary>
    private static IEnumerable<string> InstallLocations(List<string> keys)
    {
        var result = new List<string>();
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root == null) continue;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using RegistryKey? k = root.OpenSubKey(sub);
                    string display = AppIndex.Fold(k?.GetValue("DisplayName") as string ?? "");
                    string loc = (k?.GetValue("InstallLocation") as string ?? "").Trim('"', ' ');
                    if (display.Length >= 3 && loc.Length > 3 && keys.Any(k => display.Contains(k, StringComparison.Ordinal))) result.Add(loc.TrimEnd('\\'));
                }
            }
            catch
            {
                // нет доступа
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static long FolderSize(string dir, Stopwatch watch)
    {
        long sum = 0;
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(4)) break;
                try
                {
                    sum += new FileInfo(f).Length;
                }
                catch
                {
                    // файл исчез
                }
            }
        }
        catch
        {
            // нет доступа
        }
        return sum;
    }

    // ---------- открыть ----------

    /// <summary>Открыть папку, где лежит файл, и выделить его в Проводнике.</summary>
    public static void ShowInFolder(string path)
    {
        if (Directory.Exists(path))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public static void OpenFile(string path) => ShellHelper.Open(path);

    /// <summary>«загрузки», «документы», «рабочий стол»… → путь.</summary>
    public static string? KnownFolder(string name)
    {
        string n = name.ToLowerInvariant().Trim();
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? path = n switch
        {
            _ when Regex.IsMatch(n, @"^(загрузк|скачанн|downloads)") => Path.Combine(profile, "Downloads"),
            _ when Regex.IsMatch(n, @"^(документ|мои документы|documents)") => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            _ when Regex.IsMatch(n, @"^(рабоч\w* стол|desktop)") => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            _ when Regex.IsMatch(n, @"^(изображени|картинк|фото|pictures)") => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            _ when Regex.IsMatch(n, @"^(видео|videos)") => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            _ when Regex.IsMatch(n, @"^(музык|music)") => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            _ when Regex.IsMatch(n, @"^(скриншот|снимки экрана|screenshots)") => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"),
            _ when Regex.IsMatch(n, @"^(appdata|апдата|аппдата)") => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            _ when Regex.IsMatch(n, @"^(корзин)") => "shell:RecycleBinFolder",
            _ when Regex.IsMatch(n, @"^(этот компьютер|мой компьютер|компьютер)") => "shell:MyComputerFolder",
            _ => null,
        };
        if (path == null)
        {
            Match m = Regex.Match(n, @"^(?:диск\s+)?([a-zа-я])(?:\s*диск)?$");
            if (m.Success)
            {
                char c = m.Groups[1].Value[0] switch { 'с' => 'C', 'д' => 'D', 'е' => 'E', 'ф' => 'F', 'г' => 'G', var x => char.ToUpperInvariant(x) };
                if (c is >= 'A' and <= 'Z') path = c + @":\";
            }
        }
        return path;
    }

    public static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    // ---------- текст для ответа ----------

    public static string Describe(List<Found> list, int max = 5)
    {
        var lines = new List<string>();
        for (int i = 0; i < Math.Min(max, list.Count); i++)
        {
            Found f = list[i];
            string name = Path.GetFileName(f.Path.TrimEnd('\\'));
            string where = Path.GetDirectoryName(f.Path.TrimEnd('\\')) ?? "";
            string size = f.Size > 0 ? ", " + ProcessCleanup.Size(f.Size) : "";
            lines.Add($"{i + 1}) {name}{size} — {where}");
        }
        return string.Join("\n", lines);
    }
}

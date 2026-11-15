using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace WikarIsland.Services;

public sealed class DownloadItem
{
    public string Key { get; init; } = "";
    public string Source { get; init; } = "";
    public string Name { get; set; } = "";
    public long Done { get; set; }
    public long Total { get; set; } // 0 — размер неизвестен
    public double Speed { get; set; } // байт в секунду
    public ImageSource? Icon { get; set; }
    public Color Accent { get; init; } = Color.FromRgb(0x9C, 0xE3, 0xB5);
    public double? Progress => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>
/// Все загрузки в одном месте, без расширений и входа в аккаунты:
/// • Steam — точный прогресс из appmanifest (SteamService);
/// • браузеры — недокачанные файлы (.crdownload, .part …) в папках загрузок, а для Chrome, Edge, Яндекса,
///   Opera, Brave и Vivaldi ещё и полный размер из их базы History;
/// • лаунчеры и торренты (Epic, Battle.net, EA, Ubisoft, GOG, Riot, Xbox, qBittorrent …) — по скорости записи на диск.
/// </summary>
public sealed class DownloadService
{
    private static readonly Dictionary<string, string> PartialExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".crdownload"] = "Браузер",
        [".part"] = "Firefox",
        [".partial"] = "Браузер",
        [".opdownload"] = "Opera",
        [".download"] = "Браузер",
        [".!qb"] = "qBittorrent",
        [".!ut"] = "µTorrent",
        [".!bt"] = "BitTorrent",
    };

    /// <summary>Имя процесса → как показать. Для «Agent» проверяем путь: это загрузчик Battle.net.</summary>
    private static readonly Dictionary<string, string> Launchers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EpicGamesLauncher"] = "Epic Games",
        ["Battle.net"] = "Battle.net",
        ["Agent"] = "Battle.net",
        ["EADesktop"] = "EA app",
        ["EABackgroundService"] = "EA app",
        ["upc"] = "Ubisoft Connect",
        ["UbisoftConnect"] = "Ubisoft Connect",
        ["GalaxyClient"] = "GOG Galaxy",
        ["GalaxyClientService"] = "GOG Galaxy",
        ["RiotClientServices"] = "Riot Client",
        ["GamingServices"] = "Xbox",
        ["XboxPcApp"] = "Xbox",
        ["qbittorrent"] = "qBittorrent",
        ["uTorrent"] = "µTorrent",
        ["BitTorrent"] = "BitTorrent",
        ["transmission-qt"] = "Transmission",
        ["tixati"] = "Tixati",
        ["Motrix"] = "Motrix",
        ["IDMan"] = "Internet Download Manager",
        ["fdm"] = "Free Download Manager",
    };

    /// <summary>Браузеры на Chromium: папка профилей (относительно LocalAppData или AppData) → имя.</summary>
    private static readonly (string root, bool roaming, string name, string exe)[] Chromium =
    {
        (@"Google\Chrome\User Data", false, "Chrome", "chrome"),
        (@"Microsoft\Edge\User Data", false, "Edge", "msedge"),
        (@"Yandex\YandexBrowser\User Data", false, "Яндекс Браузер", "browser"),
        (@"BraveSoftware\Brave-Browser\User Data", false, "Brave", "brave"),
        (@"Vivaldi\User Data", false, "Vivaldi", "vivaldi"),
        (@"Opera Software\Opera Stable", true, "Opera", "opera"),
        (@"Opera Software\Opera GX Stable", true, "Opera GX", "opera"),
    };

    private const double LauncherThreshold = 1.5 * 1024 * 1024; // запись на диск от 1,5 МБ/с — значит, качает

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<string, DownloadItem> _items = new();
    private readonly Dictionary<string, (long bytes, DateTime at)> _lastBytes = new();
    private readonly Dictionary<string, int> _launcherHot = new();
    private readonly Dictionary<string, ImageSource?> _icons = new();
    private Dictionary<string, (string target, long total, string browser, string exe)> _chromium = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _chromiumReadAt;
    private List<string> _folders = new();
    private DateTime _foldersReadAt;

    public List<DownloadItem> Items { get; private set; } = new();

    public event Action? Changed;

    /// <summary>Файл докачался: имя, полный путь, значок источника.</summary>
    public event Action<string, string, ImageSource?>? FileFinished;

    public void Start()
    {
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        try
        {
            var now = DateTime.Now;
            var seen = new HashSet<string>();
            var list = new List<DownloadItem>();

            AddSteam(list, seen, now);
            AddPartialFiles(list, seen, now);
            AddLaunchers(list, seen, now);

            // пропавшие недокачанные файлы: докачался — карточка, отменили — молча
            foreach (string key in _items.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                DownloadItem old = _items[key];
                _items.Remove(key);
                _lastBytes.Remove(key);
                if (key.StartsWith("file:", StringComparison.Ordinal))
                {
                    string final = FinalPath(key[5..]);
                    if (File.Exists(final) && new FileInfo(final).Length > 0)
                        FileFinished?.Invoke(Path.GetFileName(final), final, old.Icon);
                }
            }

            Items = list;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private DownloadItem Track(string key, Func<DownloadItem> create, long bytes, DateTime now)
    {
        if (!_items.TryGetValue(key, out DownloadItem? item))
        {
            item = create();
            _items[key] = item;
        }

        if (_lastBytes.TryGetValue(key, out var last) && bytes >= last.bytes)
        {
            double seconds = (now - last.at).TotalSeconds;
            if (seconds > 0.2)
            {
                double speed = (bytes - last.bytes) / seconds;
                item.Speed = item.Speed <= 0 ? speed : item.Speed * 0.6 + speed * 0.4; // сглаживаем скачки
            }
        }
        _lastBytes[key] = (bytes, now);
        return item;
    }

    // ---------- Steam ----------

    private void AddSteam(List<DownloadItem> list, HashSet<string> seen, DateTime now)
    {
        if (App.Steam == null) return;
        foreach (SteamDownload d in App.Steam.Downloads)
        {
            string key = $"steam:{d.AppId}";
            seen.Add(key);
            DownloadItem item = Track(key, () => new DownloadItem
            {
                Key = key,
                Source = "Steam",
                Accent = Color.FromRgb(0x66, 0xC0, 0xF4),
            }, d.Done, now);
            item.Name = d.Name;
            item.Done = d.Done;
            item.Total = d.Total;
            item.Icon = App.Steam.SteamIcon;
            list.Add(item);
        }
    }

    // ---------- браузеры и прочие недокачанные файлы ----------

    private void AddPartialFiles(List<DownloadItem> list, HashSet<string> seen, DateTime now)
    {
        if ((now - _foldersReadAt).TotalSeconds > 60) ReadFolders(now);

        var partials = new List<FileInfo>();
        foreach (string folder in _folders)
        {
            try
            {
                foreach (string path in Directory.EnumerateFiles(folder))
                    if (PartialExtensions.ContainsKey(Path.GetExtension(path))) partials.Add(new FileInfo(path));
            }
            catch
            {
                // папку удалили или нет доступа
            }
        }

        // загрузки Chromium, которые лежат не в известных папках («Сохранить как…» в другое место)
        foreach (string path in _chromium.Keys)
            if (!partials.Any(p => p.FullName.Equals(path, StringComparison.OrdinalIgnoreCase)) && File.Exists(path))
                partials.Add(new FileInfo(path));

        bool unknownChromium = partials.Any(p => p.Extension.Equals(".crdownload", StringComparison.OrdinalIgnoreCase)
                                                 && !_chromium.ContainsKey(p.FullName));
        if (partials.Count > 0 && (now - _chromiumReadAt).TotalSeconds > (unknownChromium ? 3 : 10)) ReadChromium(now);

        foreach (FileInfo file in partials)
        {
            file.Refresh();
            if (!file.Exists) continue;
            string key = "file:" + file.FullName;
            seen.Add(key);

            _chromium.TryGetValue(file.FullName, out var meta);
            string source = meta.browser ?? PartialExtensions[file.Extension];
            DownloadItem item = Track(key, () => new DownloadItem
            {
                Key = key,
                Source = source,
                Accent = Color.FromRgb(0x8A, 0xB4, 0xF8),
            }, file.Length, now);

            item.Done = file.Length;
            item.Total = meta.total > 0 ? meta.total : 0;
            item.Name = !string.IsNullOrEmpty(meta.target) ? Path.GetFileName(meta.target) : PrettyPartialName(file.Name);
            item.Icon ??= IconOf(meta.exe ?? ExeForExtension(file.Extension));
            list.Add(item);
        }
    }

    private static string PrettyPartialName(string name)
    {
        string bare = Path.GetFileNameWithoutExtension(name);
        return bare.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("Не подтвержден", StringComparison.OrdinalIgnoreCase)
            ? "Новый файл"
            : bare;
    }

    private static string? ExeForExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".part" => "firefox",
        ".!qb" => "qbittorrent",
        ".!ut" => "uTorrent",
        ".opdownload" => "opera",
        _ => null,
    };

    /// <summary>Куда ляжет готовый файл: для Chromium — из базы, иначе просто без «.crdownload/.part».</summary>
    private string FinalPath(string partial)
    {
        if (_chromium.TryGetValue(partial, out var meta) && !string.IsNullOrEmpty(meta.target)) return meta.target;
        return Path.Combine(Path.GetDirectoryName(partial) ?? "", Path.GetFileNameWithoutExtension(partial));
    }

    /// <summary>Папки загрузок: системная «Загрузки», рабочий стол и папки из настроек браузеров.</summary>
    private void ReadFolders(DateTime now)
    {
        _foldersReadAt = now;
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) folders.Add(Path.GetFullPath(path));
        }

        Add(KnownDownloads());
        Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        foreach (string profile in ChromiumProfiles().Select(p => p.dir))
        {
            try
            {
                string prefs = Path.Combine(profile, "Preferences");
                if (!File.Exists(prefs)) continue;
                using var doc = JsonDocument.Parse(ReadShared(prefs));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("download", out JsonElement dl) && dl.TryGetProperty("default_directory", out JsonElement d))
                    Add(d.GetString());
                if (root.TryGetProperty("savefile", out JsonElement sf) && sf.TryGetProperty("default_directory", out JsonElement s))
                    Add(s.GetString());
            }
            catch
            {
                // настройки браузера не прочитались — не страшно
            }
        }
        _folders = folders.ToList();
    }

    private static IEnumerable<(string dir, string name, string exe)> ChromiumProfiles()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        foreach (var (root, isRoaming, name, exe) in Chromium)
        {
            string dir = Path.Combine(isRoaming ? roaming : local, root);
            if (!Directory.Exists(dir)) continue;
            if (File.Exists(Path.Combine(dir, "History"))) yield return (dir, name, exe); // Opera: профиль прямо в корне
            string[] subs;
            try
            {
                subs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue;
            }
            foreach (string sub in subs)
            {
                string n = Path.GetFileName(sub);
                if ((n == "Default" || n.StartsWith("Profile ", StringComparison.Ordinal)) && File.Exists(Path.Combine(sub, "History")))
                    yield return (sub, name, exe);
            }
        }
    }

    /// <summary>
    /// Незавершённые загрузки из базы History. Браузер держит файл открытым, поэтому копируем его
    /// с разрешением общего доступа и читаем копию. Строка появляется в базе через несколько секунд после начала.
    /// </summary>
    private void ReadChromium(DateTime now)
    {
        _chromiumReadAt = now;
        var result = new Dictionary<string, (string, long, string, string)>(StringComparer.OrdinalIgnoreCase);
        string temp = Path.Combine(Path.GetTempPath(), "WikarIsland");
        Directory.CreateDirectory(temp);

        foreach (var (dir, name, exe) in ChromiumProfiles())
        {
            string copy = Path.Combine(temp, "history.db");
            try
            {
                string source = Path.Combine(dir, "History");
                if (new FileInfo(source).Length > 300L * 1024 * 1024) continue; // гигантская история — не мучаем диск
                CopyShared(source, copy);
                // в режиме WAL свежие строки лежат в History-wal — берём и его
                if (File.Exists(source + "-wal")) CopyShared(source + "-wal", copy + "-wal");

                using var db = new SqliteConnection($"Data Source={copy};Pooling=False");
                db.Open();
                using SqliteCommand cmd = db.CreateCommand();
                cmd.CommandText = "SELECT current_path, target_path, total_bytes FROM downloads WHERE state = 0";
                using SqliteDataReader r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string current = r.IsDBNull(0) ? "" : r.GetString(0);
                    if (current.Length == 0) continue;
                    string target = r.IsDBNull(1) ? "" : r.GetString(1);
                    long total = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                    result[current] = (target, total, name, exe);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
            finally
            {
                try
                {
                    foreach (string f in new[] { copy, copy + "-wal", copy + "-shm", copy + "-journal" })
                        if (File.Exists(f)) File.Delete(f);
                }
                catch
                {
                    // занята — удалим в следующий раз
                }
            }
        }
        _chromium = result;
    }

    private static void CopyShared(string source, string dest)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static string ReadShared(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(input, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ---------- лаунчеры и торренты ----------

    private void AddLaunchers(List<DownloadItem> list, HashSet<string> seen, DateTime now)
    {
        // сумма записи на диск по всем процессам одного лаунчера
        var written = new Dictionary<string, (long bytes, string? path)>();
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                string pname;
                try
                {
                    pname = p.ProcessName;
                }
                catch
                {
                    continue; // процесс уже закрылся
                }
                if (!Launchers.TryGetValue(pname, out string? display)) continue;
                string? path = ImagePath(p.Id);
                if (pname.Equals("Agent", StringComparison.OrdinalIgnoreCase)
                    && path?.Contains("Battle.net", StringComparison.OrdinalIgnoreCase) != true) continue;
                long bytes = WriteBytes(p.Id);
                if (bytes < 0) continue;
                written.TryGetValue(display, out var acc);
                written[display] = (acc.bytes + bytes, acc.path ?? path);
            }
        }

        foreach (var (display, (bytes, path)) in written)
        {
            string key = "app:" + display;
            string rateKey = "rate:" + display;
            double rate = 0;
            if (_lastBytes.TryGetValue(rateKey, out var last) && bytes >= last.bytes)
            {
                double seconds = (now - last.at).TotalSeconds;
                if (seconds > 0.2) rate = (bytes - last.bytes) / seconds;
            }
            _lastBytes[rateKey] = (bytes, now);

            // два замера подряд выше порога — начинаем; пять подряд ниже — заканчиваем
            int hot = _launcherHot.GetValueOrDefault(display);
            hot = rate >= LauncherThreshold ? Math.Max(1, hot + 1) : Math.Min(-1, hot - 1);
            if (hot > 10) hot = 10;
            if (hot < -10) hot = -10;
            bool wasShown = _items.ContainsKey(key);
            _launcherHot[display] = hot;
            if (!(hot >= 2 || (wasShown && hot > -5))) continue;

            seen.Add(key);
            if (!_items.TryGetValue(key, out DownloadItem? item))
            {
                item = new DownloadItem { Key = key, Source = display, Name = display };
                _items[key] = item;
            }
            item.Speed = item.Speed <= 0 ? rate : item.Speed * 0.6 + rate * 0.4;
            item.Icon ??= path != null ? IconFromFile(path) : null;
            list.Add(item);
        }
    }

    // ---------- значки ----------

    private ImageSource? IconOf(string? processName)
    {
        if (processName == null) return null;
        if (_icons.TryGetValue(processName, out ImageSource? cached)) return cached;
        ImageSource? icon = null;
        foreach (Process p in Process.GetProcessesByName(processName))
        {
            using (p)
            {
                if (icon != null) continue;
                string? path = ImagePath(p.Id);
                if (path != null) icon = IconFromFile(path);
            }
        }
        if (icon != null) _icons[processName] = icon; // не нашли — попробуем позже, когда браузер запущен
        return icon;
    }

    private ImageSource? IconFromFile(string path)
    {
        if (_icons.TryGetValue(path, out ImageSource? cached)) return cached;
        ImageSource? result = null;
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon != null)
            {
                BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                result = src;
            }
        }
        catch
        {
            // без значка
        }
        _icons[path] = result;
        return result;
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IO_COUNTERS counters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private static long WriteBytes(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return -1;
        try
        {
            return GetProcessIoCounters(h, out IO_COUNTERS c) ? (long)c.WriteTransferCount : -1;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string? ImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
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

    private static string? KnownDownloads()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr ptr) != 0) return null;
        try
        {
            return Marshal.PtrToStringUni(ptr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    public static string FormatSpeed(double bytesPerSecond) =>
        bytesPerSecond >= 1024 * 1024 * 1024 ? $"{bytesPerSecond / (1024 * 1024 * 1024):0.0} ГБ/с"
        : bytesPerSecond >= 1024 * 1024 ? $"{bytesPerSecond / (1024 * 1024):0.#} МБ/с"
        : $"{bytesPerSecond / 1024:0} КБ/с";

    public static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} ГБ"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0} МБ"
        : $"{bytes / 1024.0:0} КБ";
}

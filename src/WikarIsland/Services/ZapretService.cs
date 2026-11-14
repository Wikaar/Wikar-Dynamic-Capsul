using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// zapret (Flowseal/zapret-discord-youtube, MIT): обход блокировок Discord и YouTube через DPI без VPN.
/// Остров сам скачивает свежий релиз с GitHub в %LocalAppData%\WikarIsland\zapret,
/// разбирает стратегии general*.bat и запускает winws.exe напрямую — без окна консоли.
/// Драйверу WinDivert нужны права администратора.
/// </summary>
public sealed class ZapretService
{
    private const string Repo = "Flowseal/zapret-discord-youtube";
    private static readonly HttpClient Http = CreateClient();

    private Process? _winws;

    /// <summary>Общая папка: её и стоит добавить в исключения антивируса.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WikarIsland", "zapret");

    /// <summary>Папка текущей версии внутри Root.</summary>
    public static string Folder => App.Settings.ZapretDir is { Length: > 0 } dir && Directory.Exists(dir) ? dir : Path.Combine(Root, "current");

    private static string Bin => Path.Combine(Folder, "bin");
    private static string Lists => Path.Combine(Folder, "lists");

    public bool Installed => File.Exists(Path.Combine(Bin, "winws.exe"));
    public bool DriverPresent => File.Exists(Path.Combine(Bin, "WinDivert64.sys"));
    public bool IsRunning => _winws is { HasExited: false };
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "";

    /// <summary>winws.exe запущен кем-то другим (например, службой zapret из service.bat).</summary>
    public bool ExternalRunning => !IsRunning && Process.GetProcessesByName("winws").Length > 0;

    public event Action? StateChanged;

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WikarIsland");
        return http;
    }

    private void SetStatus(string text, bool busy)
    {
        Status = text;
        IsBusy = busy;
        StateChanged?.Invoke();
    }

    // ---------- установка и обновление ----------

    /// <summary>Скачать последний релиз с GitHub и распаковать (старую версию заменяем, свои списки сохраняем).</summary>
    public async Task<bool> InstallOrUpdateAsync()
    {
        if (IsBusy) return false;
        bool wasRunning = IsRunning;
        Stop();
        try
        {
            SetStatus("Ищу последнюю версию…", true);
            string json = await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";

            string? url = root.GetProperty("assets").EnumerateArray()
                .Select(a => a.GetProperty("browser_download_url").GetString())
                .FirstOrDefault(u => u != null && u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            url ??= root.GetProperty("zipball_url").GetString(); // запасной вариант — исходники релиза (в них есть bin)

            SetStatus($"Скачиваю zapret {tag}…", true);
            byte[] zip = await Http.GetByteArrayAsync(url);

            SetStatus("Распаковываю…", true);
            // Драйвер WinDivert после прошлого запуска держит свой .sys, поэтому старую папку не перезаписываем:
            // каждая версия распаковывается в новую папку, а старые удаляем, когда получится.
            ReleaseDriver();
            string target = Path.Combine(Root, $"{SafeName(tag)}-{DateTime.Now:yyyyMMddHHmmss}");
            string oldFolder = Folder;
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Root);
                string temp = Path.Combine(Root, "_download");
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                using (var ms = new MemoryStream(zip))
                using (var archive = new ZipArchive(ms))
                    archive.ExtractToDirectory(temp);

                // в архиве может быть одна папка верхнего уровня
                string source = temp;
                string[] dirs = Directory.GetDirectories(temp);
                if (Directory.GetFiles(temp).Length == 0 && dirs.Length == 1) source = dirs[0];
                if (!File.Exists(Path.Combine(source, "bin", "winws.exe")))
                    throw new InvalidOperationException("В архиве нет bin\\winws.exe");

                Directory.Move(source, target);
                try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }

                // переносим пользовательские списки из прошлой версии
                string oldLists = Path.Combine(oldFolder, "lists");
                if (Directory.Exists(oldLists))
                    foreach (string f in Directory.GetFiles(oldLists, "*-user.txt"))
                        File.Copy(f, Path.Combine(target, "lists", Path.GetFileName(f)), overwrite: true);
            });

            App.Settings.ZapretDir = target;
            CleanupOldVersions();

            App.Settings.ZapretVersion = tag;
            if (App.Settings.ZapretStrategy == null || !Strategies().Contains(App.Settings.ZapretStrategy))
                App.Settings.ZapretStrategy = "general";
            App.Settings.Save();

            if (!DriverPresent)
            {
                SetStatus($"Антивирус удалил WinDivert. Добавь в исключения папку {Root} и нажми «Обновить»", false);
                return false;
            }

            SetStatus($"Установлен zapret {tag}", false);
            // служба указывает на старую папку — переустанавливаем её на новую версию
            if (ZapretTools.InstalledServiceStrategy() is { } svcStrategy && SystemMonitorService.IsAdmin)
                ZapretTools.InstallService(svcStrategy);
            else if (wasRunning) Start();
            return true;
        }
        catch (UnauthorizedAccessException ex) when (ex.Message.Contains("WinDivert", StringComparison.OrdinalIgnoreCase))
        {
            App.LogError(ex);
            SetStatus($"Антивирус не даёт записать WinDivert. Добавь в исключения папку {Root} и нажми ещё раз", false);
            return false;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetStatus("Не удалось скачать: " + ex.Message, false);
            return false;
        }
    }

    private static string SafeName(string tag) =>
        string.Concat(tag.Select(c => char.IsLetterOrDigit(c) || c == '.' ? c : '_'));

    /// <summary>Выгрузить драйвер WinDivert (если остался после прошлого запуска), чтобы освободить файлы.</summary>
    private static void ReleaseDriver()
    {
        if (!SystemMonitorService.IsAdmin) return;
        foreach (string cmd in new[] { "stop WinDivert", "delete WinDivert", "stop WinDivert14", "delete WinDivert14" })
        {
            try
            {
                using Process? p = Process.Start(new ProcessStartInfo("sc.exe", cmd) { CreateNoWindow = true, UseShellExecute = false });
                p?.WaitForExit(5000);
            }
            catch
            {
                // службы нет — и хорошо
            }
        }
    }

    /// <summary>Удаляем старые версии; занятые драйвером пропускаем до следующего раза.</summary>
    private static void CleanupOldVersions()
    {
        if (!Directory.Exists(Root)) return;
        string current = App.Settings.ZapretDir ?? "";
        foreach (string dir in Directory.GetDirectories(Root))
        {
            if (string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, true); } catch { }
        }
        // файлы самой первой версии (раньше распаковывались прямо в Root)
        foreach (string file in Directory.GetFiles(Root))
            try { File.Delete(file); } catch { }
    }

    // ---------- стратегии ----------

    /// <summary>Имена стратегий: «general», «general (ALT)», «general (FAKE TLS AUTO)» …</summary>
    public List<string> Strategies()
    {
        if (!Directory.Exists(Folder)) return new List<string>();
        return Directory.GetFiles(Folder, "general*.bat")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null)
            .Select(n => n!)
            .OrderBy(n => n == "general" ? "" : n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string ShortName(string strategy)
    {
        Match m = Regex.Match(strategy, @"\((.+)\)");
        return m.Success ? m.Groups[1].Value : "Основная";
    }

    /// <summary>Достаём из .bat строку запуска winws.exe и подставляем переменные.</summary>
    public static string BuildArgsFor(string strategy) => BuildArgs(strategy);

    private static string BuildArgs(string strategy)
    {
        string bat = File.ReadAllText(Path.Combine(Folder, strategy + ".bat"), Encoding.UTF8);
        // склеиваем строки, продолженные «^»
        string joined = Regex.Replace(bat, @"\^\s*\r?\n", " ");
        string? line = joined.Split('\n').FirstOrDefault(l => l.Contains("winws.exe", StringComparison.OrdinalIgnoreCase)
                                                             && l.TrimStart().StartsWith("start", StringComparison.OrdinalIgnoreCase));
        if (line == null) throw new InvalidOperationException($"Не нашёл запуск winws.exe в «{strategy}.bat»");

        int exe = line.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
        string args = exe >= 0 ? line[(exe + "winws.exe\"".Length)..] : line[(line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) + 9)..];

        // «12» — так service.bat выключает игровой фильтр
        (string tcp, string udp, string any) = ZapretTools.GameFilterValues();
        return args
            .Replace("%BIN%", Bin + "\\")
            .Replace("%LISTS%", Lists + "\\")
            .Replace("%GameFilterTCP%", tcp)
            .Replace("%GameFilterUDP%", udp)
            .Replace("%GameFilter%", any)
            .Trim();
    }

    /// <summary>То же, что делает service.bat load_user_lists: пустых пользовательских списков быть не должно.</summary>
    private static void EnsureUserLists()
    {
        Directory.CreateDirectory(Lists);
        void Ensure(string name, string content)
        {
            string path = Path.Combine(Lists, name);
            if (!File.Exists(path)) File.WriteAllText(path, content);
        }
        Ensure("ipset-exclude-user.txt", "203.0.113.113/32\r\n");
        Ensure("list-general-user.txt", "# Never leave this file empty\r\ndomain.example.abc\r\n");
        Ensure("list-exclude-user.txt", "domain.example.abc\r\n");
    }

    // ---------- запуск ----------

    public async Task ToggleAsync()
    {
        if (IsBusy) return;
        if (IsRunning)
        {
            Stop();
            return;
        }
        if (!Installed && !await InstallOrUpdateAsync()) return;
        Start();
    }

    public bool Start(string? strategy = null)
    {
        strategy ??= App.Settings.ZapretStrategy ?? "general";
        if (!SystemMonitorService.IsAdmin)
        {
            SetStatus("Нужны права администратора: включи автозапуск от администратора или перезапусти остров", false);
            return false;
        }
        if (!DriverPresent)
        {
            SetStatus("Нет WinDivert64.sys — антивирус удалил его. Добавь папку в исключения и нажми «Обновить»", false);
            return false;
        }
        if (ExternalRunning)
        {
            SetStatus("zapret уже запущен отдельно (служба или .bat) — сначала останови его", false);
            return false;
        }

        try
        {
            EnsureUserLists();
            ZapretTools.EnsureTcpTimestamps(); // как service.bat перед каждым запуском
            string args = BuildArgs(strategy);
            _winws = Process.Start(new ProcessStartInfo(Path.Combine(Bin, "winws.exe"), args)
            {
                WorkingDirectory = Bin,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            SetStatus($"Работает · {ShortName(strategy)}", false);
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetStatus("Не запустился: " + ex.Message, false);
            return false;
        }
    }

    /// <summary>Перенос старой галочки «обход для игр» в новый режим.</summary>
    public static void MigrateSettings()
    {
        if (App.Settings.ZapretGameFilter && App.Settings.ZapretGameMode == "disabled")
        {
            App.Settings.ZapretGameMode = "all";
            App.Settings.ZapretGameFilter = false;
            App.Settings.Save();
        }
    }

    public void Stop()
    {
        try
        {
            if (_winws is { HasExited: false })
            {
                _winws.Kill();
                _winws.WaitForExit(3000);
            }
        }
        catch
        {
            // уже завершился
        }
        _winws = null;
        if (!IsBusy) SetStatus(Installed ? "Выключен" : "", false);
    }

    // ---------- подбор стратегии ----------

    private static async Task<bool> ReachableAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
        string[] targets = { "https://discord.com/", "https://www.youtube.com/", "https://i.ytimg.com/generate_204" };
        try
        {
            foreach (string url in targets)
            {
                using HttpResponseMessage r = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                if ((int)r.StatusCode >= 500) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Перебирает стратегии и оставляет первую, с которой открываются Discord и YouTube.</summary>
    public async Task<string?> FindWorkingStrategyAsync()
    {
        if (IsBusy) return null;
        if (!Installed && !await InstallOrUpdateAsync()) return null;
        if (!SystemMonitorService.IsAdmin)
        {
            SetStatus("Для подбора нужны права администратора", false);
            return null;
        }

        Stop();
        List<string> all = Strategies();
        for (int i = 0; i < all.Count; i++)
        {
            string s = all[i];
            SetStatus($"Проверяю {i + 1}/{all.Count}: {ShortName(s)}…", true);
            IsBusy = false; // Start проверяет только права/драйвер
            if (!Start(s)) return null;
            IsBusy = true;
            await Task.Delay(2500);
            bool ok = await ReachableAsync();
            if (ok)
            {
                App.Settings.ZapretStrategy = s;
                App.Settings.Save();
                SetStatus($"Работает · {ShortName(s)} (подобрана)", false);
                return s;
            }
            IsBusy = false;
            Stop();
        }
        SetStatus("Ни одна стратегия не открыла Discord и YouTube", false);
        return null;
    }
}

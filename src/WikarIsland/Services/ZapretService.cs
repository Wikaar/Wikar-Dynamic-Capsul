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

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WikarIsland", "zapret");

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
            string temp = Path.Combine(Path.GetTempPath(), "wikar-zapret-" + Guid.NewGuid().ToString("N"));
            await Task.Run(() =>
            {
                using (var ms = new MemoryStream(zip))
                using (var archive = new ZipArchive(ms))
                    archive.ExtractToDirectory(temp);

                // в архиве может быть одна папка верхнего уровня
                string source = temp;
                string[] dirs = Directory.GetDirectories(temp);
                if (Directory.GetFiles(temp).Length == 0 && dirs.Length == 1) source = dirs[0];
                if (!File.Exists(Path.Combine(source, "bin", "winws.exe")))
                    throw new InvalidOperationException("В архиве нет bin\\winws.exe");

                // сохраняем пользовательские списки
                var keep = new Dictionary<string, byte[]>();
                if (Directory.Exists(Lists))
                    foreach (string f in Directory.GetFiles(Lists, "*-user.txt"))
                        keep[Path.GetFileName(f)] = File.ReadAllBytes(f);

                if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
                Directory.CreateDirectory(Path.GetDirectoryName(Folder)!);
                Directory.Move(source, Folder);

                foreach (KeyValuePair<string, byte[]> f in keep)
                    File.WriteAllBytes(Path.Combine(Lists, f.Key), f.Value);
                try { Directory.Delete(temp, true); } catch { }
            });

            App.Settings.ZapretVersion = tag;
            if (App.Settings.ZapretStrategy == null || !Strategies().Contains(App.Settings.ZapretStrategy))
                App.Settings.ZapretStrategy = "general";
            App.Settings.Save();

            if (!DriverPresent)
            {
                SetStatus("Антивирус удалил WinDivert. Добавь папку zapret в исключения и обнови ещё раз", false);
                return false;
            }

            SetStatus($"Установлен zapret {tag}", false);
            if (wasRunning) Start();
            return true;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetStatus("Не удалось скачать: " + ex.Message, false);
            return false;
        }
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
    private static string BuildArgs(string strategy, bool gameFilter)
    {
        string bat = File.ReadAllText(Path.Combine(Folder, strategy + ".bat"), Encoding.UTF8);
        // склеиваем строки, продолженные «^»
        string joined = Regex.Replace(bat, @"\^\s*\r?\n", " ");
        string? line = joined.Split('\n').FirstOrDefault(l => l.Contains("winws.exe", StringComparison.OrdinalIgnoreCase)
                                                             && l.TrimStart().StartsWith("start", StringComparison.OrdinalIgnoreCase));
        if (line == null) throw new InvalidOperationException($"Не нашёл запуск winws.exe в «{strategy}.bat»");

        int exe = line.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase);
        string args = exe >= 0 ? line[(exe + "winws.exe\"".Length)..] : line[(line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase) + 9)..];

        string games = gameFilter ? "1024-65535" : "12"; // «12» — так service.bat выключает игровой фильтр
        return args
            .Replace("%BIN%", Bin + "\\")
            .Replace("%LISTS%", Lists + "\\")
            .Replace("%GameFilterTCP%", games)
            .Replace("%GameFilterUDP%", games)
            .Replace("%GameFilter%", games)
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
            string args = BuildArgs(strategy, App.Settings.ZapretGameFilter);
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

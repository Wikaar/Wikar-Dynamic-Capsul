using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WikarIsland.Services;

public enum DiagLevel { Ok, Warning, Error }

/// <summary>Результат одной проверки диагностики; Fix — что можно сделать в один клик.</summary>
public sealed record DiagItem(DiagLevel Level, string Text, string? FixTitle = null, Func<string>? Fix = null);

/// <summary>
/// Возможности service.bat из zapret-discord-youtube, перенесённые в остров:
/// служба Windows, игровой фильтр, IPSet, фейки, hosts, диагностика, кэш Discord, проверка обновлений.
/// Почти всё требует прав администратора.
/// </summary>
public static class ZapretTools
{
    private const string RawBase = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/";
    private const string IpsetPlaceholder = "203.0.113.113/32";
    private const string HostsBegin = "# --- Wikar Island: zapret hosts ---";
    private const string HostsEnd = "# --- Wikar Island: end ---";
    private static readonly string HostsPath = Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WikarIsland");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
        return http;
    }

    private static string Bin => Path.Combine(ZapretService.Folder, "bin");
    private static string Lists => Path.Combine(ZapretService.Folder, "lists");

    // ---------- командная строка ----------

    private static (int code, string output) Run(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (string a in args) info.ArgumentList.Add(a);
            using Process p = Process.Start(info)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>Состояние службы: RUNNING, STOPPED, STOP_PENDING… или null, если её нет.</summary>
    public static string? ServiceState(string name)
    {
        (int code, string output) = Run("sc.exe", "query", name);
        if (code != 0) return null;
        Match m = Regex.Match(output, @"STATE\s*:\s*\d+\s+(\w+)");
        return m.Success ? m.Groups[1].Value : "UNKNOWN";
    }

    private static bool ServiceExists(string name) =>
        Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}") != null;

    private static void StopAndDelete(string name)
    {
        Run("net.exe", "stop", name);
        Run("sc.exe", "delete", name);
    }

    // ---------- TCP timestamps (service.bat делает это перед каждым запуском) ----------

    public static bool TcpTimestampsEnabled()
    {
        (_, string output) = Run("netsh.exe", "interface", "tcp", "show", "global");
        return output.Split('\n').Any(l => l.Contains("timestamps", StringComparison.OrdinalIgnoreCase)
                                           && l.Contains("enabled", StringComparison.OrdinalIgnoreCase));
    }

    public static void EnsureTcpTimestamps()
    {
        if (!TcpTimestampsEnabled()) Run("netsh.exe", "interface", "tcp", "set", "global", "timestamps=enabled");
    }

    // ---------- служба Windows ----------

    public static string? InstalledServiceStrategy()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
        return key?.GetValue("zapret-discord-youtube") as string;
    }

    /// <summary>Установить стратегию как службу «zapret» с автозапуском — работает и без острова.</summary>
    public static string InstallService(string strategy)
    {
        App.Zapret.Stop();
        EnsureTcpTimestamps();
        string args = ZapretService.BuildArgsFor(strategy);
        string cmd = $"\"{Path.Combine(Bin, "winws.exe")}\" {args}";

        StopAndDelete("zapret");
        (int code, string output) = Run("sc.exe", "create", "zapret", "binPath=", cmd, "DisplayName=", "zapret", "start=", "auto");
        if (code != 0) return "Не удалось создать службу: " + output.Trim();
        Run("sc.exe", "description", "zapret", "Zapret DPI bypass software");
        Run("reg.exe", "add", @"HKLM\System\CurrentControlSet\Services\zapret", "/v", "zapret-discord-youtube", "/t", "REG_SZ", "/d", strategy, "/f");
        (code, output) = Run("sc.exe", "start", "zapret");
        return code == 0 ? $"Служба установлена и запущена · {ZapretService.ShortName(strategy)}" : "Служба создана, но не запустилась: " + output.Trim();
    }

    /// <summary>То же, что «Remove Services»: служба zapret, winws.exe, драйвер WinDivert.</summary>
    public static string RemoveServices()
    {
        App.Zapret.Stop();
        StopAndDelete("zapret");
        foreach (Process p in Process.GetProcessesByName("winws"))
            try { p.Kill(); } catch { }
        StopAndDelete("WinDivert");
        StopAndDelete("WinDivert14");
        return "Службы zapret и WinDivert удалены";
    }

    public static List<string> StatusLines()
    {
        var lines = new List<string>();
        string? svc = ServiceState("zapret");
        string? strategy = InstalledServiceStrategy();
        lines.Add(svc == null ? "Служба zapret: не установлена"
            : $"Служба zapret: {StateRu(svc)}" + (strategy != null ? $" · стратегия {ZapretService.ShortName(strategy)}" : ""));
        string? wd = ServiceState("WinDivert");
        lines.Add(wd == null ? "Драйвер WinDivert: не загружен" : $"Драйвер WinDivert: {StateRu(wd)}");
        lines.Add(Process.GetProcessesByName("winws").Length > 0 ? "Обход (winws.exe): работает" : "Обход (winws.exe): не запущен");
        lines.Add(File.Exists(Path.Combine(Bin, "WinDivert64.sys")) ? "Файл WinDivert64.sys: на месте" : "Файл WinDivert64.sys: НЕ найден");
        return lines;
    }

    private static string StateRu(string state) => state switch
    {
        "RUNNING" => "работает",
        "STOPPED" => "остановлена",
        "STOP_PENDING" => "останавливается (возможен конфликт с другим обходом)",
        "START_PENDING" => "запускается",
        _ => state,
    };

    // ---------- игровой фильтр ----------

    /// <summary>Проверка портов как в service.bat: «1024-65535» или «1024-1934,1936-65535».</summary>
    public static string? ValidateRange(string input)
    {
        string s = input.Replace(" ", "");
        if (s.Length == 0) return null;
        foreach (string item in s.Split(','))
        {
            Match m = Regex.Match(item, @"^([1-9]\d{0,4})(?:-([1-9]\d{0,4}))?$");
            if (!m.Success) return null;
            int a = int.Parse(m.Groups[1].Value);
            int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
            if (a > 65535 || b > 65535 || a > b) return null;
        }
        return s;
    }

    /// <summary>Значения переменных %GameFilterTCP% / %GameFilterUDP% / %GameFilter% («12» — выключено).</summary>
    public static (string tcp, string udp, string any) GameFilterValues()
    {
        AppSettings s = App.Settings;
        string mode = s.ZapretGameMode;
        string tcp = mode is "all" or "tcp" ? s.ZapretGameTcp : "12";
        string udp = mode is "all" or "udp" ? s.ZapretGameUdp : "12";
        string any = mode == "udp" ? udp : mode is "all" or "tcp" ? tcp : "12";
        return (tcp, udp, any);
    }

    // ---------- IPSet ----------

    private static string IpsetFile => Path.Combine(Lists, "ipset-all.txt");
    private static string IpsetBackup => IpsetFile + ".backup";

    /// <summary>loaded — проверяется список, none — никакие IP, any — любые IP.</summary>
    public static string IpsetMode()
    {
        if (!File.Exists(IpsetFile)) return "none";
        string text = File.ReadAllText(IpsetFile);
        if (text.Trim().Length == 0) return "any";
        return text.Contains(IpsetPlaceholder) ? "none" : "loaded";
    }

    public static string SetIpsetMode(string mode)
    {
        if (IpsetMode() == "loaded") File.Copy(IpsetFile, IpsetBackup, overwrite: true);
        switch (mode)
        {
            case "none":
                File.WriteAllText(IpsetFile, IpsetPlaceholder + "\r\n");
                break;
            case "any":
                File.WriteAllText(IpsetFile, "");
                break;
            default:
                if (!File.Exists(IpsetBackup)) return "Нет сохранённого списка — нажми «Обновить список IPSet»";
                File.Copy(IpsetBackup, IpsetFile, overwrite: true);
                break;
        }
        return "IPSet: " + mode;
    }

    public static async Task<string> UpdateIpsetAsync()
    {
        string text = await Http.GetStringAsync(RawBase + "ipset-service.txt");
        if (text.Trim().Length == 0) return "Список пустой — не обновлено";
        File.WriteAllText(IpsetFile, text);
        File.Copy(IpsetFile, IpsetBackup, overwrite: true);
        return $"Список IPSet обновлён ({text.Split('\n').Count(l => l.Trim().Length > 0)} записей)";
    }

    // ---------- фейки ----------

    public static List<string> FakeNames() => Directory.Exists(Bin)
        ? Directory.GetFiles(Bin, "*.bin")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != null && !n.StartsWith("ACTIVE_", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .OrderBy(n => n)
            .ToList()
        : new List<string>();

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Какой фейк сейчас стоит в ACTIVE_DISCORD_UDP.bin / ACTIVE_GAME_UDP.bin.</summary>
    public static string? CurrentFake(bool discord)
    {
        string active = Path.Combine(Bin, discord ? "ACTIVE_DISCORD_UDP.bin" : "ACTIVE_GAME_UDP.bin");
        if (!File.Exists(active)) return null;
        string h = Hash(active);
        return FakeNames().FirstOrDefault(n => Hash(Path.Combine(Bin, n + ".bin")) == h);
    }

    public static string ReplaceFake(bool discord, string fake)
    {
        string active = Path.Combine(Bin, discord ? "ACTIVE_DISCORD_UDP.bin" : "ACTIVE_GAME_UDP.bin");
        File.Copy(Path.Combine(Bin, fake + ".bin"), active, overwrite: true);
        return $"{(discord ? "Discord UDP" : "Игровой UDP")}: теперь {fake}. Перезапусти zapret";
    }

    // ---------- hosts ----------

    public static async Task<string> UpdateHostsAsync()
    {
        string block = (await Http.GetStringAsync(RawBase + "hosts")).Replace("\r\n", "\n").Trim();
        string[] lines = block.Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length == 0) return "Не удалось получить hosts из репозитория";

        string hosts = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
        if (hosts.Contains(lines[0]) && hosts.Contains(lines[^1])) return "hosts уже актуален";
        if (!SystemMonitorService.IsAdmin) return "Для записи в hosts нужны права администратора";

        File.Copy(HostsPath, HostsPath + ".wikar-backup", overwrite: true);
        // убираем наш прошлый блок и дописываем свежий
        string cleaned = Regex.Replace(hosts, Regex.Escape(HostsBegin) + ".*?" + Regex.Escape(HostsEnd) + @"\r?\n?", "", RegexOptions.Singleline);
        string updated = cleaned.TrimEnd() + "\r\n\r\n" + HostsBegin + "\r\n" + string.Join("\r\n", lines) + "\r\n" + HostsEnd + "\r\n";
        File.WriteAllText(HostsPath, updated);
        Run("ipconfig.exe", "/flushdns");
        return $"hosts обновлён ({lines.Length} строк), копия — hosts.wikar-backup";
    }

    public static string RemoveHostsBlock()
    {
        if (!File.Exists(HostsPath)) return "Файла hosts нет";
        string hosts = File.ReadAllText(HostsPath);
        string cleaned = Regex.Replace(hosts, Regex.Escape(HostsBegin) + ".*?" + Regex.Escape(HostsEnd) + @"\r?\n?", "", RegexOptions.Singleline);
        if (cleaned == hosts) return "В hosts нет записей острова";
        File.WriteAllText(HostsPath, cleaned.TrimEnd() + "\r\n");
        Run("ipconfig.exe", "/flushdns");
        return "Записи острова убраны из hosts";
    }

    // ---------- кэш Discord ----------

    public static string ClearDiscordCache()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var variants = new[] { ("Discord", "discord"), ("DiscordPTB", "discordptb"), ("DiscordCanary", "discordcanary"), ("DiscordDevelopment", "discorddevelopment") };
        int found = 0, deleted = 0;
        foreach ((string process, string folder) in variants)
        {
            string dir = Path.Combine(appData, folder);
            if (!Directory.Exists(dir)) continue;
            found++;
            foreach (Process p in Process.GetProcessesByName(process))
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            foreach (string cache in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                string path = Path.Combine(dir, cache);
                try
                {
                    if (Directory.Exists(path)) { Directory.Delete(path, true); deleted++; }
                }
                catch
                {
                    // занято — пропускаем
                }
            }
        }
        return found == 0 ? "Discord не найден" : $"Кэш Discord очищен ({deleted} папок). Запусти Discord заново";
    }

    // ---------- обновления ----------

    public static async Task<string?> LatestVersionAsync()
    {
        try { return (await Http.GetStringAsync(RawBase + "version.txt")).Trim(); }
        catch { return null; }
    }

    // ---------- диагностика (как «Run Diagnostics») ----------

    public static List<DiagItem> Diagnose()
    {
        var r = new List<DiagItem>();

        r.Add(ServiceState("BFE") == "RUNNING"
            ? new DiagItem(DiagLevel.Ok, "Служба Base Filtering Engine работает")
            : new DiagItem(DiagLevel.Error, "Base Filtering Engine не запущена — без неё zapret не работает", "Запустить",
                () => Run("sc.exe", "start", "BFE").code == 0 ? "BFE запущена" : "Не удалось запустить BFE"));

        using (RegistryKey? inet = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
        {
            bool proxy = inet?.GetValue("ProxyEnable") is int e && e == 1;
            string server = inet?.GetValue("ProxyServer") as string ?? "";
            r.Add(!proxy ? new DiagItem(DiagLevel.Ok, "Системный прокси выключен")
                : new DiagItem(DiagLevel.Warning, $"Включён системный прокси {server}" + (App.Vpn?.IsConnected == true ? " (это VPN острова)" : "") +
                                                  " — если не пользуешься прокси, выключи его"));
        }

        r.Add(TcpTimestampsEnabled()
            ? new DiagItem(DiagLevel.Ok, "TCP timestamps включены")
            : new DiagItem(DiagLevel.Warning, "TCP timestamps выключены", "Включить", () =>
            {
                EnsureTcpTimestamps();
                return TcpTimestampsEnabled() ? "TCP timestamps включены" : "Не удалось включить TCP timestamps";
            }));

        r.Add(Process.GetProcessesByName("AdguardSvc").Length > 0
            ? new DiagItem(DiagLevel.Error, "Найден AdGuard — он может мешать Discord")
            : new DiagItem(DiagLevel.Ok, "AdGuard не мешает"));

        List<(string name, string display)> services = AllServices();
        bool Has(Func<string, bool> match) => services.Any(s => match(s.name) || match(s.display));

        r.Add(Has(n => n.Contains("Killer", StringComparison.OrdinalIgnoreCase))
            ? new DiagItem(DiagLevel.Error, "Найдены службы Killer — они конфликтуют с zapret")
            : new DiagItem(DiagLevel.Ok, "Killer не мешает"));
        r.Add(Has(n => n.Contains("Intel", StringComparison.OrdinalIgnoreCase) && n.Contains("Connectivity", StringComparison.OrdinalIgnoreCase) && n.Contains("Network", StringComparison.OrdinalIgnoreCase))
            ? new DiagItem(DiagLevel.Error, "Найдена Intel Connectivity Network Service — конфликтует с zapret")
            : new DiagItem(DiagLevel.Ok, "Intel Connectivity не мешает"));
        r.Add(Has(n => n.Contains("TracSrvWrapper", StringComparison.OrdinalIgnoreCase) || n.Equals("EPWD", StringComparison.OrdinalIgnoreCase))
            ? new DiagItem(DiagLevel.Error, "Найден Check Point — конфликтует с zapret, его стоит удалить")
            : new DiagItem(DiagLevel.Ok, "Check Point не мешает"));
        r.Add(Has(n => n.Contains("SmartByte", StringComparison.OrdinalIgnoreCase))
            ? new DiagItem(DiagLevel.Error, "Найден SmartByte — конфликтует с zapret, отключи его в services.msc")
            : new DiagItem(DiagLevel.Ok, "SmartByte не мешает"));

        r.Add(File.Exists(Path.Combine(Bin, "WinDivert64.sys"))
            ? new DiagItem(DiagLevel.Ok, "Файл WinDivert64.sys на месте")
            : new DiagItem(DiagLevel.Error, $"Нет WinDivert64.sys — антивирус удалил его. Добавь {ZapretService.Root} в исключения и обнови zapret"));

        string[] vpn = services.Where(s => s.name.Contains("VPN", StringComparison.OrdinalIgnoreCase) || s.name.Contains("HappService", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.name).ToArray();
        r.Add(vpn.Length > 0
            ? new DiagItem(DiagLevel.Warning, $"Найдены VPN-службы: {string.Join(", ", vpn)} — некоторые VPN конфликтуют с zapret")
            : new DiagItem(DiagLevel.Ok, "VPN-службы не мешают"));

        r.Add(SecureDnsConfigured()
            ? new DiagItem(DiagLevel.Ok, "Безопасный DNS (DoH) настроен в Windows")
            : new DiagItem(DiagLevel.Warning, "Безопасный DNS не настроен в Windows — включи «Безопасный DNS» в браузере (не провайдера по умолчанию)"));

        string hosts = File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : "";
        bool ytInHosts = Regex.IsMatch(hosts, @"^[^#\r\n]*(youtube\.com|youtu\.be)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        if (ytInHosts) r.Add(new DiagItem(DiagLevel.Warning, "В hosts есть записи youtube.com / youtu.be — они могут мешать YouTube"));

        bool winwsRunning = Process.GetProcessesByName("winws").Length > 0;
        string? wd = ServiceState("WinDivert");
        if (!winwsRunning && wd is "RUNNING" or "STOP_PENDING")
            r.Add(new DiagItem(DiagLevel.Warning, "winws.exe не запущен, а драйвер WinDivert висит активным", "Выгрузить", () =>
            {
                StopAndDelete("WinDivert");
                return ServiceState("WinDivert") == null ? "WinDivert выгружен" : "WinDivert не выгружается — проверь другие обходы (GoodbyeDPI и т. п.)";
            }));

        string[] conflicts = new[] { "GoodbyeDPI", "discordfix_zapret", "winws1", "winws2" }.Where(ServiceExists).ToArray();
        r.Add(conflicts.Length > 0
            ? new DiagItem(DiagLevel.Error, $"Найдены другие обходы: {string.Join(", ", conflicts)}", "Удалить их", () =>
            {
                foreach (string c in conflicts) StopAndDelete(c);
                StopAndDelete("WinDivert");
                StopAndDelete("WinDivert14");
                return "Конфликтующие службы удалены";
            })
            : new DiagItem(DiagLevel.Ok, "Других обходов (GoodbyeDPI и т. п.) нет"));

        return r;
    }

    private static List<(string name, string display)> AllServices()
    {
        var list = new List<(string, string)>();
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (root == null) return list;
            foreach (string name in root.GetSubKeyNames())
            {
                using RegistryKey? k = root.OpenSubKey(name);
                // только настоящие службы (Win32), без драйверов
                if (k?.GetValue("Type") is int type && (type & 0x30) == 0) continue;
                list.Add((name, k?.GetValue("DisplayName") as string ?? ""));
            }
        }
        catch
        {
            // нет доступа к реестру
        }
        return list;
    }

    private static bool SecureDnsConfigured()
    {
        try
        {
            using RegistryKey? root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\InterfaceSpecificParameters");
            if (root == null) return false;
            foreach (string iface in root.GetSubKeyNames())
            {
                using RegistryKey? k = root.OpenSubKey(iface);
                if (k == null) continue;
                foreach (string sub in k.GetSubKeyNames())
                {
                    using RegistryKey? s = k.OpenSubKey(sub);
                    if (s == null) continue;
                    foreach (string server in s.GetSubKeyNames())
                    {
                        using RegistryKey? d = s.OpenSubKey(server);
                        if (d?.GetValue("DohFlags") is long f && f > 0) return true;
                        if (d?.GetValue("DohFlags") is int i && i > 0) return true;
                    }
                }
            }
        }
        catch
        {
            // нет доступа
        }
        return false;
    }
}

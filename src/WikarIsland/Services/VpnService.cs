using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WikarIsland.Services;

/// <summary>Сервер из ссылки vless://… (в настройках хранится зашифрованным).</summary>
public sealed class VpnServer
{
    public string Name { get; set; } = "";

    /// <summary>Ссылка, зашифрованная DPAPI текущего пользователя (base64).</summary>
    public string Protected { get; set; } = "";

    public string Link
    {
        get
        {
            try
            {
                byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(Protected), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }
        set => Protected = Convert.ToBase64String(
            ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    }
}

/// <summary>Разобранная ссылка VLESS.</summary>
public sealed class VlessLink
{
    public string Id { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string Name { get; init; } = "";
    public Dictionary<string, string> Query { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string Q(string key, string fallback = "") => Query.TryGetValue(key, out string? v) && v.Length > 0 ? v : fallback;

    public static VlessLink Parse(string link)
    {
        link = link.Trim();
        if (!link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Ссылка должна начинаться с vless://");

        string rest = link[8..];
        string name = "";
        int hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(rest[(hash + 1)..]);
            rest = rest[..hash];
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = rest.IndexOf('?');
        if (q >= 0)
        {
            foreach (string pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string k = eq >= 0 ? pair[..eq] : pair;
                string v = eq >= 0 ? Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')) : "";
                query[k] = v;
            }
            rest = rest[..q];
        }
        rest = rest.TrimEnd('/');

        int at = rest.LastIndexOf('@');
        if (at <= 0) throw new FormatException("В ссылке нет UUID@адрес:порт");
        string id = Uri.UnescapeDataString(rest[..at]);
        string hostPort = rest[(at + 1)..];

        string host;
        string portText;
        if (hostPort.StartsWith('['))
        {
            int close = hostPort.IndexOf(']');
            host = hostPort[1..close];
            portText = hostPort[(close + 2)..];
        }
        else
        {
            int colon = hostPort.LastIndexOf(':');
            if (colon <= 0) throw new FormatException("В ссылке нет порта");
            host = hostPort[..colon];
            portText = hostPort[(colon + 1)..];
        }
        if (!int.TryParse(portText, out int port)) throw new FormatException("Неверный порт");

        return new VlessLink
        {
            Id = id,
            Host = host,
            Port = port,
            Name = string.IsNullOrWhiteSpace(name) ? $"{host}:{port}" : name,
            Query = query,
        };
    }

    /// <summary>Конфиг Xray: локальные HTTP- и SOCKS-входы, выход VLESS, локальные адреса — напрямую.</summary>
    public string ToXrayConfig(int httpPort, int socksPort)
    {
        string network = Q("type", "tcp").ToLowerInvariant();
        if (network == "raw") network = "tcp";
        if (network == "splithttp") network = "xhttp";
        string security = Q("security", "none").ToLowerInvariant();

        var stream = new JsonObject { ["network"] = network, ["security"] = security };

        if (security == "reality")
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = Q("sni", Host),
                ["fingerprint"] = Q("fp", "chrome"),
                ["publicKey"] = Q("pbk"),
                ["shortId"] = Q("sid"),
                ["spiderX"] = Q("spx", "/"),
            };
        }
        else if (security == "tls")
        {
            var tls = new JsonObject
            {
                ["serverName"] = Q("sni", Q("host", Host)),
                ["fingerprint"] = Q("fp", "chrome"),
                ["allowInsecure"] = Q("allowInsecure") is "1" or "true",
            };
            string alpn = Q("alpn");
            if (alpn.Length > 0) tls["alpn"] = new JsonArray(alpn.Split(',').Select(a => (JsonNode)a.Trim()).ToArray());
            stream["tlsSettings"] = tls;
        }

        switch (network)
        {
            case "ws":
                stream["wsSettings"] = new JsonObject { ["path"] = Q("path", "/"), ["headers"] = new JsonObject { ["Host"] = Q("host") } };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = Q("serviceName"), ["multiMode"] = Q("mode") == "multi" };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject { ["path"] = Q("path", "/"), ["host"] = Q("host") };
                break;
            case "xhttp":
                stream["xhttpSettings"] = new JsonObject { ["path"] = Q("path", "/"), ["host"] = Q("host"), ["mode"] = Q("mode", "auto") };
                break;
            case "h2":
            case "http":
                stream["network"] = "http";
                stream["httpSettings"] = new JsonObject { ["path"] = Q("path", "/"), ["host"] = new JsonArray(Q("host", Host)) };
                break;
            case "tcp" when Q("headerType") == "http":
                stream["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http" } };
                break;
        }

        var user = new JsonObject { ["id"] = Id, ["encryption"] = Q("encryption", "none") };
        string flow = Q("flow");
        if (flow.Length > 0) user["flow"] = flow;

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "http", ["listen"] = "127.0.0.1", ["port"] = httpPort, ["protocol"] = "http",
                    ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls") },
                },
                new JsonObject
                {
                    ["tag"] = "socks", ["listen"] = "127.0.0.1", ["port"] = socksPort, ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["udp"] = true },
                    ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls") },
                }),
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["tag"] = "proxy",
                    ["protocol"] = "vless",
                    ["settings"] = new JsonObject
                    {
                        ["vnext"] = new JsonArray(new JsonObject
                        {
                            ["address"] = Host, ["port"] = Port, ["users"] = new JsonArray(user),
                        }),
                    },
                    ["streamSettings"] = stream,
                },
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom" },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }),
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new JsonArray(
                    new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" },
                    new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("localhost"), ["outboundTag"] = "direct" }),
            },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed record VpnExit(string Ip, string Country, string CountryCode);

/// <summary>
/// Встроенный VPN на ядре Xray (то же ядро, что у v2rayN): запускает xray.exe с конфигом из ссылки VLESS
/// и включает системный прокси Windows. При выключении возвращает прежние настройки прокси.
/// </summary>
public sealed class VpnService
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*;172.30.*;172.31.*;192.168.*;<local>";

    private Process? _xray;
    private int _httpPort;

    public bool IsConnected { get; private set; }
    public bool IsBusy { get; private set; }
    public VpnExit? Exit { get; private set; }
    public string? ServerName { get; private set; }

    public event Action? StateChanged;

    /// <summary>Подключение установлено (страна/IP) или ошибка.</summary>
    public event Action<VpnExit?, string?>? Connected;

    private static string XrayDir => Path.Combine(AppContext.BaseDirectory, "xray");
    private static string XrayExe => Path.Combine(XrayDir, "xray.exe");
    public static bool XrayAvailable => File.Exists(XrayExe);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);

    public VpnServer? ActiveServer
    {
        get
        {
            List<VpnServer> list = App.Settings.VpnServers;
            if (list.Count == 0) return null;
            int i = Math.Clamp(App.Settings.VpnActive, 0, list.Count - 1);
            return list[i];
        }
    }

    /// <summary>Если прошлый раз остров закрылся аварийно — прокси мог остаться включённым на наш порт.</summary>
    public void RecoverAfterCrash()
    {
        if (App.Settings.VpnProxyWasSet)
        {
            RestoreProxy();
            App.Settings.VpnProxyWasSet = false;
            App.Settings.Save();
        }
        foreach (Process p in Process.GetProcessesByName("xray"))
        {
            try
            {
                if (string.Equals(p.MainModule?.FileName, XrayExe, StringComparison.OrdinalIgnoreCase)) p.Kill();
            }
            catch
            {
                // чужой xray (например, от v2rayN) не трогаем
            }
        }
    }

    public async Task ToggleAsync()
    {
        if (IsBusy) return;
        if (IsConnected) Disconnect();
        else await ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        VpnServer? server = ActiveServer;
        if (server == null)
        {
            Connected?.Invoke(null, "Добавь ссылку vless:// в настройках (⚙ → VPN)");
            return;
        }
        if (!XrayAvailable)
        {
            Connected?.Invoke(null, "Не найден xray\\xray.exe рядом с программой — скачай сборку заново");
            return;
        }

        IsBusy = true;
        StateChanged?.Invoke();
        try
        {
            VlessLink link = VlessLink.Parse(server.Link);
            _httpPort = FreePort(10809);
            int socksPort = FreePort(_httpPort == 10809 ? 10808 : _httpPort + 1);

            string configPath = Path.Combine(AppSettings.DataDir, "xray-config.json");
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(configPath, link.ToXrayConfig(_httpPort, socksPort));

            var info = new ProcessStartInfo(XrayExe, $"run -c \"{configPath}\"")
            {
                WorkingDirectory = XrayDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.Environment["XRAY_LOCATION_ASSET"] = XrayDir;
            _xray = Process.Start(info) ?? throw new InvalidOperationException("Xray не запустился");

            string logPath = Path.Combine(AppSettings.DataDir, "xray.log");
            File.WriteAllText(logPath, "");
            _xray.OutputDataReceived += (_, e) => AppendLog(logPath, e.Data);
            _xray.ErrorDataReceived += (_, e) => AppendLog(logPath, e.Data);
            _xray.BeginOutputReadLine();
            _xray.BeginErrorReadLine();

            await Task.Delay(700);
            if (_xray.HasExited)
                throw new InvalidOperationException("Xray сразу завершился — проверь ссылку (подробности в xray.log)");

            SetProxy($"127.0.0.1:{_httpPort}");
            App.Settings.VpnProxyWasSet = true;
            App.Settings.Save();

            IsConnected = true;
            ServerName = link.Name;
            StateChanged?.Invoke();

            Exit = await CheckExitAsync(_httpPort);
            if (Exit == null)
            {
                Disconnect();
                Connected?.Invoke(null, "Сервер не отвечает. Проверь ссылку или попробуй другой сервер");
                return;
            }
            Connected?.Invoke(Exit, null);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Disconnect();
            Connected?.Invoke(null, ex.Message);
        }
        finally
        {
            IsBusy = false;
            StateChanged?.Invoke();
        }
    }

    public void Disconnect()
    {
        if (App.Settings.VpnProxyWasSet)
        {
            RestoreProxy();
            App.Settings.VpnProxyWasSet = false;
            App.Settings.Save();
        }
        try
        {
            if (_xray is { HasExited: false }) _xray.Kill();
        }
        catch
        {
            // уже закрылся
        }
        _xray = null;
        IsConnected = false;
        Exit = null;
        StateChanged?.Invoke();
    }

    private static void AppendLog(string path, string? line)
    {
        if (line == null) return;
        try { File.AppendAllText(path, line + Environment.NewLine); } catch { }
    }

    private static int FreePort(int preferred)
    {
        for (int port = preferred; port < preferred + 50; port += 2)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch (SocketException)
            {
                // занят — например, запущен v2rayN
            }
        }
        return preferred;
    }

    /// <summary>Через какую страну и IP теперь идёт трафик.</summary>
    private static async Task<VpnExit?> CheckExitAsync(int httpPort)
    {
        var handler = new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{httpPort}"), UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WikarIsland");
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                string json = await http.GetStringAsync("https://ipwho.is/");
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement r = doc.RootElement;
                if (r.TryGetProperty("success", out JsonElement ok) && ok.ValueKind == JsonValueKind.False) return null;
                return new VpnExit(
                    r.GetProperty("ip").GetString() ?? "",
                    r.TryGetProperty("country", out JsonElement c) ? c.GetString() ?? "" : "",
                    r.TryGetProperty("country_code", out JsonElement cc) ? cc.GetString() ?? "" : "");
            }
            catch
            {
                await Task.Delay(800);
            }
        }
        return null;
    }

    // ---------- системный прокси Windows ----------

    private static void SetProxy(string server)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(InternetSettings);
        AppSettings s = App.Settings;
        // запоминаем, что было до нас
        s.VpnPrevProxyEnable = key.GetValue("ProxyEnable") is int e ? e : 0;
        s.VpnPrevProxyServer = key.GetValue("ProxyServer") as string;
        s.VpnPrevProxyOverride = key.GetValue("ProxyOverride") as string;

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", server);
        key.SetValue("ProxyOverride", Bypass);
        NotifyProxyChanged();
    }

    private static void RestoreProxy()
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(InternetSettings);
            AppSettings s = App.Settings;
            key.SetValue("ProxyEnable", s.VpnPrevProxyEnable, RegistryValueKind.DWord);
            if (s.VpnPrevProxyServer != null) key.SetValue("ProxyServer", s.VpnPrevProxyServer);
            else key.DeleteValue("ProxyServer", false);
            if (s.VpnPrevProxyOverride != null) key.SetValue("ProxyOverride", s.VpnPrevProxyOverride);
            else key.DeleteValue("ProxyOverride", false);
            NotifyProxyChanged();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static void NotifyProxyChanged()
    {
        const int INTERNET_OPTION_SETTINGS_CHANGED = 39, INTERNET_OPTION_REFRESH = 37;
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}

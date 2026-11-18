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
    /// <param name="address">IP сервера вместо имени (режим TUN: имя узнаём заранее, пока туннеля ещё нет).</param>
    /// <param name="tun">Режим TUN: адреса уже узнал sing-box, свой DNS Xray не нужен.</param>
    public string ToXrayConfig(int httpPort, int socksPort, IReadOnlyList<string>? dnsServers = null, string? address = null, bool tun = false)
    {
        // сервер задан IP — заголовки Host и SNI всё равно должны нести имя из ссылки
        string hostHeader = Q("host", address != null && address != Host ? Host : "");
        if (tun) dnsServers = null;
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
                stream["wsSettings"] = new JsonObject { ["path"] = Q("path", "/"), ["headers"] = new JsonObject { ["Host"] = hostHeader } };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = Q("serviceName"), ["multiMode"] = Q("mode") == "multi" };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject { ["path"] = Q("path", "/"), ["host"] = hostHeader };
                break;
            case "xhttp":
            {
                var xhttp = new JsonObject { ["path"] = Q("path", "/"), ["host"] = hostHeader, ["mode"] = Q("mode", "auto") };
                // дополнительные настройки из ссылки (xmux, паддинг, отдельный адрес загрузки) — как в v2rayN
                if (Q("extra").Length > 0)
                {
                    try
                    {
                        if (JsonNode.Parse(Q("extra")) is JsonObject extra) xhttp["extra"] = extra;
                    }
                    catch (JsonException)
                    {
                        // кривой extra — без него
                    }
                }
                stream["xhttpSettings"] = xhttp;
                break;
            }
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
                            ["address"] = address ?? Host, ["port"] = Port, ["users"] = new JsonArray(user),
                        }),
                    },
                    ["streamSettings"] = stream,
                },
                new JsonObject
                {
                    ["tag"] = "direct", ["protocol"] = "freedom",
                    // со своим DNS прямые соединения тоже резолвим через него
                    ["settings"] = dnsServers is { Count: > 0 } ? new JsonObject { ["domainStrategy"] = "UseIP" } : new JsonObject(),
                },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }),
            ["routing"] = new JsonObject
            {
                // как в v2rayN: имя сайта уходит на сервер, адрес узнаёт сервер. С IPIfNonMatch Xray сам спрашивал местный DNS —
                // медленно, а в России провайдер подсовывает заблокированным сайтам «локальные» адреса, и они шли мимо VPN
                ["domainStrategy"] = "AsIs",
                ["rules"] = new JsonArray(
                    new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" },
                    new JsonObject { ["type"] = "field", ["domain"] = new JsonArray("localhost"), ["outboundTag"] = "direct" }),
            },
        };
        if (dnsServers is { Count: > 0 })
        {
            config["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(dnsServers.Select(d => (JsonNode)d).ToArray()),
                ["queryStrategy"] = "UseIPv4",
            };
        }
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed record VpnExit(string Ip, string Country, string CountryCode);

/// <summary>
/// Встроенный VPN на ядре Xray (то же ядро, что у v2rayN): запускает xray.exe с конфигом из ссылки VLESS
/// и включает системный прокси Windows (при выключении возвращает прежние настройки) — или, в режиме TUN,
/// весь трафик компьютера через sing-box (см. <see cref="VpnTun"/>).
/// </summary>
public sealed class VpnService
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.2*;172.30.*;172.31.*;192.168.*;<local>";

    private Process? _xray;
    private Process? _singBox;
    private int _httpPort;

    /// <summary>Сейчас подключён в режиме TUN (весь трафик), а не через системный прокси.</summary>
    public bool IsTun { get; private set; }

    /// <summary>Выбран режим TUN.</summary>
    public static bool TunSelected => App.Settings.VpnMode == "tun";

    /// <summary>Для TUN нужны права администратора, а остров запущен без них — спросить, перезапустить ли.</summary>
    public event Action? AdminRequired;

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
        foreach ((string name, string exe) in new[] { ("xray", XrayExe), ("sing-box", VpnTun.Exe) })
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) p.Kill();
                }
                catch
                {
                    // чужой xray или sing-box (например, от v2rayN) не трогаем
                }
            }
        }

        // перезапустились с правами администратора ради TUN — сразу подключаемся
        if (App.Settings.VpnResumeAfterRestart)
        {
            App.Settings.VpnResumeAfterRestart = false;
            App.Settings.Save();
            // чуть позже — когда пилюля и панель уже созданы и подписаны на события
            if (SystemMonitorService.IsAdmin)
                _ = Task.Delay(2500).ContinueWith(_ => ConnectAsync(), TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    /// <summary>Перезапуск острова от администратора; после него VPN включится сам.</summary>
    public static bool RestartForTun()
    {
        App.Settings.VpnResumeAfterRestart = true;
        App.Settings.Save();
        if (SystemMonitorService.RestartAsAdmin()) return true;
        App.Settings.VpnResumeAfterRestart = false;
        App.Settings.Save();
        return false;
    }

    /// <summary>Сменить режим (прокси или TUN); если VPN включён — переподключиться в новом.</summary>
    public async Task SetModeAsync(string mode)
    {
        mode = mode == "tun" ? "tun" : "proxy";
        if (App.Settings.VpnMode == mode && (!IsConnected || IsTun == (mode == "tun"))) return;
        App.Settings.VpnMode = mode;
        App.Settings.Save();
        StateChanged?.Invoke();
        if (IsConnected)
        {
            Disconnect();
            await ConnectAsync();
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
        bool tun = TunSelected;
        if (tun && !VpnTun.Available)
        {
            Connected?.Invoke(null, "Не найден sing-box\\sing-box.exe рядом с программой — скачай сборку заново");
            return;
        }
        if (tun && !SystemMonitorService.IsAdmin)
        {
            Connected?.Invoke(null, "Для режима TUN нужны права администратора");
            AdminRequired?.Invoke();
            return;
        }

        IsBusy = true;
        StateChanged?.Invoke();
        try
        {
            VlessLink link = VlessLink.Parse(server.Link);
            _httpPort = FreePort(10809);
            int socksPort = FreePort(_httpPort == 10809 ? 10808 : _httpPort + 1);

            // TUN: адрес сервера узнаём сейчас, пока туннеля нет, — потом Xray не придётся спрашивать DNS (иначе петля)
            string? address = null;
            if (tun && !IPAddress.TryParse(link.Host, out _))
            {
                IPAddress[] ips = await Dns.GetHostAddressesAsync(link.Host);
                IPAddress? v4 = ips.FirstOrDefault(i => i.AddressFamily == AddressFamily.InterNetwork) ?? ips.FirstOrDefault();
                address = v4?.ToString() ?? throw new InvalidOperationException($"Не удалось узнать адрес сервера {link.Host}");
            }

            string configPath = Path.Combine(AppSettings.DataDir, "xray-config.json");
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(configPath, link.ToXrayConfig(_httpPort, socksPort, ParseDns(App.Settings.VpnDns), address, tun));

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

            if (tun)
            {
                await StartSingBoxAsync(socksPort);
            }
            else
            {
                SetProxy($"127.0.0.1:{_httpPort}");
                App.Settings.VpnProxyWasSet = true;
                App.Settings.Save();
            }

            IsConnected = true;
            IsTun = tun;
            ServerName = link.Name;
            StateChanged?.Invoke();

            // в TUN проверяем без прокси — так, как пойдёт трафик любой программы
            Exit = await CheckExitAsync(tun ? null : _httpPort);
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
        foreach (Process? p in new[] { _singBox, _xray })
        {
            try
            {
                if (p is { HasExited: false })
                {
                    p.Kill();
                    p.WaitForExit(3000); // sing-box снимает адаптер и маршруты — дождёмся
                }
            }
            catch
            {
                // уже закрылся
            }
        }
        _singBox = null;
        _xray = null;
        IsConnected = false;
        IsTun = false;
        Exit = null;
        StateChanged?.Invoke();
    }

    /// <summary>Запустить sing-box с TUN и дождаться адаптера.</summary>
    private async Task StartSingBoxAsync(int socksPort)
    {
        string logPath = Path.Combine(AppSettings.DataDir, "sing-box.log");
        File.WriteAllText(logPath, "");
        string configPath = Path.Combine(AppSettings.DataDir, "sing-box-config.json");
        File.WriteAllText(configPath, VpnTun.Config(socksPort, ParseDns(App.Settings.VpnDns)));

        var info = new ProcessStartInfo(VpnTun.Exe, $"run -c \"{configPath}\" -D \"{AppSettings.DataDir}\"")
        {
            WorkingDirectory = VpnTun.Dir,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        _singBox = Process.Start(info) ?? throw new InvalidOperationException("sing-box не запустился");
        _singBox.OutputDataReceived += (_, e) => AppendLog(logPath, e.Data);
        _singBox.ErrorDataReceived += (_, e) => AppendLog(logPath, e.Data);
        _singBox.BeginOutputReadLine();
        _singBox.BeginErrorReadLine();
        Process started = _singBox;
        System.Windows.Threading.Dispatcher ui = System.Windows.Application.Current.Dispatcher;
        started.EnableRaisingEvents = true;
        started.Exited += (_, _) => ui.BeginInvoke(() =>
        {
            // упал сам (не мы выключили): адаптер исчез, интернет снова напрямую — так и показываем
            if (_singBox != started || !IsConnected) return;
            Disconnect();
            Connected?.Invoke(null, "TUN остановился — подробности в sing-box.log" + LastLogLine(logPath));
        });

        // адаптер и маршруты появляются за 1–3 секунды
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(300);
            if (_singBox.HasExited)
                throw new InvalidOperationException("sing-box сразу завершился — подробности в sing-box.log" + LastLogLine(logPath));
            if (TunAdapterUp()) return;
        }
        if (_singBox.HasExited) throw new InvalidOperationException("sing-box завершился — подробности в sing-box.log" + LastLogLine(logPath));
    }

    private static bool TunAdapterUp()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Any(n => n.Name == VpnTun.InterfaceName && n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up);
        }
        catch
        {
            return false;
        }
    }

    private static string LastLogLine(string path)
    {
        try
        {
            string? line = File.ReadLines(path).LastOrDefault(l => l.Trim().Length > 0);
            return line == null ? "" : ": " + (line.Length > 200 ? line[..200] : line);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>«1.1.1.1, https://dns.google/dns-query» → список серверов для Xray.</summary>
    public static List<string> ParseDns(string text) =>
        text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => d.Length > 0)
            .Distinct()
            .ToList();

    /// <summary>Переподключиться, если VPN включён (например, после смены DNS или сервера).</summary>
    public async Task ReconnectIfConnectedAsync()
    {
        if (!IsConnected) return;
        Disconnect();
        await ConnectAsync();
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
    private static async Task<VpnExit?> CheckExitAsync(int? httpPort)
    {
        var handler = httpPort is int port
            ? new HttpClientHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true }
            : new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WikarIsland");
        for (int attempt = 0; attempt < (httpPort == null ? 4 : 2); attempt++)
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

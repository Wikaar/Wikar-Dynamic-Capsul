using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WikarIsland.Services;

/// <summary>
/// Режим TUN — весь трафик компьютера через VPN, как TUN в v2rayN. Работают два ядра:
/// sing-box создаёт виртуальный адаптер (wintun) и забирает в него всё, включая UDP и DNS,
/// и отдаёт трафик в локальный SOCKS Xray, а Xray уже отвечает за VLESS (любой тип ключа, в том числе xhttp).
/// Соединения самого Xray с сервером идут мимо туннеля, иначе трафик зациклится.
/// </summary>
public static class VpnTun
{
    public const string InterfaceName = "Wikar Island TUN";

    public static string Dir => Path.Combine(AppContext.BaseDirectory, "sing-box");
    public static string Exe => Path.Combine(Dir, "sing-box.exe");
    public static bool Available => File.Exists(Exe);

    /// <summary>DNS внутри туннеля: свой из настроек (первый https:// или IP) или Cloudflare по DoH — всё через VPN.</summary>
    private static JsonObject RemoteDns(IReadOnlyList<string> userDns)
    {
        foreach (string d in userDns)
        {
            if (d.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(d, UriKind.Absolute, out Uri? uri))
            {
                var server = new JsonObject
                {
                    ["type"] = "https", ["tag"] = "remote", ["server"] = uri.Host, ["detour"] = "proxy",
                };
                if (uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/dns-query") server["path"] = uri.AbsolutePath;
                if (!uri.IsDefaultPort) server["server_port"] = uri.Port;
                if (!IPAddress.TryParse(uri.Host, out _)) server["domain_resolver"] = "local"; // имя DoH-сервера — один раз через местный DNS
                return server;
            }
        }
        foreach (string d in userDns)
            if (IPAddress.TryParse(d, out IPAddress? ip))
                return new JsonObject { ["type"] = "tcp", ["tag"] = "remote", ["server"] = ip.ToString(), ["detour"] = "proxy" };
        return new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = "proxy" };
    }

    /// <summary>Конфиг sing-box (формат 1.12): TUN → SOCKS Xray; DNS перехватывается и уходит через VPN; локальная сеть — напрямую.</summary>
    public static string Config(int socksPort, IReadOnlyList<string> userDns)
    {
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true }, // вывод забирает остров в sing-box.log
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    RemoteDns(userDns),
                    new JsonObject { ["type"] = "local", ["tag"] = "local" }),
                ["rules"] = new JsonArray(
                    // имена в домашней сети (роутер, NAS) — местным DNS
                    new JsonObject { ["domain_suffix"] = new JsonArray("local", "lan", "home.arpa", "localdomain"), ["server"] = "local" }),
                ["final"] = "remote",
                ["strategy"] = "ipv4_only",
            },
            ["inbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "tun",
                    ["tag"] = "tun-in",
                    ["interface_name"] = InterfaceName,
                    ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                    ["mtu"] = 9000,
                    ["auto_route"] = true,
                    ["strict_route"] = true, // без утечек мимо туннеля, в том числе DNS
                    ["stack"] = "mixed",
                }),
            ["outbounds"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "socks", ["tag"] = "proxy", ["server"] = "127.0.0.1", ["server_port"] = socksPort, ["version"] = "5",
                },
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray(
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
                    // сам Xray ходит к серверу VLESS напрямую — иначе петля
                    new JsonObject { ["process_name"] = new JsonArray("xray.exe"), ["outbound"] = "direct" },
                    new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" }),
                ["final"] = "proxy",
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
            },
        };
        return config.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

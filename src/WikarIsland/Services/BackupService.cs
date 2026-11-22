using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace WikarIsland.Services;

/// <summary>Резервная копия настроек острова в один .zip: настройки, заметки, ярлыки, VPN-ссылки.</summary>
public static class BackupService
{
    private const string VpnFile = "vpn-links.txt";
    private static readonly string[] Skip = { "xray-config.json" };

    /// <summary>Сохранить копию. VPN-ссылки кладутся открытым текстом, чтобы их можно было перенести на другой ПК.</summary>
    public static string Export(string zipPath)
    {
        App.Settings.Save();
        App.Notes?.Save();
        if (File.Exists(zipPath)) File.Delete(zipPath);
        int count = 0;
        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (string file in Directory.GetFiles(AppSettings.DataDir, "*.json"))
            {
                if (Skip.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(file, Path.GetFileName(file));
                count++;
            }
            List<string> links = App.Settings.VpnServers.Select(s => s.Link).Where(l => l.Length > 0).ToList();
            if (links.Count > 0)
            {
                ZipArchiveEntry entry = zip.CreateEntry(VpnFile);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(string.Join("\n", links));
            }
        }
        return $"Сохранено файлов: {count}" + (App.Settings.VpnServers.Count > 0 ? " · в копии есть VPN-ссылки, не выкладывай её в открытый доступ" : "");
    }

    /// <summary>Восстановить из копии. После этого остров нужно перезапустить.</summary>
    public static void Import(string zipPath)
    {
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        if (zip.GetEntry("settings.json") == null) throw new InvalidDataException("Это не резервная копия Wikar Dynamic Capsul — в ней нет settings.json");

        App.Vpn?.Disconnect();
        App.Zapret?.Stop();
        Directory.CreateDirectory(AppSettings.DataDir);
        foreach (ZipArchiveEntry entry in zip.Entries.Where(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && e.Name == e.FullName))
            entry.ExtractToFile(Path.Combine(AppSettings.DataDir, entry.Name), overwrite: true);

        // VPN-ссылки зашифрованы ключом Windows-пользователя — на другом ПК шифруем заново
        string settingsPath = Path.Combine(AppSettings.DataDir, "settings.json");
        AppSettings restored = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath)) ?? new AppSettings();
        if (zip.GetEntry(VpnFile) is { } vpn)
        {
            using var reader = new StreamReader(vpn.Open(), Encoding.UTF8);
            string[] links = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var servers = new List<VpnServer>();
            for (int i = 0; i < links.Length; i++)
            {
                string name = i < restored.VpnServers.Count ? restored.VpnServers[i].Name : "";
                if (name.Length == 0) name = VlessLinkName(links[i]);
                servers.Add(new VpnServer { Name = name, Link = links[i] });
            }
            restored.VpnServers = servers;
        }
        restored.VpnProxyWasSet = false;
        restored.ZapretDir = null;      // папка zapret на другом ПК своя — скачается заново
        restored.ZapretVersion = null;
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(restored, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string VlessLinkName(string link)
    {
        int hash = link.IndexOf('#');
        return hash >= 0 ? Uri.UnescapeDataString(link[(hash + 1)..]) : "Сервер";
    }
}

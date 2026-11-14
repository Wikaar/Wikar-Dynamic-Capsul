using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WikarIsland.Services;

public sealed class LauncherItem
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class PillPosition
{
    public double Left { get; set; }
    public double Top { get; set; }

    /// <summary>left / center / right — к какому краю прижата пилюля внутри своего окна.</summary>
    public string Align { get; set; } = "center";
}

/// <summary>Настройки, хранятся в %AppData%\WikarIsland\settings.json.</summary>
public sealed class AppSettings
{
    public bool AutoStart { get; set; }

    /// <summary>Автозапуск с правами администратора (задача в Планировщике) — нужен для температуры CPU.</summary>
    public bool AutoStartElevated { get; set; }
    public bool AutoHide { get; set; }

    /// <summary>Раскрывать остров при наведении (иначе — по клику).</summary>
    public bool OpenOnHover { get; set; }
    public bool EyesEnabled { get; set; } = true;

    /// <summary>Стиль глаз: classic, cat, robot, cute, sleepy, angry, pixel.</summary>
    public string EyeStyle { get; set; } = "classic";

    /// <summary>Ярлыки на «Главной».</summary>
    public List<LauncherItem> Launcher { get; set; } = new();

    /// <summary>both — глаза и часы, eyes — только глаза, clock — только часы.</summary>
    public string PillMode { get; set; } = "both";

    public string Hotkey { get; set; } = "Ctrl+Alt+Space";

    /// <summary>Пилюля откреплена: её можно таскать мышью.</summary>
    public bool Unlocked { get; set; }

    /// <summary>Позиция пилюли для каждого монитора (ключ — имя устройства экрана).</summary>
    public Dictionary<string, PillPosition> Positions { get; set; } = new();

    public string? LastScreen { get; set; }

    public bool MusicInPill { get; set; } = true;
    public bool WeatherInPill { get; set; } = true;
    public string? WeatherCity { get; set; }
    public double? WeatherLat { get; set; }
    public double? WeatherLon { get; set; }

    public bool NotificationsEnabled { get; set; } = true;

    // ---------- VPN (VLESS через Xray) ----------
    public List<VpnServer> VpnServers { get; set; } = new();
    public int VpnActive { get; set; }
    public bool VpnProxyWasSet { get; set; }
    public int VpnPrevProxyEnable { get; set; }
    public string? VpnPrevProxyServer { get; set; }
    public string? VpnPrevProxyOverride { get; set; }

    /// <summary>Программы, уведомления которых не показываем (AppUserModelId).</summary>
    public List<string> MutedNotificationApps { get; set; } = new();

    /// <summary>Программы, от которых уже приходили уведомления: AppUserModelId → имя.</summary>
    public Dictionary<string, string> KnownNotificationApps { get; set; } = new();
    public string LastTab { get; set; } = "home";

    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WikarIsland");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // испорченный файл — начинаем с настроек по умолчанию
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // не смогли записать — не страшно, попробуем в следующий раз
        }
    }
}

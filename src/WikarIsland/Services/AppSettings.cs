using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace WikarIsland.Services;

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
    public bool AutoHide { get; set; }
    public bool EyesEnabled { get; set; } = true;

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

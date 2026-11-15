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

    // ---------- zapret ----------
    public string? ZapretVersion { get; set; }

    /// <summary>Папка текущей версии zapret (каждая версия — в своей папке, старую драйвер может держать).</summary>
    public string? ZapretDir { get; set; }
    public string? ZapretStrategy { get; set; }
    public bool ZapretGameFilter { get; set; }  // старая галочка, переносится в ZapretGameMode

    /// <summary>Игровой фильтр: disabled, all (TCP и UDP), tcp, udp.</summary>
    // оформление капсулы
    /// <summary>Визуализатор: spectrum (под настоящий звук), simple (анимация), off.</summary>
    public string Visualizer { get; set; } = "spectrum";
    public bool MusicGlow { get; set; } = true;
    /// <summary>Слегка окрашивать саму капсулу в цвет обложки.</summary>
    public bool MusicTint { get; set; } = true;
    public string PillTheme { get; set; } = "black";
    public double PillOpacity { get; set; } = 1.0;

    // быстрая строка, Pomodoro
    public string CommandHotkey { get; set; } = "Win+Alt+Space";
    public string CommandAi { get; set; } = "chatgpt";
    public int PomodoroWork { get; set; } = 25;
    public int PomodoroBreak { get; set; } = 5;
    public int PomodoroLong { get; set; } = 15;

    // обновления
    public bool AutoUpdateCheck { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }
    public string? SkippedUpdate { get; set; }

    // «Не беспокоить»
    public bool DndManual { get; set; }
    public bool DndFullscreen { get; set; } = true;
    public bool DndHidePill { get; set; } = true;
    public bool DndSchedule { get; set; }
    public string DndFrom { get; set; } = "23:00";
    public string DndTo { get; set; } = "08:00";

    public bool ZapretShowInPill { get; set; } = true;

    // игры и загрузки в капсуле
    public bool GameHud { get; set; } = true;
    public bool DownloadsInPill { get; set; } = true;
    public bool DownloadFinishedCard { get; set; } = true;

    // голосовой помощник «Капсулка»
    public bool VoiceEnabled { get; set; }
    public string VoiceName { get; set; } = "ru-RU-DmitryNeural";
    public int VoiceRate { get; set; } = 5;
    public double VoiceVolume { get; set; } = 0.9;
    public bool VoiceFollowUp { get; set; } = true;
    public string VoiceHotkey { get; set; } = "Ctrl+Alt+K";

    // мозг: любой OpenAI-совместимый API
    public string AiPreset { get; set; } = "groq";
    public string AiBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public string AiModel { get; set; } = "llama-3.3-70b-versatile";

    /// <summary>Ключ API, зашифрованный DPAPI текущего пользователя.</summary>
    public string AiKeyProtected { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string AiKey
    {
        get
        {
            if (AiKeyProtected.Length == 0) return "";
            try
            {
                byte[] data = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(AiKeyProtected), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(data);
            }
            catch
            {
                return "";
            }
        }
        set => AiKeyProtected = string.IsNullOrWhiteSpace(value) ? "" : Convert.ToBase64String(
            System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value.Trim()), null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }
    public bool VpnShowInPill { get; set; } = true;

    public string ZapretGameMode { get; set; } = "disabled";
    public string ZapretGameTcp { get; set; } = "1024-65535";
    public string ZapretGameUdp { get; set; } = "1024-65535";
    public bool ZapretAutoStart { get; set; }

    // ---------- VPN (VLESS через Xray) ----------
    public List<VpnServer> VpnServers { get; set; } = new();
    public int VpnActive { get; set; }

    /// <summary>DNS для VPN: адреса через запятую или пробел (1.1.1.1, https://dns.google/dns-query …). Пусто — системный.</summary>
    public string VpnDns { get; set; } = "";
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

    /// <summary>Запрет записи: настройки только что восстановлены из копии и остров перезапускается.</summary>
    public static bool Frozen { get; set; }

    public void Save()
    {
        if (Frozen) return;
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

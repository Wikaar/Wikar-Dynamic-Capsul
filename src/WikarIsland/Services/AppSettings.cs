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

    /// <summary>Тема острова: dark или light (применяется после перезапуска).</summary>
    public string UiTheme { get; set; } = "dark";

    /// <summary>Неоновая окантовка: цвет #RRGGBB, пусто — выключена.</summary>
    public string NeonColor { get; set; } = "";
    public bool NeonPill { get; set; } = true;   // у свёрнутой капсулы (со свечением)
    public bool NeonPanel { get; set; } = true;  // у раскрытой

    // профиль: как Капсулка обращается к пользователю
    public string UserName { get; set; } = "";

    /// <summary>m — мужчина, f — женщина, пусто — не указан.</summary>
    public string UserGender { get; set; } = "";

    /// <summary>sir — сэр/мэм, title — мистер/мисс + имя, gos — господин/госпожа + имя, name — по имени, custom — своё.</summary>
    public string AddressStyle { get; set; } = "sir";
    public string AddressCustom { get; set; } = "";
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
    public bool VoiceAcknowledge { get; set; } = true; // короткое «Есть, сэр» после команд без ответа

    // Gemini Live — живой голосовой разговор вместо «распознать → ИИ → озвучить»
    public bool LiveEnabled { get; set; }
    public string LiveModel { get; set; } = "gemini-3.8-live";
    public string LiveVoice { get; set; } = "Charon";
    public bool LiveBargeIn { get; set; } // перебивать голосом (с колонками модель услышит сама себя)
    public bool LiveGoogleSearch { get; set; } // поиск Google внутри Live — только на платном тарифе
    public bool LiveOwnVoice { get; set; } // в Live говорить голосом Капсулки (Edge или Fish Audio), а не голосом Gemini
    public string GeminiKeyProtected { get; set; } = "";
    public string GroqKeyProtected { get; set; } = ""; // ключ Groq для распознавания речи (Whisper) — не зависит от выбранного «мозга»

    // свой голос через Fish Audio: ключ и ID голоса задаёт сам пользователь
    public string FishKeyProtected { get; set; } = "";
    public string FishVoiceId { get; set; } = "";
    public string FishModel { get; set; } = "s2.1-pro-free";
    public string SettingsSection { get; set; } = "general"; // последний открытый раздел настроек

    // встроенная музыка: Яндекс Музыка, YouTube Music, Spotify
    public string MusicProvider { get; set; } = "yandex";
    public bool MusicVoiceBuiltIn { get; set; } = true; // Капсулка включает музыку во встроенном плеере, если в сервис вошли
    public bool MusicYoutubeUsed { get; set; }

    // видео YouTube
    public bool VideoInIsland { get; set; } = true;   // «включи на ютубе …» — во встроенном плеере, а не в браузере
    public bool VideoStartMini { get; set; }          // новое видео сразу мини-плеером в углу
    public bool VideoAdBlock { get; set; } = true;    // блокировщик рекламы во встроенном браузере
    public string YandexTokenProtected { get; set; } = "";
    public string SpotifyClientId { get; set; } = "";
    public string SpotifyRefreshProtected { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string YandexToken
    {
        get => Unprotect(YandexTokenProtected);
        set => YandexTokenProtected = Protect(value);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string SpotifyRefresh
    {
        get => Unprotect(SpotifyRefreshProtected);
        set => SpotifyRefreshProtected = Protect(value);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FishKey
    {
        get => Unprotect(FishKeyProtected);
        set => FishKeyProtected = Protect(value);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string GroqKey
    {
        get => Unprotect(GroqKeyProtected);
        set => GroqKeyProtected = Protect(value);
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string GeminiKey
    {
        get => Unprotect(GeminiKeyProtected);
        set => GeminiKeyProtected = Protect(value);
    }

    private static string Protect(string value) => string.IsNullOrWhiteSpace(value) ? "" : Convert.ToBase64String(
        System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(value.Trim()), null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser));

    private static string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        try
        {
            return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(value), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }
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
    public string VpnMode { get; set; } = "proxy";          // proxy — системный прокси, tun — весь трафик (sing-box)
    public bool VpnResumeAfterRestart { get; set; }         // перезапустились от администратора ради TUN — подключиться сразу
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

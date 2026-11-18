using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WikarIsland.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Программа, у которой сейчас есть звук (сессия в микшере громкости).</summary>
public sealed record AppAudio(int ProcessId, string ProcessName, string Title, float Volume, bool Muted);

/// <summary>
/// Громкость, уровень звука и устройство вывода по умолчанию.
/// Работаем через NAudio (как и запись звука), иначе две обёртки одного COM-объекта мешают друг другу.
/// Смена устройства — через недокументированный IPolicyConfig (так же делают EarTrumpet и SoundSwitch).
/// </summary>
public sealed class AudioService
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private DateTime _deviceAt;

    /// <summary>Устройство вывода по умолчанию; перепроверяем раз в пару секунд.</summary>
    private MMDevice? Device
    {
        get
        {
            if (_device == null || (DateTime.Now - _deviceAt).TotalSeconds > 2)
            {
                _deviceAt = DateTime.Now;
                try
                {
                    _device = _enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                        : null;
                }
                catch
                {
                    _device = null;
                }
            }
            return _device;
        }
    }

    /// <summary>Текущий пик звука 0..1 — для «живой» пилюли.</summary>
    public float Peak
    {
        get
        {
            try { return Device?.AudioMeterInformation.MasterPeakValue ?? 0; }
            catch { _device = null; return 0; }
        }
    }

    /// <summary>Громкость 0..1, или -1, если устройства нет.</summary>
    public float Volume
    {
        get
        {
            try { return Device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? -1; }
            catch { _device = null; return -1; }
        }
        set
        {
            try
            {
                MMDevice? d = Device;
                if (d == null) return;
                d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
                if (value > 0) d.AudioEndpointVolume.Mute = false;
            }
            catch { _device = null; }
        }
    }

    public bool Muted
    {
        get
        {
            try { return Device?.AudioEndpointVolume.Mute ?? false; }
            catch { return false; }
        }
        set
        {
            try { if (Device != null) Device.AudioEndpointVolume.Mute = value; }
            catch { _device = null; }
        }
    }

    public void Step(float delta)
    {
        float v = Volume;
        if (v >= 0) Volume = Math.Clamp(v + delta, 0f, 1f);
    }

    public List<AudioDevice> GetOutputDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            string? defaultId = Device?.ID;
            foreach (MMDevice d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string name;
                try { name = d.FriendlyName; } catch { name = "Устройство"; }
                list.Add(new AudioDevice(d.ID, name, d.ID == defaultId));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return list;
    }

    public void SetDefaultOutput(string deviceId)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigComObject();
            for (int role = 0; role <= 2; role++) // console, multimedia, communications
                policy.SetDefaultEndpoint(deviceId, role);
            _device = null; // перечитать устройство по умолчанию
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ---------- громкость отдельных программ (как в микшере громкости) ----------

    private static readonly string[] BrowserProcesses = { "chrome", "msedge", "firefox", "opera", "browser", "brave", "vivaldi", "yandex" };

    /// <summary>Звуковые сессии программ на всех устройствах вывода (без системных звуков и острова).</summary>
    private List<(AudioSessionControl session, AppAudio info)> Sessions()
    {
        var list = new List<(AudioSessionControl, AppAudio)>();
        var names = new Dictionary<int, (string process, string title)>();
        try
        {
            foreach (MMDevice d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                AudioSessionManager manager = d.AudioSessionManager;
                manager.RefreshSessions();
                SessionCollection sessions = manager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    AudioSessionControl s = sessions[i];
                    try
                    {
                        if (s.IsSystemSoundsSession || (int)s.State == 2) continue; // 2 — сессия завершена
                        int pid = (int)s.GetProcessID;
                        if (pid == 0) continue;
                        if (!names.TryGetValue(pid, out var n))
                        {
                            n = Describe(pid);
                            names[pid] = n;
                        }
                        if (n.process.Length == 0) continue;
                        list.Add((s, new AppAudio(pid, n.process, n.title.Length > 0 ? n.title : s.DisplayName, s.SimpleAudioVolume.Volume, s.SimpleAudioVolume.Mute)));
                    }
                    catch
                    {
                        // сессия закрылась, пока перебирали
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return list;
    }

    private static (string process, string title) Describe(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            string title = "";
            try { title = p.MainModule?.FileVersionInfo.FileDescription ?? ""; } catch { /* нет доступа к процессу администратора */ }
            return (p.ProcessName, title);
        }
        catch
        {
            return ("", "");
        }
    }

    /// <summary>Какие программы сейчас могут звучать — для ИИ.</summary>
    public string DescribeApps()
    {
        List<AppAudio> apps = Sessions().Select(x => x.info).GroupBy(a => a.ProcessName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        if (apps.Count == 0) return "Сейчас ни у одной программы нет звука.";
        return "Программы со звуком: " + string.Join(", ", apps.Select(a =>
            $"{(a.Title.Length > 0 ? a.Title : a.ProcessName)} {Math.Round(a.Volume * 100)}%" + (a.Muted ? " (без звука)" : "")));
    }

    /// <summary>
    /// Громкость программы по названию («дискорд», «браузер», «хром»): точное значение, шаг или звук вкл/выкл.
    /// Возвращает итог для ответа или null, если такой программы со звуком нет.
    /// </summary>
    public string? SetAppVolume(string app, float? level, float? delta, bool? mute)
    {
        List<(AudioSessionControl session, AppAudio info)> all = Sessions();
        if (all.Count == 0) return null;
        List<(AudioSessionControl session, AppAudio info)> found = Match(all, app);
        if (found.Count == 0) return null;
        float result = 0;
        foreach ((AudioSessionControl s, AppAudio _) in found)
        {
            try
            {
                SimpleAudioVolume v = s.SimpleAudioVolume;
                if (mute is bool m) v.Mute = m;
                if (level is float l) v.Volume = Math.Clamp(l, 0f, 1f);
                else if (delta is float dl) v.Volume = Math.Clamp(v.Volume + dl, 0f, 1f);
                if ((level ?? delta) is { } changed && changed != 0 && v.Volume > 0) v.Mute = false;
                result = v.Volume;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
        AppAudio first = found[0].info;
        string name = first.Title.Length > 0 ? first.Title : first.ProcessName;
        if (mute == true) return $"{name}: звук выключен.";
        if (mute == false && level == null && delta == null) return $"{name}: звук включён.";
        return $"{name}: громкость {Math.Round(result * 100)}%.";
    }

    private static List<(AudioSessionControl, AppAudio)> Match(List<(AudioSessionControl session, AppAudio info)> all, string app)
    {
        string a = app.Trim().ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(a, @"^(браузер\w*|интернет\w*)$"))
            return all.Where(x => BrowserProcesses.Contains(x.info.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (System.Text.RegularExpressions.Regex.IsMatch(a, @"^(игр\w*)$") && App.Game.Active && App.Game.Pid > 0)
            return all.Where(x => x.info.ProcessId == App.Game.Pid).ToList();

        // «дискорда», «хрому», «спотифае» — пробуем и без окончания
        var queries = new List<string>(AssistantService.NameVariants(a));
        for (int cut = 1; cut <= 2 && a.Length - cut >= 3; cut++) queries.AddRange(AssistantService.NameVariants(a[..^cut]));
        double best = 0;
        string? bestProcess = null;
        foreach (string q in queries.Where(q => q.Length >= 2).Distinct())
        {
            foreach ((AudioSessionControl _, AppAudio info) in all)
            {
                double s = Math.Max(AppIndex.Similarity(q, info.ProcessName), AppIndex.Similarity(q, info.Title));
                if (s > best)
                {
                    best = s;
                    bestProcess = info.ProcessName;
                }
            }
        }
        if (bestProcess == null || best < 0.75) return new List<(AudioSessionControl, AppAudio)>();
        return all.Where(x => x.info.ProcessName.Equals(bestProcess, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    // ---------- COM ----------

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigComObject { }

    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        // первые 10 методов не используем, но они должны стоять на своих местах в таблице
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}

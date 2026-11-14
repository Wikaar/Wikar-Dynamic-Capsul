using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WikarIsland.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

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

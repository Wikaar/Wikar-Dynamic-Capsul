using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace WikarIsland.Services;

public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Громкость и устройство вывода по умолчанию через Core Audio.
/// Смена устройства — через недокументированный IPolicyConfig (так же делают EarTrumpet и SoundSwitch).
/// Все вызовы — из UI-потока.
/// </summary>
public sealed class AudioService
{
    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const uint DEVICE_STATE_ACTIVE = 1;
    private const int CLSCTX_ALL = 23;

    private static Guid _empty = Guid.Empty;
    private readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    private IAudioEndpointVolume? GetVolume()
    {
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out IMMDevice device) != 0) return null;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o);
            return (IAudioEndpointVolume)o;
        }
        catch
        {
            return null; // нет устройств вывода
        }
    }

    private IAudioMeterInformation? _meter;
    private DateTime _meterAt;

    /// <summary>Текущий пик звука 0..1 на устройстве по умолчанию — для «живой» пилюли.</summary>
    public float Peak
    {
        get
        {
            try
            {
                if (_meter == null || (DateTime.Now - _meterAt).TotalSeconds > 3)
                {
                    _meterAt = DateTime.Now;
                    _meter = null;
                    if (_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out IMMDevice device) == 0)
                    {
                        Guid iid = typeof(IAudioMeterInformation).GUID;
                        device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o);
                        _meter = (IAudioMeterInformation)o;
                    }
                }
                if (_meter == null) return 0;
                _meter.GetPeakValue(out float peak);
                return peak;
            }
            catch
            {
                _meter = null;
                return 0;
            }
        }
    }

    /// <summary>Громкость 0..1, или -1, если устройства нет.</summary>
    public float Volume
    {
        get
        {
            IAudioEndpointVolume? v = GetVolume();
            if (v == null) return -1;
            v.GetMasterVolumeLevelScalar(out float level);
            return level;
        }
        set
        {
            IAudioEndpointVolume? v = GetVolume();
            if (v == null) return;
            v.SetMasterVolumeLevelScalar(Math.Clamp(value, 0f, 1f), ref _empty);
            if (value > 0) v.SetMute(false, ref _empty);
        }
    }

    public bool Muted
    {
        get
        {
            IAudioEndpointVolume? v = GetVolume();
            if (v == null) return false;
            v.GetMute(out bool muted);
            return muted;
        }
        set => GetVolume()?.SetMute(value, ref _empty);
    }

    public void Step(float delta) => Volume = Math.Clamp(Volume + delta, 0f, 1f);

    public List<AudioDevice> GetOutputDevices()
    {
        var list = new List<AudioDevice>();
        try
        {
            string? defaultId = null;
            if (_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out IMMDevice def) == 0)
                def.GetId(out defaultId);

            _enumerator.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out IMMDeviceCollection devices);
            devices.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                devices.Item(i, out IMMDevice d);
                d.GetId(out string id);
                list.Add(new AudioDevice(id, FriendlyName(d), id == defaultId));
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
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static string FriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store);
            var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
            store.GetValue(ref key, out PROPVARIANT value);
            string? name = value.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(value.pointerValue) : null;
            PropVariantClear(ref value);
            return name ?? "Устройство";
        }
        catch
        {
            return "Устройство";
        }
    }

    // ---------- COM ----------

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public int pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr pointerValue;
        public IntPtr extra;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
        [PreserveSig] int GetMeteringChannelCount(out uint count);
        [PreserveSig] int GetChannelsPeakValues(uint count, IntPtr peaks);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
    }

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

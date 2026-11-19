using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace WikarIsland.Services;

public enum BatteryDeviceKind { Headset, Mouse, Keyboard, Gamepad, Other }

/// <summary>Заряд одного устройства. Source — откуда взяли: «JBL», «Windows», «HeadsetControl».</summary>
public sealed record DeviceBattery(string Id, string Name, BatteryDeviceKind Kind, int Percent, bool Charging, string Source)
{
    public string KindWord => Kind switch
    {
        BatteryDeviceKind.Headset => "наушники",
        BatteryDeviceKind.Mouse => "мышь",
        BatteryDeviceKind.Keyboard => "клавиатура",
        BatteryDeviceKind.Gamepad => "геймпад",
        _ => "устройство",
    };

    public string Glyph => Kind switch
    {
        BatteryDeviceKind.Headset => "",
        BatteryDeviceKind.Mouse => "",
        BatteryDeviceKind.Keyboard => "",
        BatteryDeviceKind.Gamepad => "",
        _ => "",
    };
}

/// <summary>
/// Заряд наушников, мышей и прочих беспроводных устройств. Источники по очереди:
/// 1) свои профили устройств, которые не отдают заряд системе (JBL Quantum 350 — напрямую с донгла);
/// 2) Windows — Bluetooth-устройства, у которых система сама знает процент;
/// 3) HeadsetControl (лежит рядом с программой) — гарнитуры Logitech, SteelSeries, HyperX, Corsair и др.
/// На 20, 10 и 5% Капсулка один раз голосом просит поставить устройство на зарядку.
/// </summary>
public sealed class DeviceBatteryService : IDisposable
{
    private static readonly int[] Thresholds = { 20, 10, 5 };

    private readonly object _lock = new();
    private readonly Dictionary<string, int> _warned = new(); // id → последний порог, о котором уже сказали
    private Timer? _timer;
    private int _polling;

    public IReadOnlyList<DeviceBattery> Devices { get; private set; } = Array.Empty<DeviceBattery>();
    public event Action? Changed;
    /// <summary>Заряд опустился до порога (20/10/5). Вызывается не из UI-потока.</summary>
    public event Action<DeviceBattery, int>? Low;

    public static string HeadsetControlPath => Path.Combine(AppContext.BaseDirectory, "headsetcontrol", "headsetcontrol.exe");
    public static bool HeadsetControlAvailable => File.Exists(HeadsetControlPath);

    public void Start()
    {
        lock (_lock)
        {
            _timer ??= new Timer(_ => Poll(), null, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(60));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
        Devices = Array.Empty<DeviceBattery>();
        Changed?.Invoke();
    }

    /// <summary>Опросить сейчас (кнопка «Обновить», голосовой вопрос).</summary>
    public Task RefreshAsync() => Task.Run(Poll);

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            var list = new List<DeviceBattery>();
            Collect(list, JblQuantum.Read);
            Collect(list, WindowsBattery.Read);
            if (HeadsetControlAvailable) Collect(list, HeadsetControl.Read);

            // одно устройство из двух источников — оставляем первый (свой профиль точнее)
            var unique = new List<DeviceBattery>();
            foreach (DeviceBattery d in list)
                if (!unique.Any(u => u.Id == d.Id || SameName(u.Name, d.Name))) unique.Add(d);

            bool changed = unique.Count != Devices.Count || unique.Zip(Devices).Any(p => p.First != p.Second);
            Devices = unique;
            if (changed) Changed?.Invoke();
            CheckLow(unique);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private static void Collect(List<DeviceBattery> list, Func<IEnumerable<DeviceBattery>> source)
    {
        try { list.AddRange(source()); }
        catch (Exception ex) { App.LogError(new InvalidOperationException("Заряд устройств: источник не ответил", ex)); }
    }

    private static bool SameName(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase));

    private void CheckLow(List<DeviceBattery> devices)
    {
        List<(DeviceBattery, int)> fire = new();
        lock (_warned)
        {
            foreach (DeviceBattery d in devices)
            {
                // зарядили (или поставили на зарядку) — предупреждения снова в силе
                if (d.Charging || d.Percent > Thresholds[0] + 5)
                {
                    _warned.Remove(d.Id);
                    continue;
                }
                int crossed = Thresholds.Where(t => d.Percent <= t).DefaultIfEmpty(0).Min();
                if (crossed == 0) continue;
                int last = _warned.TryGetValue(d.Id, out int w) ? w : int.MaxValue;
                if (crossed < last)
                {
                    _warned[d.Id] = crossed;
                    fire.Add((d, crossed));
                }
            }
        }
        foreach ((DeviceBattery d, int t) in fire) Low?.Invoke(d, t);
    }

    /// <summary>Фраза для голосового ответа «сколько заряда».</summary>
    public string Describe(string? only = null)
    {
        IEnumerable<DeviceBattery> list = Devices;
        if (only == "headset") list = list.Where(d => d.Kind == BatteryDeviceKind.Headset);
        else if (only == "mouse") list = list.Where(d => d.Kind == BatteryDeviceKind.Mouse);
        var items = list.ToList();
        if (items.Count == 0)
            return only switch
            {
                "headset" => "Заряд наушников не вижу, сэр. Они включены?",
                "mouse" => "Заряд мыши не вижу, сэр. Она не отдаёт его системе.",
                _ => "Не вижу ни одного устройства с батареей, сэр.",
            };
        return string.Join(" ", items.Select(d =>
            $"{Capital(d.KindWord)} {d.Name}: {d.Percent} {Percent(d.Percent)}{(d.Charging ? ", заряжается" : "")}."));
    }

    public static string Percent(int n)
    {
        int a = n % 100, b = n % 10;
        if (a is >= 11 and <= 14) return "процентов";
        return b switch { 1 => "процент", 2 or 3 or 4 => "процента", _ => "процентов" };
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    public void Dispose() => Stop();

    // ================= JBL Quantum (свой профиль) =================

    /// <summary>
    /// JBL Quantum 350: заряд не уходит в Windows, его знает только донгл. Запрос — HID Feature Report 0x49,
    /// процент — во втором байте ответа. Состояние связи с наушниками — отчёт 0x68 (0 — наушники выключены).
    /// </summary>
    internal static class JblQuantum
    {
        private const string Vid = "0ecb";

        private static readonly (string pid, string name)[] Models =
        {
            ("206b", "JBL Quantum 350"),
            ("20b2", "JBL Quantum 350"),
            ("2047", "JBL Quantum 350"),
        };

        public static IEnumerable<DeviceBattery> Read()
        {
            var result = new List<DeviceBattery>();
            foreach (string path in Hid.Paths(Vid))
            {
                string lower = path.ToLowerInvariant();
                (string pid, string name) model = Models.FirstOrDefault(m => lower.Contains("pid_" + m.pid));
                if (model.pid == null) continue;
                string id = "jbl:" + model.pid;
                if (result.Any(r => r.Id == id)) continue;

                using SafeFileHandle h = Hid.Open(path);
                if (h.IsInvalid) continue;
                int len = Hid.FeatureLength(h);
                if (len < 2) continue;

                byte[]? data = Hid.GetFeature(h, 0x49, len);
                if (data == null || data[1] > 100) continue;
                // донгл есть, а наушники выключены: связь 0 и пустой заряд — не показываем
                byte[]? link = Hid.GetFeature(h, 0x68, len);
                if (data[1] == 0 && (link == null || link[1] == 0)) continue;
                result.Add(new DeviceBattery(id, model.name, BatteryDeviceKind.Headset, data[1], false, "JBL"));
            }
            return result;
        }
    }

    // ================= Windows (Bluetooth) =================

    /// <summary>Процент, который Windows сама знает о Bluetooth-устройствах (свойство DEVPKEY_Bluetooth_Battery).</summary>
    internal static class WindowsBattery
    {
        private static readonly DEVPROPKEY BatteryKey = new() { fmtid = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), pid = 2 };
        private static readonly DEVPROPKEY FriendlyNameKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        private static readonly DEVPROPKEY DescKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
        private static readonly DEVPROPKEY ClassKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 9 };

        public static IEnumerable<DeviceBattery> Read()
        {
            var result = new List<DeviceBattery>();
            IntPtr set = SetupDiGetClassDevsW(IntPtr.Zero, null, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (set == new IntPtr(-1)) return result;
            try
            {
                var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref info); i++)
                {
                    byte[]? b = GetProp(set, ref info, BatteryKey);
                    if (b == null || b.Length < 1 || b[0] > 100) continue;
                    string name = GetString(set, ref info, FriendlyNameKey) ?? GetString(set, ref info, DescKey) ?? "Bluetooth-устройство";
                    string cls = GetString(set, ref info, ClassKey) ?? "";
                    string id = "win:" + name;
                    if (result.Any(r => r.Id == id)) continue;
                    result.Add(new DeviceBattery(id, name, Guess(name, cls), b[0], false, "Windows"));
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        private static BatteryDeviceKind Guess(string name, string cls)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("mouse") || n.Contains("мыш") || n.Contains("mx master") || n.Contains("mx anywhere")) return BatteryDeviceKind.Mouse;
            if (n.Contains("keyboard") || n.Contains("клавиат") || n.Contains("keys")) return BatteryDeviceKind.Keyboard;
            if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("xbox")) return BatteryDeviceKind.Gamepad;
            if (n.Contains("buds") || n.Contains("pods") || n.Contains("head") || n.Contains("наушн") || n.Contains("wh-") || n.Contains("wf-")
                || n.Contains("jbl") || n.Contains("airpods") || n.Contains("audio") || cls.Contains("AudioEndpoint", StringComparison.OrdinalIgnoreCase))
                return BatteryDeviceKind.Headset;
            return BatteryDeviceKind.Other;
        }

        private static byte[]? GetProp(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
        {
            DEVPROPKEY k = key;
            SetupDiGetDevicePropertyW(set, ref info, ref k, out _, null, 0, out int size, 0);
            if (size <= 0 || size > 4096) return null;
            var buf = new byte[size];
            return SetupDiGetDevicePropertyW(set, ref info, ref k, out _, buf, size, out _, 0) ? buf : null;
        }

        private static string? GetString(IntPtr set, ref SP_DEVINFO_DATA info, DEVPROPKEY key)
        {
            byte[]? b = GetProp(set, ref info, key);
            if (b == null) return null;
            string s = System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0');
            return s.Length > 0 ? s : null;
        }

        private const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY { public Guid fmtid; public int pid; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr hwnd, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA info);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA info, ref DEVPROPKEY key,
            out int type, byte[]? buffer, int size, out int required, int flags);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }

    // ================= HeadsetControl =================

    /// <summary>HeadsetControl (github.com/Sapd/HeadsetControl) — отдельная программа, лежит в папке headsetcontrol.</summary>
    internal static class HeadsetControl
    {
        public static IEnumerable<DeviceBattery> Read()
        {
            var result = new List<DeviceBattery>();
            var psi = new ProcessStartInfo(HeadsetControlPath, "-b --output JSON")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process? p = Process.Start(psi);
            if (p == null) return result;
            Task<string> output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(6000))
            {
                try { p.Kill(); } catch { }
                return result;
            }
            string json = output.Result.Trim();
            int start = json.IndexOf('{');
            if (start < 0) return result;

            using JsonDocument doc = JsonDocument.Parse(json[start..]);
            if (!doc.RootElement.TryGetProperty("devices", out JsonElement devices) || devices.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement dev in devices.EnumerateArray())
            {
                string name = Str(dev, "device") ?? Str(dev, "product") ?? "Гарнитура";
                if (!dev.TryGetProperty("battery", out JsonElement bat) || bat.ValueKind != JsonValueKind.Object) continue;
                string status = Str(bat, "status") ?? "";
                if (status.Contains("UNAVAILABLE", StringComparison.OrdinalIgnoreCase)) continue;
                if (!bat.TryGetProperty("level", out JsonElement lv) || !lv.TryGetInt32(out int level) || level < 0 || level > 100) continue;
                bool charging = status.Contains("CHARGING", StringComparison.OrdinalIgnoreCase);
                result.Add(new DeviceBattery("hsc:" + name, name, BatteryDeviceKind.Headset, level, charging, "HeadsetControl"));
            }
            return result;
        }

        private static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // ================= HID =================

    internal static class Hid
    {
        public static List<string> Paths(string vid)
        {
            var res = new List<string>();
            HidD_GetHidGuid(out Guid g);
            IntPtr set = SetupDiGetClassDevs(ref g, IntPtr.Zero, IntPtr.Zero, 0x12); // PRESENT | DEVICEINTERFACE
            if (set == new IntPtr(-1)) return res;
            try
            {
                var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref g, i, ref d); i++)
                {
                    SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int req, IntPtr.Zero);
                    if (req <= 0) continue;
                    IntPtr buf = Marshal.AllocHGlobal(req);
                    try
                    {
                        Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, req, out _, IntPtr.Zero))
                        {
                            string? p = Marshal.PtrToStringUni(buf + 4);
                            if (p != null && p.Contains("vid_" + vid, StringComparison.OrdinalIgnoreCase)) res.Add(p);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buf);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return res;
        }

        public static SafeFileHandle Open(string path)
        {
            SafeFileHandle h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
            return CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        }

        public static int FeatureLength(SafeFileHandle h)
        {
            if (!HidD_GetPreparsedData(h, out IntPtr pd)) return 0;
            try
            {
                var caps = new HIDP_CAPS();
                return HidP_GetCaps(pd, ref caps) == 0x110000 ? caps.FeatureReportByteLength : 0;
            }
            finally
            {
                HidD_FreePreparsedData(pd);
            }
        }

        public static byte[]? GetFeature(SafeFileHandle h, byte reportId, int len)
        {
            var b = new byte[len];
            b[0] = reportId;
            return HidD_GetFeature(h, b, len) && b[0] == reportId ? b : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid g);
        [DllImport("hid.dll", SetLastError = true)] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
        [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pd);
        [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr pd);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr pd, ref HIDP_CAPS caps);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid g, IntPtr e, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr di, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA d);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int req, IntPtr di);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using LibreHardwareMonitor.Hardware;

namespace WikarIsland.Services;

public sealed class ChipStats
{
    public string Name { get; init; } = "";
    public double? Load { get; init; }        // %
    public double? Temperature { get; init; } // °C
    public double? ClockMHz { get; init; }
    public double? Power { get; init; }       // Вт
    public double? MemoryUsedGb { get; init; }
    public double? MemoryTotalGb { get; init; }
}

public sealed record TempSensor(string Group, string Name, double Celsius);

public sealed class SystemSnapshot
{
    public ChipStats? Cpu { get; init; }
    public ChipStats? Gpu { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    public List<TempSensor> Temperatures { get; init; } = new();
    public bool IsAdmin { get; init; }
}

/// <summary>
/// Показатели ПК через LibreHardwareMonitor. Опрос идёт в фоне и только пока открыта вкладка «Система».
/// Температура процессора доступна лишь с правами администратора и драйвером PawnIO.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private readonly object _lock = new();
    private Computer? _computer;
    private Timer? _timer;
    private bool _opening;

    public SystemSnapshot? Current { get; private set; }
    public event Action? Updated;

    public static bool IsAdmin
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_timer != null) return;
            _timer = new Timer(_ => Poll(), null, 0, 1500);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Poll()
    {
        if (!Monitor.TryEnter(_lock)) return; // прошлый опрос ещё идёт
        try
        {
            if (_computer == null)
            {
                if (_opening) return;
                _opening = true;
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                };
                _computer.Open(); // первый раз — пара секунд
            }

            foreach (IHardware hw in _computer.Hardware) UpdateDeep(hw);
            Current = Read(_computer);
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    private static void UpdateDeep(IHardware hw)
    {
        hw.Update();
        foreach (IHardware sub in hw.SubHardware) UpdateDeep(sub);
    }

    private static IEnumerable<ISensor> AllSensors(IHardware hw) =>
        hw.Sensors.Concat(hw.SubHardware.SelectMany(AllSensors));

    private static double? Value(IEnumerable<ISensor> sensors, SensorType type, params string[] names)
    {
        foreach (string name in names)
        {
            ISensor? s = sensors.FirstOrDefault(x => x.SensorType == type && x.Value.HasValue
                                                     && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (s != null) return s.Value;
        }
        return null;
    }

    private static SystemSnapshot Read(Computer computer)
    {
        var temps = new List<TempSensor>();
        ChipStats? cpu = null, gpu = null;
        double ramUsed = 0, ramTotal = 0;

        foreach (IHardware hw in computer.Hardware)
        {
            List<ISensor> sensors = AllSensors(hw).ToList();

            foreach (ISensor t in sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value is > 0 and < 150))
            {
                // у дисков и процессоров датчиков много — оставляем понятные
                if (hw.HardwareType is HardwareType.Cpu && t.Name.StartsWith("Core #", StringComparison.Ordinal) && t.Name.Contains("Distance")) continue;
                temps.Add(new TempSensor(hw.Name, t.Name, t.Value!.Value));
            }

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu when cpu == null:
                {
                    double? temp = Value(sensors, SensorType.Temperature, "Core (Tctl/Tdie)", "CPU Package", "Core (Tdie)", "Core (Tctl)", "Core Average", "Core Max");
                    temp ??= sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value > 0).Select(s => (double?)s.Value).Max();
                    List<double> clocks = sensors
                        .Where(s => s.SensorType == SensorType.Clock && s.Name.StartsWith("Core", StringComparison.Ordinal) && s.Value > 0)
                        .Select(s => (double)s.Value!.Value).ToList();
                    cpu = new ChipStats
                    {
                        Name = hw.Name,
                        Load = Value(sensors, SensorType.Load, "CPU Total"),
                        Temperature = temp,
                        ClockMHz = clocks.Count > 0 ? clocks.Max() : null,
                        Power = Value(sensors, SensorType.Power, "Package", "CPU Package"),
                    };
                    break;
                }

                case HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
                    when gpu == null || (gpu.Name.Contains("Intel") && hw.HardwareType != HardwareType.GpuIntel):
                {
                    double? usedMb = Value(sensors, SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
                    double? totalMb = Value(sensors, SensorType.SmallData, "GPU Memory Total");
                    gpu = new ChipStats
                    {
                        Name = hw.Name,
                        Load = Value(sensors, SensorType.Load, "GPU Core", "D3D 3D"),
                        Temperature = Value(sensors, SensorType.Temperature, "GPU Core", "GPU Hot Spot"),
                        ClockMHz = Value(sensors, SensorType.Clock, "GPU Core"),
                        Power = Value(sensors, SensorType.Power, "GPU Package", "GPU Power", "GPU Core"),
                        MemoryUsedGb = usedMb / 1024,
                        MemoryTotalGb = totalMb / 1024,
                    };
                    break;
                }

                case HardwareType.Memory when ramTotal == 0:
                {
                    double used = Value(sensors, SensorType.Data, "Memory Used") ?? 0;
                    double free = Value(sensors, SensorType.Data, "Memory Available") ?? 0;
                    ramUsed = used;
                    ramTotal = used + free;
                    break;
                }
            }
        }

        return new SystemSnapshot
        {
            Cpu = cpu,
            Gpu = gpu,
            RamUsedGb = ramUsed,
            RamTotalGb = ramTotal,
            Temperatures = temps,
            IsAdmin = IsAdmin,
        };
    }

    /// <summary>Перезапустить остров с правами администратора (Windows спросит разрешение).</summary>
    public static bool RestartAsAdmin()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return false;
            // новый экземпляр дождётся, пока этот закроется (иначе его остановит защита от двойного запуска)
            Process.Start(new ProcessStartInfo(exe, $"--wait-for {Environment.ProcessId}") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch
        {
            return false; // отказались в окне UAC
        }
    }

    public void Dispose()
    {
        Stop();
        lock (_lock)
        {
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}

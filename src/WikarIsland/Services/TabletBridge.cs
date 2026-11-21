using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Связь с Capsul Box — панелью на Android-планшете. Агент Capsul Box слушает на этом компьютере
/// (http://127.0.0.1:8765), Капсула отдаёт ему то, что знает сама: пока заряд устройств (JBL через QuantumENGINE,
/// HeadsetControl, Bluetooth). Если агент не запущен, ничего не происходит.
/// </summary>
public sealed class TabletBridge : IDisposable
{
    private const string Agent = "http://127.0.0.1:8765";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private readonly DeviceBatteryService _batteries;
    private readonly Timer _timer;
    private int _sending;

    public TabletBridge(DeviceBatteryService batteries)
    {
        _batteries = batteries;
        _batteries.Changed += SendSoon;
        // агент держит список 90 с — обновляем чаще, даже если заряд не менялся
        _timer = new Timer(_ => SendSoon(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
    }

    private void SendSoon() => _ = SendBatteriesAsync();

    private async Task SendBatteriesAsync()
    {
        if (Interlocked.Exchange(ref _sending, 1) == 1) return;
        try
        {
            var devices = _batteries.Devices.Select(d => new
            {
                name = d.Name,
                level = d.Percent,
                kind = d.Kind switch
                {
                    BatteryDeviceKind.Headset => "headphones",
                    BatteryDeviceKind.Mouse => "mouse",
                    BatteryDeviceKind.Keyboard => "keyboard",
                    BatteryDeviceKind.Gamepad => "controller",
                    _ => "other",
                },
                charging = d.Charging,
            }).ToList();
            using var r = await Http.PostAsJsonAsync(Agent + "/api/capsula/batteries", new { devices });
        }
        catch { /* агент не запущен или старый — панели нет, это нормально */ }
        finally { Interlocked.Exchange(ref _sending, 0); }
    }

    public void Dispose()
    {
        _batteries.Changed -= SendSoon;
        _timer.Dispose();
    }
}

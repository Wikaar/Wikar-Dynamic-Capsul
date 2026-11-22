using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>
/// Связь с Capsul Box — панелью на Android-планшете. Агент Capsul Box слушает на этом компьютере
/// (http://127.0.0.1:8775), Капсула отдаёт ему то, что знает сама: заряд устройств (JBL через QuantumENGINE,
/// HeadsetControl, Bluetooth), FPS игры и её начало/конец — тогда планшет сам открывает страницу «Игра» и потом
/// возвращается. Если агент не запущен, ничего не происходит.
/// </summary>
public sealed class TabletBridge : IDisposable
{
    private const string Agent = "http://127.0.0.1:8775";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private const string GameRule = "capsula-game";

    private readonly DeviceBatteryService _batteries;
    private readonly GameService _game;
    private readonly Timer _timer;
    private readonly Timer _gameTimer;
    private int _sending;
    private int _gameSending;
    private bool _wasPlaying;
    private DateTime _idleSentAt = DateTime.MinValue;

    public TabletBridge(DeviceBatteryService batteries, GameService game)
    {
        _batteries = batteries;
        _game = game;
        _batteries.Changed += SendSoon;
        // агент держит список 90 с — обновляем чаще, даже если заряд не менялся
        _timer = new Timer(_ => SendSoon(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));
        _gameTimer = new Timer(_ => _ = SendGameAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
    }

    /// <summary>Раз в секунду: FPS в плитку (живёт 5 с), а при начале и конце игры — переход страницы на планшете.</summary>
    private async Task SendGameAsync()
    {
        if (Interlocked.Exchange(ref _gameSending, 1) == 1) return;
        try
        {
            bool playing = _game.Active;
            if (playing)
            {
                var fps = _game.Fps;
                await Post("/api/push/fps?ttl=5", new
                {
                    title = "FPS",
                    value = fps is { } f ? (object)(int)Math.Round(f) : "…",
                    sub = string.IsNullOrWhiteSpace(_game.Name) ? null : _game.Name,
                });
                if (!_wasPlaying)
                    await Post("/api/goto/" + Uri.EscapeDataString("Игра") + "?countdown=0&hold=true&rule=" + GameRule + "&name=" + Uri.EscapeDataString("Игра"), new { });
            }
            else
            {
                if (_wasPlaying) await Post("/api/back/" + GameRule, new { });
                if (_wasPlaying || DateTime.Now - _idleSentAt > TimeSpan.FromSeconds(60))
                {
                    await Post("/api/push/fps?ttl=180", new { title = "FPS", value = "—", sub = "нет игры" });
                    _idleSentAt = DateTime.Now;
                }
            }
            _wasPlaying = playing;
        }
        catch { /* агента нет — попробуем через секунду */ }
        finally { Interlocked.Exchange(ref _gameSending, 0); }
    }

    private static async Task Post(string path, object body)
    {
        using var r = await Http.PostAsJsonAsync(Agent + path, body);
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
            await Post("/api/capsula/batteries", new { devices });
        }
        catch { /* агент не запущен или старый — панели нет, это нормально */ }
        finally { Interlocked.Exchange(ref _sending, 0); }
    }

    public void Dispose()
    {
        _batteries.Changed -= SendSoon;
        _timer.Dispose();
        _gameTimer.Dispose();
    }
}

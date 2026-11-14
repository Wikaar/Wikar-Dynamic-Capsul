using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WikarIsland.Services;

public enum PowerAction
{
    Shutdown,
    Restart,
    Sleep,
    Hibernate,
}

/// <summary>Таймер выключения/перезагрузки/сна ПК, в том числе «после текущего трека».</summary>
public sealed class PowerTimerService
{
    public static readonly TimeSpan WarningTime = TimeSpan.FromSeconds(60);

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string? _trackKey;
    private DateTime? _stoppedSince;

    public PowerAction Action { get; private set; } = PowerAction.Shutdown;
    public DateTime? Target { get; private set; }
    public bool AfterTrack { get; private set; }
    public bool Active => Target != null || AfterTrack;
    public TimeSpan Remaining => Target is DateTime t ? (t - DateTime.Now < TimeSpan.Zero ? TimeSpan.Zero : t - DateTime.Now) : TimeSpan.Zero;
    public bool InWarning => Target != null && Remaining <= WarningTime;

    public event Action? Changed;

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    public PowerTimerService()
    {
        _timer.Tick += (_, _) => Tick();
    }

    public static string ActionName(PowerAction a) => a switch
    {
        PowerAction.Restart => "Перезагрузка",
        PowerAction.Sleep => "Сон",
        PowerAction.Hibernate => "Гибернация",
        _ => "Выключение",
    };

    public void StartIn(TimeSpan delay, PowerAction action)
    {
        Action = action;
        AfterTrack = false;
        Target = DateTime.Now + delay;
        _timer.Start();
        Changed?.Invoke();
    }

    public void StartAfterTrack(PowerAction action)
    {
        Action = action;
        Target = null;
        AfterTrack = true;
        _trackKey = TrackKey();
        _stoppedSince = null;
        _timer.Start();
        Changed?.Invoke();
    }

    public void AddMinutes(int minutes)
    {
        if (Target == null) return;
        Target = Target.Value.AddMinutes(minutes);
        Changed?.Invoke();
    }

    public void Cancel()
    {
        Target = null;
        AfterTrack = false;
        _timer.Stop();
        Changed?.Invoke();
    }

    private static string? TrackKey()
    {
        MediaInfo? m = App.Media.Current;
        return m == null ? null : m.AppId + "|" + m.Title + "|" + m.Artist;
    }

    private void Tick()
    {
        if (AfterTrack)
        {
            MediaInfo? m = App.Media.Current;
            string? key = TrackKey();
            bool playing = m?.IsPlaying == true;
            _stoppedSince = playing ? null : _stoppedSince ?? DateTime.Now;

            bool trackChanged = _trackKey != null && key != _trackKey;
            bool stopped = _stoppedSince != null && (DateTime.Now - _stoppedSince.Value).TotalSeconds > 5;
            if (_trackKey == null) _trackKey = key;

            if (trackChanged || stopped)
            {
                if (trackChanged && playing) App.Media.PlayPause(); // следующий трек не нужен
                AfterTrack = false;
                Target = DateTime.Now + WarningTime; // минута на отмену
            }
        }

        if (Target != null && DateTime.Now >= Target.Value)
        {
            PowerAction action = Action;
            Cancel();
            Execute(action);
            return;
        }

        Changed?.Invoke();
    }

    private static void Execute(PowerAction action)
    {
        try
        {
            switch (action)
            {
                case PowerAction.Shutdown:
                    Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    break;
                case PowerAction.Restart:
                    Process.Start(new ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    break;
                case PowerAction.Sleep:
                    SetSuspendState(false, false, false);
                    break;
                case PowerAction.Hibernate:
                    SetSuspendState(true, false, false);
                    break;
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}

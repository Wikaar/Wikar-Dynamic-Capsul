using System;
using System.IO;
using System.Media;

namespace WikarIsland.Services;

/// <summary>Обычный таймер обратного отсчёта и Pomodoro (работа → перерыв, каждый 4-й перерыв длинный).</summary>
public sealed class FocusTimerService
{
    public string Label { get; private set; } = "";
    public TimeSpan Total { get; private set; }
    public bool Pomodoro { get; private set; }
    public bool IsBreak { get; private set; }

    /// <summary>Номер рабочего отрезка в цикле Pomodoro (1–4).</summary>
    public int Round { get; private set; }

    private DateTime? _end;
    private TimeSpan? _pausedLeft;

    public bool Active => _end != null || _pausedLeft != null;
    public bool Paused => _pausedLeft != null;

    public TimeSpan Remaining =>
        _pausedLeft ?? (_end is DateTime e && e > DateTime.Now ? e - DateTime.Now : TimeSpan.Zero);

    /// <summary>Прошедшая доля 0…1 — для кольца прогресса.</summary>
    public double Progress => Total.TotalSeconds > 0 ? Math.Clamp(1 - Remaining.TotalSeconds / Total.TotalSeconds, 0, 1) : 0;

    public event Action? Changed;

    /// <summary>Таймер дошёл до нуля: заголовок и текст карточки.</summary>
    public event Action<string, string>? Finished;

    public void Start(TimeSpan duration, string label = "")
    {
        if (duration <= TimeSpan.Zero) return;
        Pomodoro = false;
        IsBreak = false;
        Begin(duration, label.Length > 0 ? label : "Таймер");
    }

    public void StartPomodoro()
    {
        Pomodoro = true;
        IsBreak = false;
        Round = 1;
        Begin(TimeSpan.FromMinutes(App.Settings.PomodoroWork), "Фокус 1/4");
    }

    private void Begin(TimeSpan duration, string label)
    {
        Total = duration;
        Label = label;
        _end = DateTime.Now + duration;
        _pausedLeft = null;
        Changed?.Invoke();
    }

    public void Stop()
    {
        _end = null;
        _pausedLeft = null;
        Pomodoro = false;
        Changed?.Invoke();
    }

    public void TogglePause()
    {
        if (!Active) return;
        if (_pausedLeft is TimeSpan left)
        {
            _end = DateTime.Now + left;
            _pausedLeft = null;
        }
        else
        {
            _pausedLeft = Remaining;
            _end = null;
        }
        Changed?.Invoke();
    }

    public void AddMinutes(int minutes)
    {
        if (!Active) return;
        TimeSpan add = TimeSpan.FromMinutes(minutes);
        if (_pausedLeft is TimeSpan left) _pausedLeft = left + add;
        else _end += add;
        Total += add;
        Changed?.Invoke();
    }

    /// <summary>Вызывается раз в секунду таймером капсулы.</summary>
    public void Tick()
    {
        if (_end is not DateTime end || DateTime.Now < end) return;

        if (!Pomodoro)
        {
            string label = Label;
            Stop();
            PlayChime();
            Finished?.Invoke("Время вышло", label == "Таймер" ? $"Прошло {Format(Total)}" : $"{label} · {Format(Total)}");
            return;
        }

        AppSettings s = App.Settings;
        PlayChime();
        if (!IsBreak)
        {
            bool longBreak = Round % 4 == 0;
            IsBreak = true;
            Begin(TimeSpan.FromMinutes(longBreak ? s.PomodoroLong : s.PomodoroBreak), longBreak ? "Длинный перерыв" : "Перерыв");
            Finished?.Invoke("Отличная работа!", $"Теперь {(longBreak ? s.PomodoroLong : s.PomodoroBreak)} минут отдыха");
        }
        else
        {
            IsBreak = false;
            Round = Round % 4 + 1;
            Begin(TimeSpan.FromMinutes(s.PomodoroWork), $"Фокус {Round}/4");
            Finished?.Invoke("Перерыв окончен", $"Следующие {s.PomodoroWork} минут — работа");
        }
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    public static void PlayChime()
    {
        try
        {
            string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
            foreach (string name in new[] { "Windows Notify Calendar.wav", "Alarm01.wav", "notify.wav" })
            {
                string path = Path.Combine(media, name);
                if (!File.Exists(path)) continue;
                new SoundPlayer(path).Play();
                return;
            }
            SystemSounds.Asterisk.Play();
        }
        catch
        {
            // без звука
        }
    }
}

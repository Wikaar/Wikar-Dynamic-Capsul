using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WikarIsland.Services;

public sealed class Reminder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime Due { get; set; }

    /// <summary>none, daily, weekdays, weekly.</summary>
    public string Repeat { get; set; } = "none";

    public string RepeatName => Repeat switch
    {
        "daily" => "каждый день",
        "weekdays" => "по будням",
        "weekly" => "каждую неделю",
        _ => "",
    };
}

/// <summary>Свои напоминания: «в 18:30», «через 20 минут», «каждый день в 9:00». Хранятся в reminders.json.</summary>
public sealed class ReminderService
{
    private static string FilePath => Path.Combine(AppSettings.DataDir, "reminders.json");

    public List<Reminder> Items { get; private set; } = new();

    public event Action? Changed;
    public event Action<Reminder>? Fired;

    public ReminderService()
    {
        try
        {
            if (File.Exists(FilePath))
                Items = JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch
        {
            Items = new();
        }
    }

    public Reminder Add(string text, DateTime due, string repeat = "none")
    {
        var r = new Reminder { Text = text, Due = due, Repeat = repeat };
        Items.Add(r);
        Save();
        return r;
    }

    public void Remove(Reminder r)
    {
        Items.Remove(r);
        Save();
    }

    public void Snooze(Reminder r, int minutes)
    {
        if (!Items.Contains(r)) Items.Add(r);
        r.Due = DateTime.Now.AddMinutes(minutes);
        Save();
    }

    public IEnumerable<Reminder> Upcoming => Items.OrderBy(r => r.Due);

    /// <summary>Раз в секунду: срабатывают наступившие (в том числе пропущенные, пока ПК был выключен).</summary>
    public void Tick()
    {
        DateTime now = DateTime.Now;
        List<Reminder> due = Items.Where(r => r.Due <= now).ToList();
        if (due.Count == 0) return;

        foreach (Reminder r in due)
        {
            Fired?.Invoke(r);
            if (r.Repeat == "none")
            {
                Items.Remove(r);
                continue;
            }
            // повторяющееся — переносим на следующий подходящий день
            while (r.Due <= now)
                r.Due = Next(r.Due, r.Repeat);
        }
        Save();
    }

    private static DateTime Next(DateTime from, string repeat)
    {
        switch (repeat)
        {
            case "weekly":
                return from.AddDays(7);
            case "weekdays":
                DateTime d = from.AddDays(1);
                while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
                return d;
            default:
                return from.AddDays(1);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            if (!AppSettings.Frozen)
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // не записалось — попробуем в следующий раз
        }
        Changed?.Invoke();
    }
}

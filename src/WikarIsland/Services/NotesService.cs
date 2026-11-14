using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace WikarIsland.Services;

public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Text { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;

    public string Title
    {
        get
        {
            string first = Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return first.Length == 0 ? "Новая заметка" : first.Length > 60 ? first[..60] + "…" : first;
        }
    }

    public string Preview
    {
        get
        {
            string rest = string.Join(" ", Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Skip(1));
            return rest.Length > 90 ? rest[..90] + "…" : rest;
        }
    }
}

/// <summary>Заметки в %AppData%\WikarIsland\notes.json.</summary>
public sealed class NotesService
{
    private static string FilePath => Path.Combine(AppSettings.DataDir, "notes.json");

    public List<Note> Notes { get; private set; } = new();

    public NotesService()
    {
        try
        {
            if (File.Exists(FilePath))
                Notes = JsonSerializer.Deserialize<List<Note>>(File.ReadAllText(FilePath)) ?? new List<Note>();
        }
        catch
        {
            Notes = new List<Note>();
        }
    }

    public Note Create()
    {
        var note = new Note();
        Notes.Insert(0, note);
        Save();
        return note;
    }

    public void Delete(Note note)
    {
        Notes.Remove(note);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            // пустые заметки не храним
            List<Note> keep = Notes.Where(n => !string.IsNullOrWhiteSpace(n.Text)).ToList();
            File.WriteAllText(FilePath, JsonSerializer.Serialize(keep, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}

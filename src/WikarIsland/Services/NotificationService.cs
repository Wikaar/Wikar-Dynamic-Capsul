using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;

namespace WikarIsland.Services;

public sealed class NotificationItem
{
    public long Id { get; init; }
    public string AppId { get; init; } = "";
    public string AppName { get; set; } = "";
    public ImageSource? AppIcon { get; set; }
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime Time { get; init; }

    public bool IsDiscord => AppId.Contains("Discord", StringComparison.OrdinalIgnoreCase);

    /// <summary>Открыть программу, приславшую уведомление.</summary>
    public void OpenApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{AppId}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }
}

/// <summary>
/// Уведомления Windows. Обычной программе (не MSIX-пакету) система не даёт подписаться на чужие уведомления,
/// поэтому читаем базу центра уведомлений текущего пользователя
/// (%LocalAppData%\Microsoft\Windows\Notifications\wpndatabase.db) только на чтение и показываем новые записи.
/// </summary>
public sealed class NotificationService
{
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Dictionary<string, (ImageSource? icon, string name)> _apps = new();
    private long _lastId = -1;
    private bool _busy;

    public List<NotificationItem> History { get; } = new();
    public int DiscordUnread { get; private set; }
    public bool Available => File.Exists(DbPath);

    public event Action<NotificationItem>? Arrived;
    public event Action? HistoryChanged;

    public void Start()
    {
        if (!Available) return;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    public void ResetDiscord()
    {
        if (DiscordUnread == 0) return;
        DiscordUnread = 0;
        HistoryChanged?.Invoke();
    }

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            long last = _lastId;
            List<(long id, string appId, string payload, long arrival)> rows = await Task.Run(() => Read(last));

            if (_lastId < 0)
            {
                // первый запуск: старые уведомления не показываем, только запоминаем, где остановились
                _lastId = rows.Count > 0 ? rows.Max(r => r.id) : 0;
                return;
            }

            foreach (var row in rows.OrderBy(r => r.id))
            {
                _lastId = Math.Max(_lastId, row.id);
                NotificationItem? item = Parse(row.id, row.appId, row.payload, row.arrival);
                if (item == null) continue;

                if (!_apps.TryGetValue(item.AppId, out var app))
                {
                    app = await AppIconHelper.GetAsync(item.AppId);
                    _apps[item.AppId] = app;
                }
                item.AppIcon = app.icon;
                item.AppName = string.IsNullOrWhiteSpace(app.name) || app.name == item.AppId
                    ? (item.IsDiscord ? "Discord" : item.AppId.Split('.', '!').Last())
                    : app.name;

                AppSettings s = App.Settings;
                if (!s.KnownNotificationApps.ContainsKey(item.AppId))
                {
                    s.KnownNotificationApps[item.AppId] = item.AppName;
                    s.Save();
                }
                if (!s.NotificationsEnabled || s.MutedNotificationApps.Contains(item.AppId)) continue;

                History.Insert(0, item);
                if (History.Count > 30) History.RemoveAt(History.Count - 1);
                if (item.IsDiscord) DiscordUnread++;

                Arrived?.Invoke(item);
                HistoryChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            // база занята или схема другая — попробуем на следующем круге
            Debug.WriteLine(ex);
        }
        finally
        {
            _busy = false;
        }
    }

    private static List<(long, string, string, long)> Read(long afterId)
    {
        var list = new List<(long, string, string, long)>();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var connection = new SqliteConnection(cs);
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();

        if (afterId < 0)
        {
            cmd.CommandText = "SELECT MAX(Id) FROM Notification";
            object? max = cmd.ExecuteScalar();
            long id = max is long l ? l : 0;
            list.Add((id, "", "", 0));
            return list;
        }

        cmd.CommandText =
            "SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime " +
            "FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
            "WHERE n.Type = 'toast' AND n.Id > $last ORDER BY n.Id LIMIT 20";
        cmd.Parameters.AddWithValue("$last", afterId);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string payload = r.IsDBNull(2) ? "" :
                r.GetFieldType(2) == typeof(byte[]) ? Encoding.UTF8.GetString((byte[])r.GetValue(2)) : r.GetString(2);
            long arrival = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            list.Add((r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1), payload, arrival));
        }
        return list;
    }

    private static NotificationItem? Parse(long id, string appId, string payload, long arrival)
    {
        if (string.IsNullOrWhiteSpace(payload) || string.IsNullOrWhiteSpace(appId)) return null;
        try
        {
            XDocument doc = XDocument.Parse(payload.TrimEnd('\0'));
            List<string> texts = doc.Descendants("text")
                .Select(t => t.Value.Trim())
                .Where(t => t.Length > 0)
                .ToList();
            if (texts.Count == 0) return null;

            DateTime time;
            try { time = arrival > 0 ? DateTime.FromFileTimeUtc(arrival).ToLocalTime() : DateTime.Now; }
            catch { time = DateTime.Now; }

            return new NotificationItem
            {
                Id = id,
                AppId = appId,
                Title = texts[0],
                Body = string.Join(" ", texts.Skip(1)),
                Time = time,
            };
        }
        catch
        {
            return null;
        }
    }
}

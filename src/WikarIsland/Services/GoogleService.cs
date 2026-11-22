using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WikarIsland.Services;

public sealed record CalendarEvent(string Id, string Title, DateTime Start, DateTime End, bool AllDay, string Calendar, string? Location)
{
    public bool IsNow => !AllDay && Start <= DateTime.Now && End > DateTime.Now;

    public string When
    {
        get
        {
            var ru = new CultureInfo("ru-RU");
            string day = Start.Date == DateTime.Today ? "сегодня" : Start.Date == DateTime.Today.AddDays(1) ? "завтра" : Start.ToString("ddd d MMM", ru);
            return AllDay ? day + ", весь день" : $"{day}, {Start:HH:mm}–{End:HH:mm}";
        }
    }
}

public sealed record GoogleTask(string Id, string ListId, string Title, DateTime? Due, string Notes);

/// <summary>
/// Google Календарь и Google Задачи. Вход — своё приложение Google Cloud (Client ID и секрет типа «Компьютер»):
/// открывается обычный браузер, ответ ловим на 127.0.0.1 (встроенные окна Google не пускает).
/// Ключ обновления хранится зашифрованным (DPAPI). События — на 7 дней вперёд, обновляются раз в 5 минут.
/// </summary>
public sealed class GoogleService
{
    private const string Scopes = "openid email https://www.googleapis.com/auth/calendar.readonly https://www.googleapis.com/auth/tasks";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly HashSet<string> _alerted = new();
    private string? _access;
    private DateTime _accessUntil;
    private Timer? _refreshTimer;
    private Timer? _alertTimer;
    private int _refreshing;

    public IReadOnlyList<CalendarEvent> Events { get; private set; } = Array.Empty<CalendarEvent>();
    public IReadOnlyList<GoogleTask> Tasks { get; private set; } = Array.Empty<GoogleTask>();
    public string? LastError { get; private set; }
    public DateTime LastUpdate { get; private set; }

    public bool IsConfigured => App.Settings.GoogleClientId.Length > 0 && App.Settings.GoogleClientSecret.Length > 0;
    public bool IsSignedIn => IsConfigured && App.Settings.GoogleRefresh.Length > 0;

    public event Action? Changed;
    /// <summary>До события осталось 10 минут (или меньше, если узнали позже). Вызывается не из UI-потока.</summary>
    public event Action<CalendarEvent>? EventSoon;

    public void Start()
    {
        if (!IsSignedIn) return;
        _refreshTimer ??= new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(8), TimeSpan.FromMinutes(5));
        _alertTimer ??= new Timer(_ => CheckSoon(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30));
    }

    public void Stop()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _alertTimer?.Dispose();
        _alertTimer = null;
    }

    // ---------- вход ----------

    /// <summary>Вход через браузер. Возвращает текст ошибки или null.</summary>
    public async Task<string?> SignInAsync()
    {
        if (!IsConfigured) return "Сначала вставь Client ID и секрет своего приложения Google.";
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirect = $"http://127.0.0.1:{port}/";
        try
        {
            string url = "https://accounts.google.com/o/oauth2/v2/auth?response_type=code" +
                         "&client_id=" + Uri.EscapeDataString(App.Settings.GoogleClientId) +
                         "&redirect_uri=" + Uri.EscapeDataString(redirect) +
                         "&scope=" + Uri.EscapeDataString(Scopes) +
                         "&code_challenge_method=S256&code_challenge=" + challenge +
                         "&state=" + state + "&access_type=offline&prompt=consent";
            ShellHelper.Open(url);

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            string query;
            while (true)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cts.Token);
                using NetworkStream stream = client.GetStream();
                var buf = new byte[8192];
                int n = await stream.ReadAsync(buf, cts.Token);
                string request = Encoding.ASCII.GetString(buf, 0, n);
                Match line = Regex.Match(request, @"^GET\s+(\S+)");
                string path = line.Success ? line.Groups[1].Value : "";
                bool ours = path.Contains("state=" + state) || path.Contains("error=");
                string html = ours
                    ? "<html><head><meta charset='utf-8'><title>Wikar Dynamic Capsul</title></head><body style='font-family:Segoe UI;background:#111;color:#eee;text-align:center;padding-top:80px'>" +
                      "<h2>Готово, можно закрыть эту вкладку</h2><p>Капсула получила ответ Google.</p></body></html>"
                    : "";
                byte[] body = Encoding.UTF8.GetBytes(html);
                byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ours ? "200 OK" : "404 Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(head, cts.Token);
                await stream.WriteAsync(body, cts.Token);
                if (!ours) continue; // favicon и прочее
                query = path;
                break;
            }

            string error = Regex.Match(query, "[?&]error=([^&]+)").Groups[1].Value;
            if (error.Length > 0)
                return error == "access_denied" ? "Вход отменён в браузере." : "Google отказал во входе: " + Uri.UnescapeDataString(error);
            string code = Regex.Match(query, "[?&]code=([^&]+)").Groups[1].Value;
            if (code.Length == 0) return "Google не вернул код входа.";

            JsonNode? j = await TokenAsync(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = Uri.UnescapeDataString(code),
                ["redirect_uri"] = redirect,
                ["client_id"] = App.Settings.GoogleClientId,
                ["client_secret"] = App.Settings.GoogleClientSecret,
                ["code_verifier"] = verifier,
            });
            if (j?["refresh_token"]?.ToString() is not { Length: > 0 } refresh)
                return "Google не выдал постоянный доступ. Удали доступ приложения в аккаунте Google (Безопасность → Сторонние приложения) и войди ещё раз.";
            App.Settings.GoogleRefresh = refresh;
            App.Settings.GoogleEmail = EmailFromIdToken(j["id_token"]?.ToString()) ?? "";
            App.Settings.Save();
            LastError = null;
            Start();
            await RefreshAsync();
            return null;
        }
        catch (OperationCanceledException)
        {
            return "Не дождался ответа из браузера (5 минут). Попробуй ещё раз.";
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return "Не получилось войти: " + ex.Message;
        }
        finally
        {
            listener.Stop();
        }
    }

    public void SignOut()
    {
        Stop();
        string refresh = App.Settings.GoogleRefresh;
        if (refresh.Length > 0)
            _ = Http.PostAsync("https://oauth2.googleapis.com/revoke?token=" + Uri.EscapeDataString(refresh), new StringContent(""));
        App.Settings.GoogleRefresh = "";
        App.Settings.GoogleEmail = "";
        App.Settings.Save();
        _access = null;
        Events = Array.Empty<CalendarEvent>();
        Tasks = Array.Empty<GoogleTask>();
        Changed?.Invoke();
    }

    private static string? EmailFromIdToken(string? idToken)
    {
        try
        {
            string[] parts = (idToken ?? "").Split('.');
            if (parts.Length < 2) return null;
            string p = parts[1].Replace('-', '+').Replace('_', '/');
            p += new string('=', (4 - p.Length % 4) % 4);
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)))?["email"]?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonNode?> TokenAsync(Dictionary<string, string> form)
    {
        using HttpResponseMessage resp = await Http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        string body = await resp.Content.ReadAsStringAsync();
        JsonNode? j = JsonNode.Parse(body);
        if (!resp.IsSuccessStatusCode)
        {
            string err = j?["error"]?.ToString() ?? ((int)resp.StatusCode).ToString();
            if (form["grant_type"] == "refresh_token" && err == "invalid_grant")
            {
                // отозвали доступ или истёк (приложение в режиме «Тестирование» — 7 дней)
                App.Settings.GoogleRefresh = "";
                App.Settings.Save();
                throw new InvalidOperationException("Вход в Google истёк — войди заново. Чтобы не повторялось каждую неделю, переведи приложение в Google Cloud в режим «В продакшене» (Publish app).");
            }
            throw new InvalidOperationException("Google не выдал доступ: " + (j?["error_description"]?.ToString() ?? err));
        }
        _access = j?["access_token"]?.ToString();
        _accessUntil = DateTime.UtcNow.AddSeconds(((int?)j?["expires_in"] ?? 3600) - 60);
        return j;
    }

    private async Task<string> AccessAsync()
    {
        if (_access != null && DateTime.UtcNow < _accessUntil) return _access;
        if (!IsSignedIn) throw new InvalidOperationException("Сначала войди в Google.");
        await TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = App.Settings.GoogleRefresh,
            ["client_id"] = App.Settings.GoogleClientId,
            ["client_secret"] = App.Settings.GoogleClientSecret,
        });
        return _access ?? throw new InvalidOperationException("Google не выдал доступ.");
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, object? body = null)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessAsync());
        if (body != null) req.Content = JsonContent.Create(body);
        using HttpResponseMessage resp = await Http.SendAsync(req);
        string text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            string msg = JsonNode.Parse(text.Length > 0 ? text : "{}")?["error"]?["message"]?.ToString() ?? ((int)resp.StatusCode).ToString();
            if (msg.Contains("has not been used", StringComparison.OrdinalIgnoreCase) || msg.Contains("is disabled", StringComparison.OrdinalIgnoreCase))
                msg = "В Google Cloud не включён нужный API (Google Calendar API и Google Tasks API — оба).";
            throw new InvalidOperationException(msg);
        }
        return text.Length > 0 ? JsonNode.Parse(text) : null;
    }

    // ---------- календарь и задачи: чтение ----------

    public async Task RefreshAsync()
    {
        if (!IsSignedIn || Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            Events = await LoadEventsAsync();
            Tasks = await LoadTasksAsync();
            LastError = null;
            LastUpdate = DateTime.Now;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            if (ex is not InvalidOperationException) App.LogError(ex);
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
        Changed?.Invoke();
    }

    private async Task<List<CalendarEvent>> LoadEventsAsync()
    {
        var result = new List<CalendarEvent>();
        JsonNode? cals = await SendAsync(HttpMethod.Get, "https://www.googleapis.com/calendar/v3/users/me/calendarList?minAccessRole=reader");
        var calendars = (cals?["items"] as JsonArray ?? new JsonArray())
            .Where(c => c?["selected"]?.GetValue<bool>() == true || c?["primary"]?.GetValue<bool>() == true)
            .Select(c => (id: c!["id"]!.ToString(), name: c["summaryOverride"]?.ToString() ?? c["summary"]?.ToString() ?? ""))
            .Take(12).ToList();
        string from = Uri.EscapeDataString(DateTime.Today.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        string to = Uri.EscapeDataString(DateTime.Today.AddDays(8).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
        foreach ((string id, string name) in calendars)
        {
            JsonNode? list;
            try
            {
                list = await SendAsync(HttpMethod.Get, "https://www.googleapis.com/calendar/v3/calendars/" + Uri.EscapeDataString(id) +
                    $"/events?singleEvents=true&orderBy=startTime&maxResults=50&timeMin={from}&timeMax={to}");
            }
            catch (InvalidOperationException) when (calendars.Count > 1)
            {
                continue; // один календарь недоступен — остальные покажем
            }
            foreach (JsonNode? e in list?["items"] as JsonArray ?? new JsonArray())
            {
                if (e == null || e["status"]?.ToString() == "cancelled") continue;
                // встреча, от которой отказались
                if (e["attendees"] is JsonArray att && att.Any(a => a?["self"]?.GetValue<bool>() == true && a["responseStatus"]?.ToString() == "declined")) continue;
                (DateTime start, bool allDay) = ParseTime(e["start"]);
                (DateTime end, _) = ParseTime(e["end"]);
                if (start == default) continue;
                if (end == default) end = start.AddHours(1);
                if (end <= DateTime.Now) continue;
                string title = e["summary"]?.ToString() is { Length: > 0 } s ? s : "(без названия)";
                result.Add(new CalendarEvent(e["id"]?.ToString() ?? title + start, title, start, end, allDay, name, e["location"]?.ToString()));
            }
        }
        return result.OrderBy(e => e.AllDay ? e.Start.Date : e.Start).ThenBy(e => e.AllDay ? 0 : 1).ToList();
    }

    private static (DateTime, bool allDay) ParseTime(JsonNode? n)
    {
        if (n?["dateTime"]?.ToString() is { Length: > 0 } dt && DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset o))
            return (o.LocalDateTime, false);
        if (n?["date"]?.ToString() is { Length: > 0 } d && DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day))
            return (day, true);
        return (default, false);
    }

    private async Task<List<GoogleTask>> LoadTasksAsync()
    {
        JsonNode? list = await SendAsync(HttpMethod.Get, "https://tasks.googleapis.com/tasks/v1/lists/@default/tasks?showCompleted=false&showHidden=false&maxResults=100");
        var result = new List<GoogleTask>();
        foreach (JsonNode? t in list?["items"] as JsonArray ?? new JsonArray())
        {
            string title = t?["title"]?.ToString()?.Trim() ?? "";
            if (t == null || title.Length == 0) continue;
            DateTime? due = t["due"]?.ToString() is { Length: > 0 } d && DateTimeOffset.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset o)
                ? o.UtcDateTime.Date : null; // у Google Задач в сроке только дата
            result.Add(new GoogleTask(t["id"]!.ToString(), "@default", title, due, t["notes"]?.ToString() ?? ""));
        }
        return result.OrderBy(t => t.Due ?? DateTime.MaxValue).ToList();
    }

    // ---------- задачи: запись ----------

    public async Task<GoogleTask> AddTaskAsync(string title, DateTime? due, string notes = "")
    {
        var body = new Dictionary<string, object> { ["title"] = title };
        if (notes.Length > 0) body["notes"] = notes;
        if (due is DateTime d) body["due"] = d.Date.ToString("yyyy-MM-dd") + "T00:00:00.000Z";
        JsonNode? j = await SendAsync(HttpMethod.Post, "https://tasks.googleapis.com/tasks/v1/lists/@default/tasks", body);
        var task = new GoogleTask(j?["id"]?.ToString() ?? "", "@default", title, due?.Date, notes);
        Tasks = Tasks.Append(task).OrderBy(t => t.Due ?? DateTime.MaxValue).ToList();
        Changed?.Invoke();
        return task;
    }

    public async Task CompleteTaskAsync(GoogleTask task)
    {
        await SendAsync(HttpMethod.Patch, $"https://tasks.googleapis.com/tasks/v1/lists/{Uri.EscapeDataString(task.ListId)}/tasks/{Uri.EscapeDataString(task.Id)}",
            new Dictionary<string, object> { ["status"] = "completed" });
        Tasks = Tasks.Where(t => t.Id != task.Id).ToList();
        Changed?.Invoke();
    }

    /// <summary>Задача по словам из голосовой команды: «отметь задачу купить хлеб».</summary>
    public GoogleTask? FindTask(string words)
    {
        string w = Norm(words);
        if (w.Length == 0) return null;
        return Tasks.FirstOrDefault(t => Norm(t.Title) == w)
               ?? Tasks.FirstOrDefault(t => Norm(t.Title).Contains(w))
               ?? Tasks.OrderByDescending(t => Overlap(Norm(t.Title), w)).FirstOrDefault(t => Overlap(Norm(t.Title), w) >= 0.5);
    }

    private static string Norm(string s) => Regex.Replace(s.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\p{Nd} ]", "").Trim();

    private static double Overlap(string a, string b)
    {
        string[] wa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries), wb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wb.Length == 0) return 0;
        int hit = wb.Count(x => wa.Any(y => y.StartsWith(x.Length > 4 ? x[..^2] : x)));
        return (double)hit / wb.Length;
    }

    // ---------- напоминание за 10 минут ----------

    private void CheckSoon()
    {
        if (!App.Settings.GoogleEventAlerts) return;
        DateTime now = DateTime.Now;
        foreach (CalendarEvent e in Events)
        {
            if (e.AllDay || e.Start <= now) continue;
            if (e.Start - now > TimeSpan.FromMinutes(10)) break;
            string key = e.Id + e.Start.Ticks;
            lock (_alerted)
            {
                if (!_alerted.Add(key)) continue;
            }
            EventSoon?.Invoke(e);
        }
    }

    /// <summary>Ближайшее событие для капсулы: идёт сейчас или начнётся в течение часа.</summary>
    public CalendarEvent? NextForPill()
    {
        DateTime now = DateTime.Now;
        return Events.FirstOrDefault(e => !e.AllDay && (e.IsNow || (e.Start > now && e.Start - now <= TimeSpan.FromMinutes(60))));
    }

    /// <summary>Фраза для голоса: «что у меня сегодня в календаре».</summary>
    public string Describe(DateTime day)
    {
        if (!IsSignedIn) return "Google Календарь не подключён, сэр. Войдите в Google в настройках, раздел «Google».";
        var items = Events.Where(e => e.AllDay ? e.Start.Date <= day.Date && e.End.Date > day.Date : e.Start.Date == day.Date).ToList();
        string when = day.Date == DateTime.Today ? "Сегодня" : day.Date == DateTime.Today.AddDays(1) ? "Завтра" : day.ToString("d MMMM", new CultureInfo("ru-RU"));
        if (day.Date == DateTime.Today) items = items.Where(e => e.AllDay || e.End > DateTime.Now).ToList();
        if (items.Count == 0) return $"{when} в календаре ничего нет, сэр.";
        string list = string.Join("; ", items.Take(6).Select(e => e.AllDay ? e.Title + " — весь день" : $"в {e.Start:HH:mm} {e.Title}"));
        string more = items.Count > 6 ? $" И ещё {items.Count - 6}." : "";
        return $"{when}: {list}.{more}";
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

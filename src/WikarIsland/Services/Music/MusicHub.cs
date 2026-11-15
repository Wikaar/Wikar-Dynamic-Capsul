using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WikarIsland.Services;

/// <summary>
/// Встроенная музыка острова: Яндекс Музыка, YouTube Music и Spotify под своими аккаунтами.
/// Хранит, какой сервис выбран во вкладке и какой сейчас играет, и отдаёт снимок «что играет»
/// в <see cref="MediaService"/> — поэтому пилюля, эквалайзер, «Главная» и Капсулка видят встроенный плеер как любой другой.
/// </summary>
public sealed class MusicHub : IDisposable
{
    public const string AppIdPrefix = "wikar.music.";

    private static readonly HttpClient Http = MusicHttp.Create();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _coverUrl = "";
    private ImageSource? _cover;
    private Color? _accent;
    private bool _polling;

    public IReadOnlyList<IMusicProvider> Providers { get; }
    public IMusicProvider Selected { get; private set; }

    /// <summary>Сервис, который сейчас играет (или стоит на паузе) во встроенном плеере.</summary>
    public IMusicProvider? Active { get; private set; }

    /// <summary>Последнее сообщение для строки состояния во вкладке «Музыка».</summary>
    public string Status { get; private set; } = "";

    /// <summary>Что-то изменилось: трек, пауза, вход, сообщение.</summary>
    public event Action? Changed;

    public MusicHub()
    {
        Providers = new IMusicProvider[] { new YandexMusicProvider(), new YouTubeMusicProvider(), new SpotifyProvider() };
        Selected = Providers.FirstOrDefault(p => p.Key == App.Settings.MusicProvider) ?? Providers[0];
        foreach (IMusicProvider p in Providers)
        {
            IMusicProvider provider = p;
            provider.StateChanged += () => OnStateChanged(provider);
            provider.Error += message => SetStatus(message);
        }
        _poll.Tick += async (_, _) =>
        {
            if (Active == null || _polling) return;
            _polling = true;
            try
            {
                await Active.PollAsync();
            }
            finally
            {
                _polling = false;
            }
        };
    }

    /// <summary>При запуске острова: проверить сохранённые входы (в фоне, без окон).</summary>
    public async void Start()
    {
        foreach (IMusicProvider p in Providers.Where(p => p.IsLoggedIn))
        {
            try
            {
                await p.RefreshAccountAsync();
            }
            catch (MusicException ex)
            {
                if (p == Selected) SetStatus(ex.Message);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
        Changed?.Invoke();
    }

    public IMusicProvider? Find(string key) => Providers.FirstOrDefault(p => p.Key == key);

    public void Select(string key)
    {
        IMusicProvider? p = Find(key);
        if (p == null || p == Selected) return;
        Selected = p;
        App.Settings.MusicProvider = key;
        Status = "";
        Changed?.Invoke();
    }

    public void SetStatus(string message)
    {
        Status = message;
        Changed?.Invoke();
    }

    // ---------- действия из вкладки и голоса (ошибки → строка состояния) ----------

    public async Task<bool> LoginAsync(IMusicProvider p)
    {
        SetStatus("Открываю вход…");
        try
        {
            string? error = await p.LoginAsync();
            SetStatus(error ?? $"Готово: {p.Name} подключён" + (p.Account.Length > 0 ? $" ({p.Account})." : "."));
            return error == null;
        }
        catch (MusicException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetStatus("Не получилось войти: " + ex.Message);
        }
        return false;
    }

    public void Logout(IMusicProvider p)
    {
        if (Active == p) Deactivate();
        p.Logout();
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<MusicTrack>?> SearchAsync(IMusicProvider p, string query, CancellationToken ct)
    {
        SetStatus($"Ищу «{query}»…");
        IReadOnlyList<MusicTrack>? list = await Guard(() => p.SearchAsync(query, ct));
        if (list != null) SetStatus(list.Count == 0 ? "Ничего не нашлось." : "");
        return list;
    }

    public async Task<IReadOnlyList<MusicTrack>?> FavoritesAsync(IMusicProvider p, CancellationToken ct)
    {
        SetStatus("Загружаю…");
        IReadOnlyList<MusicTrack>? list = await Guard(() => p.FavoritesAsync(ct));
        if (list != null) SetStatus(list.Count == 0 ? "Здесь пока пусто." : "");
        return list;
    }

    public async Task<bool> PlayAsync(IMusicProvider p, IReadOnlyList<MusicTrack> queue, int index)
    {
        Activate(p);
        SetStatus("Включаю…");
        bool ok = await Guard(async () => { await p.PlayAsync(queue, index); return true; });
        if (ok && Status == "Включаю…") SetStatus("");
        return ok;
    }

    public async Task<bool> PlayMixAsync(IMusicProvider p)
    {
        Activate(p);
        SetStatus($"Включаю «{p.MixName}»…");
        bool ok = await Guard(async () => { await p.PlayMixAsync(); return true; });
        if (ok && Status.StartsWith("Включаю")) SetStatus("");
        return ok;
    }

    private async Task<T?> Guard<T>(Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (OperationCanceledException)
        {
            return default;
        }
        catch (MusicException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            SetStatus("Ошибка: " + ex.Message);
        }
        return default;
    }

    /// <summary>Играть может только один сервис: остальные ставим на паузу.</summary>
    private void Activate(IMusicProvider p)
    {
        if (Active != null && Active != p) Active.Stop();
        Active = p;
        _poll.Start();
    }

    private void Deactivate()
    {
        Active?.Stop();
        Active = null;
        _poll.Stop();
        App.Media.Refresh();
    }

    public void PlayPause() => Active?.PlayPause();
    public void Next() => Active?.Next();
    public void Previous() => Active?.Previous();
    public void Seek(TimeSpan position) => Active?.Seek(position);

    // ---------- голос: «Капсулка, включи … в Яндекс Музыке» ----------

    /// <summary>Сервис по названию в любом написании (как в AppAutomation).</summary>
    public static string KeyOf(string spoken) =>
        spoken.Contains("янд", StringComparison.OrdinalIgnoreCase) || spoken.Contains("yandex", StringComparison.OrdinalIgnoreCase) ? "yandex"
        : spoken.Contains("спот", StringComparison.OrdinalIgnoreCase) || spoken.Contains("spot", StringComparison.OrdinalIgnoreCase) ? "spotify"
        : "youtube";

    /// <summary>
    /// Включить во встроенном плеере, если в сервис уже вошли. null — сервис не подключён к острову,
    /// тогда Капсулка действует по-старому (открывает программу и нажимает кнопки).
    /// </summary>
    public async Task<AppAutomation.MusicResult?> PlayVoiceAsync(string spokenService, string? query)
    {
        IMusicProvider? p = Find(KeyOf(spokenService));
        if (p == null || !p.IsLoggedIn || !App.Settings.MusicVoiceBuiltIn) return null;

        bool started;
        if (query == null)
        {
            started = await PlayMixAsync(p);
        }
        else
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            IReadOnlyList<MusicTrack>? found = await SearchAsync(p, query, cts.Token);
            if (found == null) return new AppAutomation.MusicResult(false, $"{p.Name}: {Status}");
            if (found.Count == 0) return new AppAutomation.MusicResult(false, $"в {p.Name} ничего не нашлось по запросу «{query}».");
            App.Panel.ShowMusicResults(p, found, query);
            started = await PlayAsync(p, found, 0);
        }
        if (!started) return new AppAutomation.MusicResult(false, $"{p.Name}: {Status}");

        for (int i = 0; i < 20; i++)
        {
            if (p.State is { IsPlaying: true, Title.Length: > 0 } s)
            {
                string track = s.Artist.Length > 0 ? $"{s.Artist}, {s.Title}" : s.Title;
                return new AppAutomation.MusicResult(true, $"Играет {track}.");
            }
            await Task.Delay(500);
            if (Active == p) await p.PollAsync();
        }
        return new AppAutomation.MusicResult(false, $"{p.Name} не начал играть. {Status}".Trim());
    }

    // ---------- для пилюли и медиапульта ----------

    private void OnStateChanged(IMusicProvider p)
    {
        if (p != Active) return;
        string url = p.State?.CoverUrl ?? "";
        if (url != _coverUrl)
        {
            _coverUrl = url;
            _cover = null;
            _accent = null;
            if (url.Length > 0) _ = LoadCoverAsync(url);
        }
        App.Media.Refresh();
        Changed?.Invoke();
    }

    private async Task LoadCoverAsync(string url)
    {
        try
        {
            byte[] bytes = await Http.GetByteArrayAsync(url);
            if (url != _coverUrl) return;
            _cover = MediaService.ImageFromBytes(bytes, 200);
            _accent = _cover is BitmapSource bmp ? ColorHelper.Dominant(bmp) : null;
            App.Media.Refresh();
            Changed?.Invoke();
        }
        catch
        {
            // без обложки
        }
    }

    public ImageSource? Cover => _cover;

    /// <summary>Снимок для <see cref="MediaService"/>: что играет во встроенном плеере, или null.</summary>
    public MediaInfo? Snapshot()
    {
        if (Active?.State is not { Title.Length: > 0 } s) return null;
        return new MediaInfo
        {
            Title = s.Title,
            Artist = s.Artist,
            IsPlaying = s.IsPlaying,
            Position = s.Position,
            Duration = s.Duration,
            PositionUpdated = s.PositionUpdated,
            Cover = _cover,
            Accent = _accent,
            AppName = Active.Name,
            AppId = AppIdPrefix + Active.Key,
        };
    }

    public void Dispose()
    {
        _poll.Stop();
        foreach (IMusicProvider p in Providers)
        {
            try
            {
                p.Stop();
                p.Dispose();
            }
            catch
            {
                // при выходе — неважно
            }
        }
    }
}

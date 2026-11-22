using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using WikarIsland.Views;

namespace WikarIsland.Services;

/// <summary>Трек из поиска или подборки любого сервиса.</summary>
public sealed record MusicTrack(string Provider, string Id, string Title, string Artist, string? CoverUrl, TimeSpan Duration)
{
    public string Album { get; init; } = "";

    /// <summary>Яндекс — id альбома, Spotify — uri трека.</summary>
    public string? Extra { get; init; }
}

/// <summary>Что играет во встроенном плеере сервиса.</summary>
public sealed class MusicState
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string? CoverUrl { get; init; }
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset PositionUpdated { get; init; } = DateTimeOffset.Now;

    /// <summary>Прямая ссылка на звук трека (Яндекс Музыка) — для Discord-бота.</summary>
    public Uri? StreamUrl { get; init; }

    /// <summary>ID видео YouTube (YouTube Music) — для Discord-бота.</summary>
    public string? VideoId { get; init; }

    /// <summary>Изменилось ли что-то, что видно в пилюле (без учёта позиции).</summary>
    public bool SameAs(MusicState? other) =>
        other != null && other.Title == Title && other.Artist == Artist && other.CoverUrl == CoverUrl &&
        other.IsPlaying == IsPlaying && Math.Abs((other.Duration - Duration).TotalSeconds) < 1 &&
        other.VideoId == VideoId && other.StreamUrl == StreamUrl;
}

/// <summary>Понятная пользователю ошибка сервиса — показывается как есть.</summary>
public sealed class MusicException : Exception
{
    public MusicException(string message) : base(message) { }
}

/// <summary>
/// Музыкальный сервис внутри острова: вход в свой аккаунт, поиск и воспроизведение.
/// Все методы вызываются в потоке интерфейса.
/// </summary>
public interface IMusicProvider : IDisposable
{
    string Key { get; }
    string Name { get; }

    /// <summary>Подпись кнопки личной подборки: «Моя волна», «Понравившиеся»…</summary>
    string MixName { get; }

    /// <summary>Подпись кнопки списка любимых треков или null, если у сервиса её нет.</summary>
    string? FavoritesName { get; }

    /// <summary>Вошли ли в аккаунт (или сервис готов к работе без входа).</summary>
    bool IsLoggedIn { get; }

    /// <summary>Имя аккаунта для подписи «Вошли как …».</summary>
    string Account { get; }

    MusicState? State { get; }

    /// <summary>Сменился трек, пауза/воспроизведение, перемотка.</summary>
    event Action? StateChanged;

    /// <summary>Ошибка по ходу воспроизведения (например, трек недоступен).</summary>
    event Action<string>? Error;

    /// <summary>Открыть окно входа. null — получилось, иначе причина.</summary>
    Task<string?> LoginAsync();

    void Logout();

    /// <summary>Проверить вход и обновить имя аккаунта (при запуске и после входа).</summary>
    Task RefreshAccountAsync();

    Task<IReadOnlyList<MusicTrack>> SearchAsync(string query, CancellationToken ct);
    Task<IReadOnlyList<MusicTrack>> FavoritesAsync(CancellationToken ct);
    Task PlayAsync(IReadOnlyList<MusicTrack> queue, int index);
    Task PlayMixAsync();

    void PlayPause();
    void Next();
    void Previous();
    void Seek(TimeSpan position);
    void Stop();

    /// <summary>Заглушить звук на ПК (музыка в это время играет через Discord-бота). Сервисы без своего звука — ничего не делают.</summary>
    void SetMuted(bool muted) { }

    /// <summary>Раз в секунду, пока сервис активен: обновить состояние, если оно живёт снаружи (страница, Spotify).</summary>
    Task PollAsync();
}

internal static class MusicHttp
{
    public static HttpClient Create()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru");
        return http;
    }
}

/// <summary>Окно входа: открывает страницу сервиса во встроенном браузере и ждёт адрес возврата с токеном или кодом.</summary>
internal static class LoginWindow
{
    /// <summary>Адрес, на котором закончился вход, или null, если окно закрыли.</summary>
    public static async Task<string?> CaptureAsync(string startUrl, Func<string, bool> isDone, string title)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new AiWindow(null) { Title = title, Width = 560, Height = 760 };
        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Show();
        window.Activate();
        await window.InitAsync();
        CoreWebView2? core = window.Web.CoreWebView2;
        if (core == null)
        {
            window.Close();
            return null;
        }

        void Check(string? url)
        {
            if (!string.IsNullOrEmpty(url) && isDone(url)) tcs.TrySetResult(url);
        }

        core.NavigationStarting += (_, e) =>
        {
            if (!isDone(e.Uri)) return;
            e.Cancel = true; // адрес возврата (127.0.0.1, music.yandex.ru#token) открывать не нужно
            tcs.TrySetResult(e.Uri);
        };
        core.SourceChanged += (_, _) => Check(core.Source);
        core.NavigationCompleted += (_, _) => Check(core.Source);
        core.Navigate(startUrl);

        string? result = await tcs.Task;
        if (window.IsVisible) window.Close();
        return result;
    }
}

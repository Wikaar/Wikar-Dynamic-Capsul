using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WikarIsland.Services;

/// <summary>Что играет сейчас — снимок для интерфейса.</summary>
public sealed class MediaInfo
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public bool IsPlaying { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset PositionUpdated { get; init; }
    public ImageSource? Cover { get; init; }
    public ImageSource? AppIcon { get; init; }
    public string AppName { get; init; } = "";
    public string AppId { get; init; } = "";

    /// <summary>Основной цвет обложки (для свечения пилюли и эквалайзера).</summary>
    public Color? Accent { get; init; }

    /// <summary>Позиция «сейчас» с учётом времени, прошедшего с последнего обновления.</summary>
    public TimeSpan LivePosition
    {
        get
        {
            TimeSpan pos = Position;
            if (IsPlaying && PositionUpdated != default)
                pos += DateTimeOffset.Now - PositionUpdated;
            if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
            return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
        }
    }
}

/// <summary>
/// Читает системные медиа-контролы Windows (то, что видно во всплывающем окне громкости):
/// Яндекс Музыка, Spotify, браузеры с YouTube и т. д.
/// </summary>
public sealed class MediaService
{
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DispatcherTimer _poll;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _coverKey = "";
    private ImageSource? _cover;
    private Color? _accent;
    private string _iconKey = "";
    private (ImageSource? icon, string name) _icon;

    public MediaInfo? Current { get; private set; }
    public event Action? Changed;

    public MediaService()
    {
        // события от неактивных сессий не приходят — раз в 2 с проверяем, кто играет
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _poll.Tick += (_, _) => Attach();
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(Attach);
            _manager.SessionsChanged += (_, _) => _ui.BeginInvoke(Attach);
            Attach();
            _poll.Start();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        try
        {
            var sessions = _manager.GetSessions();
            var playing = sessions.FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            return playing ?? _manager.GetCurrentSession() ?? sessions.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private void Attach()
    {
        GlobalSystemMediaTransportControlsSession? next = PickSession();
        if (next?.SourceAppUserModelId != _session?.SourceAppUserModelId)
        {
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnSessionChanged;
                _session.PlaybackInfoChanged -= OnSessionChanged;
                _session.TimelinePropertiesChanged -= OnSessionChanged;
            }
            _session = next;
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnSessionChanged;
                _session.PlaybackInfoChanged += OnSessionChanged;
                _session.TimelinePropertiesChanged += OnSessionChanged;
            }
        }
        _ = RefreshAsync();
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSession sender, object args) =>
        _ui.BeginInvoke(() => _ = RefreshAsync());

    private async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            GlobalSystemMediaTransportControlsSession? s = _session;

            // встроенный плеер острова (Яндекс Музыка, YouTube Music, Spotify) важнее, пока играет
            // или пока никто другой не играет — тогда пилюля и Капсулка видят его как любой другой плеер
            if (App.Music?.Snapshot() is { } own &&
                (own.IsPlaying || s?.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing))
            {
                Publish(own);
                return;
            }

            if (s == null)
            {
                Publish(null);
                return;
            }

            var props = await s.TryGetMediaPropertiesAsync();
            var playback = s.GetPlaybackInfo();
            var timeline = s.GetTimelineProperties();
            string appId = s.SourceAppUserModelId ?? "";

            if (props == null || string.IsNullOrWhiteSpace(props.Title))
            {
                Publish(null);
                return;
            }

            string coverKey = appId + "|" + props.Title + "|" + props.Artist;
            if (coverKey != _coverKey)
            {
                _coverKey = coverKey;
                _cover = await ReadImageAsync(props.Thumbnail);
                _accent = _cover is BitmapSource bmp ? ColorHelper.Dominant(bmp) : null;
            }

            if (appId != _iconKey)
            {
                _iconKey = appId;
                _icon = await AppIconHelper.GetAsync(appId);
            }

            Publish(new MediaInfo
            {
                Title = props.Title,
                Artist = props.Artist ?? "",
                IsPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline != null ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero,
                PositionUpdated = timeline?.LastUpdatedTime ?? default,
                Cover = _cover,
                Accent = _accent,
                AppIcon = _icon.icon,
                AppName = _icon.name,
                AppId = appId,
            });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(MediaInfo? info)
    {
        Current = info;
        Changed?.Invoke();
    }

    private static async Task<ImageSource?> ReadImageAsync(IRandomAccessStreamReference? reference)
    {
        if (reference == null) return null;
        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            using Stream net = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await net.CopyToAsync(ms);
            return ImageFromBytes(ms.ToArray(), 200);
        }
        catch
        {
            return null;
        }
    }

    public static ImageSource? ImageFromBytes(byte[] bytes, int decodeWidth)
    {
        if (bytes.Length == 0) return null;
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = new MemoryStream(bytes);
        img.DecodePixelWidth = decodeWidth;
        img.EndInit();
        img.Freeze();
        return img;
    }

    /// <summary>Перечитать «что играет» (встроенный плеер сообщает о своих изменениях сам).</summary>
    public void Refresh() => _ui.BeginInvoke(() => _ = RefreshAsync());

    // ---------- управление ----------

    /// <summary>Сейчас показан встроенный плеер острова — команды идут ему, а не системному медиапульту.</summary>
    private static bool Own => App.Music != null && App.Media.Current?.AppId.StartsWith(MusicHub.AppIdPrefix, StringComparison.Ordinal) == true;

    public async void PlayPause()
    {
        if (Own) { App.Music.PlayPause(); return; }
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async void Next()
    {
        if (Own) { App.Music.Next(); return; }
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    public async void Previous()
    {
        if (Own) { App.Music.Previous(); return; }
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    public async void Seek(TimeSpan position)
    {
        if (Own) { App.Music.Seek(position); return; }
        try { if (_session != null) await _session.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }
}

/// <summary>Яркий основной цвет картинки.</summary>
internal static class ColorHelper
{
    public static Color? Dominant(BitmapSource source)
    {
        try
        {
            double scale = 24.0 / Math.Max(source.PixelWidth, source.PixelHeight);
            BitmapSource small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            byte[] px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double pb = px[i], pg = px[i + 1], pr = px[i + 2];
                double max = Math.Max(pr, Math.Max(pg, pb)), min = Math.Min(pr, Math.Min(pg, pb));
                double weight = 0.05 + (max - min) / 255.0 * (max / 255.0); // яркие и насыщенные важнее
                r += pr * weight; g += pg * weight; b += pb * weight; total += weight;
            }
            if (total <= 0) return null;
            r /= total; g /= total; b /= total;

            // поднимаем яркость, чтобы свечение было видно на тёмном
            double m = Math.Max(r, Math.Max(g, b));
            if (m < 1) return Color.FromRgb(200, 200, 210);
            double k = Math.Min(255 / m, 1.6);
            return Color.FromRgb((byte)Math.Min(255, r * k), (byte)Math.Min(255, g * k), (byte)Math.Min(255, b * k));
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Иконка и имя программы-источника звука по её AppUserModelId.</summary>
internal static class AppIconHelper
{
    public static async Task<(ImageSource? icon, string name)> GetAsync(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return (null, "");

        // 1) приложения из Microsoft Store и программы, зарегистрированные в меню «Пуск» (Windows 10 2004+)
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        try
        {
            AppInfo info = AppInfo.GetFromAppUserModelId(appId);
            string name = info.DisplayInfo.DisplayName;
            var logo = info.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
            ImageSource? icon = null;
            if (logo != null)
            {
                using IRandomAccessStreamWithContentType stream = await logo.OpenReadAsync();
                using Stream net = stream.AsStreamForRead();
                var ms = new MemoryStream();
                await net.CopyToAsync(ms);
                icon = MediaService.ImageFromBytes(ms.ToArray(), 64);
            }
            if (icon != null) return (icon, name);
        }
        catch
        {
            // не нашли — пробуем через процесс
        }

        // 2) обычные программы: ищем процесс по имени exe и берём иконку из файла
        try
        {
            string exe = Path.GetFileNameWithoutExtension(appId.Split('!')[0]);
            string tail = appId.Split('.', '!').Last();
            Process? process = Process.GetProcessesByName(exe).FirstOrDefault()
                               ?? Process.GetProcesses().FirstOrDefault(p =>
                                   p.MainWindowHandle != IntPtr.Zero &&
                                   p.ProcessName.Contains(tail, StringComparison.OrdinalIgnoreCase));
            string? path = process?.MainModule?.FileName;
            if (path != null)
            {
                using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    string name = process!.MainModule?.FileVersionInfo.FileDescription ?? process.ProcessName;
                    return (src, string.IsNullOrWhiteSpace(name) ? process.ProcessName : name);
                }
            }
        }
        catch
        {
            // нет доступа к процессу — останется значок по умолчанию
        }

        return (null, appId);
    }
}

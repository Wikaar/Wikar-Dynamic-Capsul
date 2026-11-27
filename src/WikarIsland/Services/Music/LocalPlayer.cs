using System;
using System.Windows;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace WikarIsland.Services;

/// <summary>
/// Свой проигрыватель (Windows.Media.Playback): играет поток по ссылке и сам появляется
/// в системном медиапульте Windows — с названием, обложкой и медиаклавишами.
/// </summary>
public sealed class LocalPlayer : IDisposable
{
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private MediaPlayer? _player;

    public event Action? Ended;
    public event Action? StateChanged;
    public event Action<string>? Failed;
    public event Action? NextRequested;
    public event Action? PreviousRequested;

    private MediaPlayer Player
    {
        get
        {
            if (_player != null) return _player;
            var p = new MediaPlayer { AudioCategory = MediaPlayerAudioCategory.Media, AutoPlay = true };
            p.CommandManager.IsEnabled = true; // пауза/воспроизведение из медиапульта и медиаклавиш — сами
            p.CommandManager.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            p.CommandManager.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
            p.CommandManager.NextReceived += (_, e) =>
            {
                e.Handled = true;
                _ui.BeginInvoke(() => NextRequested?.Invoke());
            };
            p.CommandManager.PreviousReceived += (_, e) =>
            {
                e.Handled = true;
                _ui.BeginInvoke(() => PreviousRequested?.Invoke());
            };
            p.MediaEnded += (_, _) => _ui.BeginInvoke(() => Ended?.Invoke());
            p.MediaFailed += (_, e) =>
            {
                string message = string.IsNullOrWhiteSpace(e.ErrorMessage) ? e.Error.ToString() : e.ErrorMessage;
                _ui.BeginInvoke(() => Failed?.Invoke(message));
            };
            p.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke());
            p.PlaybackSession.SeekCompleted += (_, _) => _ui.BeginInvoke(() => StateChanged?.Invoke());
            _player = p;
            return p;
        }
    }

    /// <summary>Ссылка на поток, который сейчас открыт (для Discord-бота).</summary>
    public Uri? CurrentUri { get; private set; }

    /// <summary>Без звука на ПК — музыка в это время звучит в Discord.</summary>
    public void SetMuted(bool muted) => Player.IsMuted = muted;

    public void Play(Uri uri, MusicTrack track)
    {
        CurrentUri = uri;
        var item = new MediaPlaybackItem(MediaSource.CreateFromUri(uri));
        try
        {
            MediaItemDisplayProperties props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = track.Title;
            props.MusicProperties.Artist = track.Artist;
            props.MusicProperties.AlbumTitle = track.Album;
            if (track.CoverUrl != null) props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(track.CoverUrl));
            item.ApplyDisplayProperties(props);
        }
        catch (Exception ex)
        {
            App.LogError(ex); // без подписи в медиапульте — не страшно, играть всё равно будем
        }
        Player.Source = item;
        Player.Play();
    }

    /// <summary>
    /// Обновить подпись в медиапульте Windows (SMTC) у того, что уже играет: радио узнаёт песню позже начала эфира.
    /// Отсюда же её берут Capsul Box и другие программы, читающие медиасессии Windows.
    /// </summary>
    public void UpdateDisplay(string title, string artist, string album, string? coverUrl)
    {
        if (_player?.Source is not MediaPlaybackItem item) return;
        try
        {
            MediaItemDisplayProperties props = item.GetDisplayProperties();
            props.Type = MediaPlaybackType.Music;
            props.MusicProperties.Title = title;
            props.MusicProperties.Artist = artist;
            props.MusicProperties.AlbumTitle = album;
            if (!string.IsNullOrWhiteSpace(coverUrl) && Uri.TryCreate(coverUrl, UriKind.Absolute, out Uri? cover))
                props.Thumbnail = RandomAccessStreamReference.CreateFromUri(cover);
            item.ApplyDisplayProperties(props);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }

        // Свойства элемента Windows переносит в медиапульт только при смене трека, поэтому пишем туда и напрямую:
        // иначе другие программы (Capsul Box, окно громкости) видели бы подпись, заданную при включении станции.
        try
        {
            SystemMediaTransportControlsDisplayUpdater du = _player.SystemMediaTransportControls.DisplayUpdater;
            du.Type = MediaPlaybackType.Music;
            du.MusicProperties.Title = title;
            du.MusicProperties.Artist = artist;
            du.MusicProperties.AlbumTitle = album;
            if (!string.IsNullOrWhiteSpace(coverUrl) && Uri.TryCreate(coverUrl, UriKind.Absolute, out Uri? thumb))
                du.Thumbnail = RandomAccessStreamReference.CreateFromUri(thumb);
            du.Update();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public bool HasSource => _player?.Source != null;

    /// <summary>Играет или вот-вот заиграет (открывается, буферизуется).</summary>
    public bool IsPlaying => _player?.Source != null && _player.PlaybackSession.PlaybackState is
        MediaPlaybackState.Playing or MediaPlaybackState.Buffering or MediaPlaybackState.Opening;

    public TimeSpan Position => _player?.PlaybackSession.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _player?.PlaybackSession.NaturalDuration ?? TimeSpan.Zero;

    public void PlayPause()
    {
        if (_player?.Source == null) return;
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        else _player.Play();
    }

    public void Seek(TimeSpan position)
    {
        if (_player?.Source == null) return;
        _player.PlaybackSession.Position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
    }

    public void Stop()
    {
        if (_player == null) return;
        _player.Pause();
        _player.Source = null;
        CurrentUri = null;
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        try
        {
            _player?.Dispose();
        }
        catch
        {
            // при выходе — неважно
        }
        _player = null;
    }
}

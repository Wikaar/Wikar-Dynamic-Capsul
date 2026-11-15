using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>Вкладка «Музыка»: вход в сервис, поиск, «Моя волна» / любимые и встроенный плеер.</summary>
public partial class PanelWindow
{
    private IReadOnlyList<MusicTrack> _musicTracks = Array.Empty<MusicTrack>();
    private IMusicProvider? _musicTracksFrom;
    private CancellationTokenSource? _musicCts;

    private static readonly Brush ChipSelected = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowNormal = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22));
    private static readonly Brush RowHover = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
    private static readonly Brush RowPlaying = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x36));

    /// <summary>Сервис что-то сообщил (трек, вход, ошибка) — перерисовать вкладку, если она открыта.</summary>
    public void UpdateMusic()
    {
        if (_current == "music") UpdateMusicView();
    }

    /// <summary>Голосовой поиск: показать найденное во вкладке, чтобы можно было выбрать другой трек.</summary>
    public void ShowMusicResults(IMusicProvider provider, IReadOnlyList<MusicTrack> tracks, string query)
    {
        App.Music.Select(provider.Key);
        MusicSearchBox.Text = query;
        SetMusicList(provider, tracks, $"ПОИСК: {query.ToUpperInvariant()}");
    }

    private void UpdateMusicView()
    {
        MusicHub hub = App.Music;
        IMusicProvider p = hub.Selected;

        foreach (Button chip in new[] { MusicYandexChip, MusicYoutubeChip, MusicSpotifyChip })
        {
            bool on = (string)chip.Tag == p.Key;
            chip.Background = on ? ChipSelected : Brushes.Transparent;
            chip.Foreground = on ? (Brush)FindResource("TextMain") : new SolidColorBrush(Color.FromRgb(0xD8, 0xD8, 0xDE));
        }

        bool needsClientId = p.Key == "spotify" && App.Settings.SpotifyClientId.Length == 0;
        MusicSpotifySetup.Visibility = p.Key == "spotify" && !p.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        MusicSpotifyRedirect.Text = SpotifyProvider.RedirectUri;
        if (MusicSpotifySetup.Visibility == Visibility.Visible && !MusicSpotifyIdBox.IsKeyboardFocused)
            MusicSpotifyIdBox.Text = App.Settings.SpotifyClientId;

        bool youtubeGuest = p.Key == "youtube" && p.Account.Length == 0;
        MusicAccountText.Text = p.Account.Length > 0 ? p.Account
            : youtubeGuest && p.IsLoggedIn ? "без входа: поиск и прослушивание"
            : p.IsLoggedIn ? "вход выполнен"
            : "";
        MusicLoginButton.Content = p.Account.Length > 0 || (p.IsLoggedIn && p.Key != "youtube") ? "Выйти" : "Войти";
        MusicLoginButton.IsEnabled = !needsClientId;

        MusicMixButton.Content = p.MixName;
        MusicFavButton.Content = p.FavoritesName ?? "";
        MusicFavButton.Visibility = p.FavoritesName != null ? Visibility.Visible : Visibility.Collapsed;

        if (_musicTracksFrom != null && _musicTracksFrom != p)
        {
            _musicTracks = Array.Empty<MusicTrack>();
            _musicTracksFrom = null;
            MusicListTitle.Text = "";
        }
        if (_musicTracks.Count == 0) ShowMusicHint(p);
        else HighlightPlaying();

        UpdateMusicNow();
        MusicStatus.Text = hub.Status;
    }

    private void ShowMusicHint(IMusicProvider p)
    {
        MusicList.Children.Clear();
        MusicListTitle.Text = "";
        string text = p.Key switch
        {
            "yandex" when !p.IsLoggedIn => "Войди в аккаунт Яндекса — и музыка будет играть прямо в острове: поиск, «Моя волна», «Мне нравится». Полные треки — с подпиской Плюс.",
            "spotify" when !p.IsLoggedIn => "После входа: поиск по Spotify, любимые треки и управление воспроизведением. Звук играет приложение Spotify (так требует Spotify), остров его запускает и управляет.",
            "youtube" => "Ищи и слушай YouTube Music прямо в острове. Вход в Google общий с ИИ-вкладками: если ты уже вошёл в Gemini, ты вошёл и сюда.",
            _ => "Найди исполнителя или трек — или включи «" + p.MixName + "».",
        };
        MusicList.Children.Add(new TextBlock { Text = text, Style = (Style)FindResource("Hint"), Margin = new Thickness(4, 6, 4, 0) });
        MusicList.Children.Add(new TextBlock
        {
            Text = "Капсулка тоже умеет: «Капсулка, включи Король и Шут в " + p.Name + "».",
            Style = (Style)FindResource("Hint"),
            FontSize = 11.5,
            Margin = new Thickness(4, 8, 4, 0),
        });
    }

    private void SetMusicList(IMusicProvider p, IReadOnlyList<MusicTrack> tracks, string title)
    {
        _musicTracks = tracks;
        _musicTracksFrom = p;
        MusicListTitle.Text = title;
        MusicList.Children.Clear();
        for (int i = 0; i < tracks.Count; i++) MusicList.Children.Add(MusicRow(p, tracks, i));
        HighlightPlaying();
    }

    private Border MusicRow(IMusicProvider p, IReadOnlyList<MusicTrack> tracks, int index)
    {
        MusicTrack t = tracks[index];
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cover = new Image
        {
            Width = 40,
            Height = 40,
            Stretch = Stretch.UniformToFill,
            Source = RemoteImage(t.CoverUrl, 80),
            Clip = new RectangleGeometry(new Rect(0, 0, 40, 40), 6, 6),
        };
        grid.Children.Add(cover);

        var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = t.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextMain"),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
        });
        text.Children.Add(new TextBlock
        {
            Text = t.Album.Length > 0 && t.Artist.Length > 0 ? $"{t.Artist} · {t.Album}" : t.Artist,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextDim"),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = 12,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (t.Duration > TimeSpan.Zero)
        {
            var duration = new TextBlock { Text = FormatTime(t.Duration), Style = (Style)FindResource("Hint"), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(duration, 2);
            grid.Children.Add(duration);
        }

        var row = new Border
        {
            Child = grid,
            Background = RowNormal,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 12, 6),
            Margin = new Thickness(0, 0, 0, 5),
            Cursor = Cursors.Hand,
            Tag = t,
            ToolTip = "Включить",
        };
        row.MouseEnter += (_, _) => { if (!IsPlayingRow(row)) row.Background = RowHover; };
        row.MouseLeave += (_, _) => row.Background = IsPlayingRow(row) ? RowPlaying : RowNormal;
        row.MouseLeftButtonUp += async (_, _) => await App.Music.PlayAsync(p, tracks, index);
        return row;
    }

    private bool IsPlayingRow(Border row) =>
        row.Tag is MusicTrack t && App.Music.Active?.Key == t.Provider && App.Music.Active.State is { } s &&
        s.Title.StartsWith(t.Title.Split(" (")[0], StringComparison.CurrentCultureIgnoreCase);

    private void HighlightPlaying()
    {
        foreach (Border row in MusicList.Children.OfType<Border>())
            row.Background = IsPlayingRow(row) ? RowPlaying : RowNormal;
    }

    private static ImageSource? RemoteImage(string? url, int width)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(url);
            img.DecodePixelWidth = width;
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            return img;
        }
        catch
        {
            return null;
        }
    }

    // ---------- сейчас играет ----------

    private void UpdateMusicNow()
    {
        MusicHub hub = App.Music;
        MusicState? s = hub.Active?.State;
        if (s == null || s.Title.Length == 0)
        {
            MusicNowCard.Visibility = Visibility.Collapsed;
            return;
        }
        MusicNowCard.Visibility = Visibility.Visible;
        MusicNowTitle.Text = s.Title;
        MusicNowArtist.Text = hub.Active!.Key == hub.Selected.Key ? s.Artist : $"{s.Artist} · {hub.Active.Name}";
        MusicNowCover.Source = hub.Cover;
        MusicPlayButton.Content = s.IsPlaying ? "" : "";
        UpdateMusicProgress();
    }

    private void UpdateMusicProgress()
    {
        if (_current != "music" || MusicNowCard.Visibility != Visibility.Visible) return;
        MusicState? s = App.Music.Active?.State;
        if (s == null || s.Duration <= TimeSpan.Zero)
        {
            MusicProgressFill.Width = 0;
            return;
        }
        TimeSpan pos = s.Position + (s.IsPlaying ? DateTimeOffset.Now - s.PositionUpdated : TimeSpan.Zero);
        double width = MusicProgressArea.ActualWidth;
        MusicProgressFill.Width = width > 0 ? width * Math.Clamp(pos / s.Duration, 0, 1) : 0;
    }

    private void MusicProgress_Click(object sender, MouseButtonEventArgs e)
    {
        MusicState? s = App.Music.Active?.State;
        if (s == null || s.Duration <= TimeSpan.Zero || MusicProgressArea.ActualWidth <= 0) return;
        double ratio = Math.Clamp(e.GetPosition(MusicProgressArea).X / MusicProgressArea.ActualWidth, 0, 1);
        App.Music.Seek(TimeSpan.FromTicks((long)(s.Duration.Ticks * ratio)));
    }

    private void MusicPlay_Click(object sender, RoutedEventArgs e) => App.Music.PlayPause();
    private void MusicNext_Click(object sender, RoutedEventArgs e) => App.Music.Next();
    private void MusicPrev_Click(object sender, RoutedEventArgs e) => App.Music.Previous();

    // ---------- действия ----------

    private void MusicProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            App.Music.Select(key);
            UpdateMusicView();
        }
    }

    private async void MusicLogin_Click(object sender, RoutedEventArgs e)
    {
        IMusicProvider p = App.Music.Selected;
        bool loggedIn = p.Account.Length > 0 || (p.IsLoggedIn && p.Key != "youtube");
        if (loggedIn)
        {
            App.Music.Logout(p);
            _musicTracks = Array.Empty<MusicTrack>();
            UpdateMusicView();
            return;
        }
        MusicLoginButton.IsEnabled = false;
        try
        {
            await App.Music.LoginAsync(p);
        }
        finally
        {
            MusicLoginButton.IsEnabled = true;
            UpdateMusicView();
        }
    }

    private void MusicSpotifyId_Click(object sender, RoutedEventArgs e)
    {
        string id = MusicSpotifyIdBox.Text.Trim();
        if (id.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-fA-F]{32}$"))
        {
            App.Music.SetStatus("Client ID — это 32 символа из цифр и букв a–f, его видно на странице приложения Spotify.");
            return;
        }
        App.Settings.SpotifyClientId = id;
        App.Settings.Save();
        App.Music.SetStatus(id.Length > 0 ? "Client ID сохранён — теперь нажми «Войти»." : "");
        UpdateMusicView();
    }

    private async void MusicSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string query = MusicSearchBox.Text.Trim();
        if (query.Length == 0) return;
        IMusicProvider p = App.Music.Selected;
        if (!p.IsLoggedIn && p.Key != "youtube")
        {
            App.Music.SetStatus($"Сначала войди в {p.Name}.");
            return;
        }
        CancellationToken ct = RestartMusicRequest();
        IReadOnlyList<MusicTrack>? found = await App.Music.SearchAsync(p, query, ct);
        if (found != null && !ct.IsCancellationRequested) SetMusicList(p, found, $"ПОИСК: {query.ToUpperInvariant()}");
    }

    private async void MusicMix_Click(object sender, RoutedEventArgs e)
    {
        IMusicProvider p = App.Music.Selected;
        if (!p.IsLoggedIn && p.Key != "youtube")
        {
            App.Music.SetStatus($"Сначала войди в {p.Name}.");
            return;
        }
        await App.Music.PlayMixAsync(p);
    }

    private async void MusicFav_Click(object sender, RoutedEventArgs e)
    {
        IMusicProvider p = App.Music.Selected;
        if (!p.IsLoggedIn)
        {
            App.Music.SetStatus($"Сначала войди в {p.Name}.");
            return;
        }
        CancellationToken ct = RestartMusicRequest();
        IReadOnlyList<MusicTrack>? list = await App.Music.FavoritesAsync(p, ct);
        if (list != null && !ct.IsCancellationRequested) SetMusicList(p, list, (p.FavoritesName ?? "").ToUpperInvariant());
    }

    private CancellationToken RestartMusicRequest()
    {
        _musicCts?.Cancel();
        _musicCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return _musicCts.Token;
    }
}

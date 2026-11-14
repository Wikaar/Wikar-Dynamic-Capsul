using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>
/// Раскрытый остров: «Главная» (часы, плеер, звук), «Погода», ИИ-чаты (веб-версии в WebView2) и настройки.
/// Это обычное непрозрачное окно — WebView2 не умеет рисоваться в прозрачных окнах WPF.
/// </summary>
public partial class PanelWindow : Window
{
    private static readonly Dictionary<string, string> AiUrls = new()
    {
        ["chatgpt"] = "https://chatgpt.com/",
        ["claude"] = "https://claude.ai/new",
        ["gemini"] = "https://gemini.google.com/app",
        ["deepseek"] = "https://chat.deepseek.com/",
        ["deepl"] = "https://www.deepl.com/translator",
    };

    private static readonly CultureInfo Ru = new("ru-RU");
    private static Task<CoreWebView2Environment>? _environment;

    private readonly Dictionary<string, WebView2> _views = new();
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _tickTimer;
    private string _current = "home";
    private bool _loadingSettings;
    private bool _updatingSlider;
    private DateTime? _outsideSince;

    public PanelWindow()
    {
        InitializeComponent();

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _closeTimer.Tick += (_, _) => CloseTick();

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tickTimer.Tick += (_, _) =>
        {
            UpdateHomeClock();
            UpdateProgress();
        };

        Deactivated += OnDeactivated;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideIsland();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _closeTimer.Start();
                _tickTimer.Start();
                UpdateHomeClock();
                RefreshSound();
            }
            else
            {
                _closeTimer.Stop();
                _tickTimer.Stop();
            }
        };

        Version? v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Wikar Island {v?.ToString(3)}";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExStyle(handle, NativeMethods.WS_EX_TOOLWINDOW);
        NativeMethods.SetDwmInt(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, NativeMethods.DWMWCP_ROUND);
        NativeMethods.SetDwmInt(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
    }

    /// <summary>Общая среда WebView2: вход в аккаунты сохраняется между запусками.</summary>
    public static Task<CoreWebView2Environment> GetEnvironmentAsync() =>
        _environment ??= CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WikarIsland", "WebView2"));

    /// <summary>
    /// Всплывающие окна (например, вход через Google) открываем в своём окне поверх острова,
    /// иначе они окажутся под ним.
    /// </summary>
    public static void HookNewWindows(CoreWebView2 core)
    {
        core.NewWindowRequested += async (_, e) =>
        {
            CoreWebView2Deferral deferral = e.GetDeferral();
            try
            {
                var popup = new AiWindow(url: null) { Topmost = true, Width = 520, Height = 700 };
                popup.Show();
                await popup.InitAsync();
                e.NewWindow = popup.Web.CoreWebView2;
                e.Handled = true;
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    // ---------- показ и скрытие ----------

    public void Toggle(bool activate)
    {
        if (IsVisible && (IsActive || !activate)) HideIsland();
        else ShowNearPill(activate);
    }

    public void ShowTab(string key, bool activate)
    {
        SelectTab(key);
        ShowNearPill(activate);
    }

    public void ShowNearPill(bool activate)
    {
        if (!IsVisible)
        {
            PositionNearPill();
            if (TabHome.IsChecked != true && TabWeather.IsChecked != true && TabSettings.IsChecked != true
                && !_views.ContainsKey(_current))
                SelectTab(App.Settings.LastTab);
            Show();
        }

        _outsideSince = null;
        if (activate)
        {
            Activate();
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            if (_views.TryGetValue(_current, out WebView2? view)) view.Focus();
        }
    }

    public void HideIsland()
    {
        if (!IsVisible) return;
        Hide();
        App.Settings.Save();
    }

    private void PositionNearPill()
    {
        PillWindow pill = App.Pill;
        Rect wa = ScreenHelper.ToDip(ScreenHelper.ScreenOf(pill).WorkingArea, pill);
        Rect p = pill.PillRect();

        double centerX = p.Left + p.Width / 2;
        Left = Math.Clamp(centerX - Width / 2, wa.Left + 6, Math.Max(wa.Left + 6, wa.Right - Width - 6));

        bool pillInUpperHalf = p.Top + p.Height / 2 < wa.Top + wa.Height / 2;
        double top = pillInUpperHalf
            ? p.Bottom + 8          // раскрываемся вниз
            : p.Top - Height - 8;   // пилюля внизу — раскрываемся вверх
        Top = Math.Clamp(top, wa.Top + 4, Math.Max(wa.Top + 4, wa.Bottom - Height - 4));
    }

    /// <summary>Открыт наведением и не активирован: прячем, когда курсор ушёл с острова.</summary>
    private void CloseTick()
    {
        if (IsActive)
        {
            _outsideSince = null;
            return;
        }

        Point c = ScreenHelper.CursorDip(this);
        var panelRect = new Rect(Left - 10, Top - 10, Width + 20, Height + 20);
        Rect pillRect = App.Pill.PillRect();
        pillRect.Inflate(8, 8);

        if (panelRect.Contains(c) || pillRect.Contains(c))
        {
            _outsideSince = null;
            return;
        }

        _outsideSince ??= DateTime.Now;
        if ((DateTime.Now - _outsideSince.Value).TotalMilliseconds > 600) HideIsland();
    }

    /// <summary>Кликнули в другую программу — прячемся. Свои окна (вход через Google, «В окне») не в счёт.</summary>
    private async void OnDeactivated(object? sender, EventArgs e)
    {
        await Task.Delay(150);
        if (!IsActive && !NativeMethods.ForegroundIsOurs()) HideIsland();
    }

    // ---------- вкладки ----------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string key }) SelectTab(key);
    }

    private void SelectTab(string key)
    {
        if (key != "home" && key != "weather" && key != "settings" && !AiUrls.ContainsKey(key)) key = "home";
        _current = key;

        RadioButton tab = key switch
        {
            "weather" => TabWeather,
            "chatgpt" => TabChatGpt,
            "claude" => TabClaude,
            "gemini" => TabGemini,
            "deepseek" => TabDeepSeek,
            "deepl" => TabDeepL,
            "settings" => TabSettings,
            _ => TabHome,
        };
        if (tab.IsChecked != true) tab.IsChecked = true; // вызовет Tab_Checked ещё раз, это безопасно

        bool isAi = AiUrls.ContainsKey(key);
        HomeView.Visibility = key == "home" ? Visibility.Visible : Visibility.Collapsed;
        WeatherView.Visibility = key == "weather" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AiHost.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
        PopOutButton.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
        ReloadButton.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;

        foreach (KeyValuePair<string, WebView2> pair in _views)
            pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;

        if (isAi && !_views.ContainsKey(key)) _ = CreateAiViewAsync(key);
        if (key == "settings") LoadSettings();
        if (key == "weather") BuildWeatherView();
        if (key == "home") { UpdateHomeClock(); RefreshSound(); }

        App.Settings.LastTab = key;
    }

    private async Task CreateAiViewAsync(string key)
    {
        var view = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 20, 20, 23),
        };
        _views[key] = view;
        AiHost.Children.Add(view);

        try
        {
            CoreWebView2Environment env = await GetEnvironmentAsync();
            await view.EnsureCoreWebView2Async(env);
            view.CoreWebView2.Settings.IsStatusBarEnabled = false;
            HookNewWindows(view.CoreWebView2);
            view.CoreWebView2.Navigate(AiUrls[key]);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            AiHost.Children.Remove(view);
            _views.Remove(key);
            AiHost.Children.Add(new TextBlock
            {
                Text = "Не удалось запустить встроенный браузер (WebView2).\n" +
                       "Установи «Microsoft Edge WebView2 Runtime» с сайта Microsoft и перезапусти остров.\n\n" + ex.Message,
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(28),
            });
        }
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (!_views.TryGetValue(_current, out WebView2? view) || view.CoreWebView2 == null) return;
        string url = view.CoreWebView2.Source;
        HideIsland();
        new AiWindow(url).Show();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (_views.TryGetValue(_current, out WebView2? view)) view.CoreWebView2?.Reload();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideIsland();

    // ---------- главная: часы ----------

    private void UpdateHomeClock()
    {
        if (_current != "home") return;
        DateTime now = DateTime.Now;
        HomeTime.Text = now.ToString("HH:mm");
        string date = now.ToString("dddd, d MMMM", Ru);
        HomeDate.Text = char.ToUpper(date[0], Ru) + date[1..];
        HomeHint.Text = $"{App.Settings.Hotkey} — открыть остров • колёсико над пилюлей — громкость • правый клик по пилюле — меню";
    }

    // ---------- главная: плеер ----------

    public void UpdateMedia()
    {
        MediaInfo? m = App.Media.Current;
        PlayerEmpty.Visibility = m == null ? Visibility.Visible : Visibility.Collapsed;
        PlayerContent.Visibility = m == null ? Visibility.Collapsed : Visibility.Visible;
        if (m == null) return;

        CardCover.Source = m.Cover ?? m.AppIcon;
        CardAppIcon.Source = m.AppIcon;
        CardAppName.Text = m.AppName;
        CardAppName.ToolTip = m.AppId;
        CardTitle.Text = m.Title;
        CardArtist.Text = m.Artist;
        PlayPauseButton.Content = m.IsPlaying ? "" : "";
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        MediaInfo? m = App.Media.Current;
        if (m == null || m.Duration <= TimeSpan.Zero)
        {
            ProgressFill.Width = 0;
            PosText.Text = DurText.Text = "";
            return;
        }

        TimeSpan pos = m.LivePosition;
        double width = ProgressArea.ActualWidth;
        ProgressFill.Width = width > 0 ? width * Math.Clamp(pos / m.Duration, 0, 1) : 0;
        PosText.Text = FormatTime(pos);
        DurText.Text = FormatTime(m.Duration);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Progress_Click(object sender, MouseButtonEventArgs e)
    {
        MediaInfo? m = App.Media.Current;
        if (m == null || m.Duration <= TimeSpan.Zero || ProgressArea.ActualWidth <= 0) return;
        double ratio = Math.Clamp(e.GetPosition(ProgressArea).X / ProgressArea.ActualWidth, 0, 1);
        App.Media.Seek(TimeSpan.FromTicks((long)(m.Duration.Ticks * ratio)));
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e) => App.Media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => App.Media.Next();
    private void Prev_Click(object sender, RoutedEventArgs e) => App.Media.Previous();

    // ---------- главная: звук ----------

    public void RefreshSound()
    {
        if (!IsVisible) return;
        float level = App.Audio.Volume;
        bool muted = App.Audio.Muted;

        _updatingSlider = true;
        VolumeSlider.Value = level < 0 ? 0 : Math.Round(level * 100);
        _updatingSlider = false;
        VolumeSlider.IsEnabled = level >= 0;
        VolumePercent.Text = level < 0 ? "—" : muted ? "выкл" : $"{Math.Round(level * 100)}";
        MuteButton.Content = muted ? "" : "";

        DevicesPanel.Children.Clear();
        foreach (AudioDevice device in App.Audio.GetOutputDevices())
        {
            var button = new Button
            {
                Content = device.Name,
                Style = (Style)FindResource("FlatButton"),
                Margin = new Thickness(0, 0, 6, 6),
                Tag = device.Id,
                ToolTip = device.IsDefault ? "Сейчас звук идёт сюда" : "Переключить звук сюда",
            };
            if (device.IsDefault)
                button.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x80));
            button.Click += (_, _) =>
            {
                App.Audio.SetDefaultOutput(device.Id);
                RefreshSound();
            };
            DevicesPanel.Children.Add(button);
        }
        if (DevicesPanel.Children.Count == 0)
            DevicesPanel.Children.Add(new TextBlock { Text = "Устройства вывода не найдены", Style = (Style)FindResource("Hint") });
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !IsLoaded) return;
        App.Audio.Volume = (float)(e.NewValue / 100);
        VolumePercent.Text = $"{Math.Round(e.NewValue)}";
        MuteButton.Content = "";
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        App.Audio.Muted = !App.Audio.Muted;
        RefreshSound();
    }

    // ---------- погода ----------

    public void UpdateWeather()
    {
        WeatherData? w = App.Weather.Current;
        HomeWeather.Text = w == null
            ? (App.Settings.WeatherLat == null ? "Погода: выбери город в настройках" : "Погода загружается…")
            : $"{w.Icon} {Math.Round(w.Temp):0}°, {w.Text.ToLower(Ru)} · {w.City}";
        if (_current == "weather") BuildWeatherView();
    }

    private void HomeWeather_Click(object sender, MouseButtonEventArgs e) =>
        SelectTab(App.Settings.WeatherLat == null ? "settings" : "weather");

    private void BuildWeatherView()
    {
        WeatherContent.Children.Clear();
        WeatherData? w = App.Weather.Current;

        if (w == null)
        {
            WeatherContent.Children.Add(Text(App.Settings.WeatherLat == null
                ? "Город не выбран. Открой «Настройки» → «Погода» и найди свой город."
                : "Загружаю прогноз…", 15, dim: true));
            if (App.Settings.WeatherLat == null)
            {
                var go = new Button { Content = "Выбрать город", Style = (Style)FindResource("FlatButton"),
                                      Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
                go.Click += (_, _) => SelectTab("settings");
                WeatherContent.Children.Add(go);
            }
            return;
        }

        WeatherContent.Children.Add(Text(w.City, 16, dim: true));

        var now = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        now.Children.Add(Text(w.Icon, 54, emoji: true));
        var nowText = new StackPanel { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        nowText.Children.Add(Text($"{Math.Round(w.Temp):0}°", 46, bold: true));
        now.Children.Add(nowText);
        var details = new StackPanel { Margin = new Thickness(28, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(Text(w.Text, 18));
        details.Children.Add(Text($"Ощущается как {Math.Round(w.Feels):0}°", 14, dim: true));
        details.Children.Add(Text($"Ветер {w.Wind:0.#} м/с · осадки {w.Precipitation:0.#} мм", 14, dim: true));
        now.Children.Add(details);
        WeatherContent.Children.Add(now);

        // по часам
        WeatherContent.Children.Add(Text("По часам", 15, bold: true, top: 14));
        var hours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (HourForecast h in w.Hours)
        {
            var cell = new StackPanel { Width = 54 };
            cell.Children.Add(Text(h.Time.ToString("HH:mm"), 12, dim: true, center: true));
            cell.Children.Add(Text(WeatherCodes.Icon(h.Code, h.Time.Hour is >= 7 and < 19), 20, emoji: true, center: true));
            cell.Children.Add(Text($"{Math.Round(h.Temp):0}°", 14, center: true));
            hours.Children.Add(cell);
        }
        WeatherContent.Children.Add(new ScrollViewer
        {
            Content = hours,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        // по дням
        WeatherContent.Children.Add(Text("На неделю", 15, bold: true, top: 18));
        foreach (DayForecast d in w.Days)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string day = d.Date.Date == DateTime.Today ? "Сегодня"
                : d.Date.Date == DateTime.Today.AddDays(1) ? "Завтра"
                : d.Date.ToString("ddd, d MMM", Ru);
            var c0 = Text(day, 14);
            var c1 = Text(WeatherCodes.Icon(d.Code), 17, emoji: true);
            var c2 = Text(WeatherCodes.Text(d.Code), 14, dim: true);
            var c3 = Text($"{Math.Round(d.Max):0}° / {Math.Round(d.Min):0}°", 14, bold: true);
            Grid.SetColumn(c1, 1);
            Grid.SetColumn(c2, 2);
            Grid.SetColumn(c3, 3);
            row.Children.Add(c0);
            row.Children.Add(c1);
            row.Children.Add(c2);
            row.Children.Add(c3);
            WeatherContent.Children.Add(row);
        }

        WeatherContent.Children.Add(Text($"Обновлено в {w.Updated:HH:mm} · данные Open-Meteo", 12, dim: true, top: 16));
    }

    private TextBlock Text(string text, double size, bool dim = false, bool bold = false, bool emoji = false,
                           bool center = false, double top = 0) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = (Brush)FindResource(dim ? "TextDim" : "TextMain"),
        FontFamily = new FontFamily(emoji ? "Segoe UI Emoji" : "Segoe UI Variable Text, Segoe UI"),
        TextAlignment = center ? TextAlignment.Center : TextAlignment.Left,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, top, 0, 0),
    };

    // ---------- настройки ----------

    private void LoadSettings()
    {
        _loadingSettings = true;
        AppSettings s = App.Settings;
        AutoStartBox.IsChecked = s.AutoStart;
        AutoHideBox.IsChecked = s.AutoHide;
        EyesBox.IsChecked = s.EyesEnabled;
        ModeBoth.IsChecked = s.PillMode == "both";
        ModeEyes.IsChecked = s.PillMode == "eyes";
        ModeClock.IsChecked = s.PillMode == "clock";
        MusicPillBox.IsChecked = s.MusicInPill;
        WeatherPillBox.IsChecked = s.WeatherInPill;
        HotkeyBox.Text = s.Hotkey;
        WeatherCityText.Text = s.WeatherCity != null ? $"Сейчас: {s.WeatherCity}" : "Город не выбран";
        _loadingSettings = false;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings s = App.Settings;

        bool autoStart = AutoStartBox.IsChecked == true;
        if (autoStart != s.AutoStart) AutoStart.Set(autoStart);
        s.AutoStart = autoStart;
        s.AutoHide = AutoHideBox.IsChecked == true;
        s.EyesEnabled = EyesBox.IsChecked == true;
        s.PillMode = ModeEyes.IsChecked == true ? "eyes" : ModeClock.IsChecked == true ? "clock" : "both";
        s.MusicInPill = MusicPillBox.IsChecked == true;
        s.WeatherInPill = WeatherPillBox.IsChecked == true;

        s.Save();
        App.Pill.ApplySettings();
    }

    private void CitySearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CitySearch_Click(sender, e);
    }

    private async void CitySearch_Click(object sender, RoutedEventArgs e)
    {
        string query = CitySearchBox.Text.Trim();
        if (query.Length < 2) return;

        CityResults.Children.Clear();
        CityResults.Children.Add(new TextBlock { Text = "Ищу…", Style = (Style)FindResource("Hint") });
        try
        {
            List<CityResult> found = await WeatherService.SearchAsync(query);
            CityResults.Children.Clear();
            if (found.Count == 0)
            {
                CityResults.Children.Add(new TextBlock { Text = "Ничего не нашлось", Style = (Style)FindResource("Hint") });
                return;
            }
            foreach (CityResult city in found)
            {
                var button = new Button
                {
                    Content = city.Full,
                    Style = (Style)FindResource("FlatButton"),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                button.Click += async (_, _) =>
                {
                    App.Settings.WeatherCity = city.Name;
                    App.Settings.WeatherLat = city.Lat;
                    App.Settings.WeatherLon = city.Lon;
                    App.Settings.Save();
                    CityResults.Children.Clear();
                    CitySearchBox.Text = "";
                    WeatherCityText.Text = $"Сейчас: {city.Name}";
                    await App.Weather.RefreshAsync();
                };
                CityResults.Children.Add(button);
            }
        }
        catch (Exception ex)
        {
            CityResults.Children.Clear();
            CityResults.Children.Add(new TextBlock { Text = "Не получилось найти: " + ex.Message, Style = (Style)FindResource("Hint") });
        }
    }

    private void ApplyHotkey_Click(object sender, RoutedEventArgs e)
    {
        string text = HotkeyBox.Text.Trim();
        HotkeyStatus.Text = App.Pill.SetHotkey(text, out string error)
            ? $"Готово: {text}"
            : $"Не получилось: {error}";
    }

    private void ResetPill_Click(object sender, RoutedEventArgs e) => App.Pill.ResetPosition();

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Quit();
}

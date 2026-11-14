using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WikarIsland.Controls;
using WikarIsland.Services;
using Windows.Devices.Radios;

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
    private IntPtr _previousForeground;

    // перевоплощение пилюли в панель: анимируем область окна (SetWindowRgn)
    private const double FlushInset = 14;
    private readonly DispatcherTimer _morphTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
    private DateTime _morphStart;
    private Rect _morphFrom, _morphTo;
    private double _radiusFrom, _radiusTo;
    private Action? _morphDone;
    private bool _closing;
    private string? _pendingPower;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    public PanelWindow()
    {
        InitializeComponent();

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _closeTimer.Tick += (_, _) => CloseTick();

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tickTimer.Tick += (_, _) =>
        {
            UpdateHomeClock();
            UpdateProgress();
        };
        _morphTimer.Tick += (_, _) => MorphTick();

        Deactivated += OnDeactivated;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !DevicePopup.IsOpen) HideIsland();
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

    /// <summary>Клик по пилюле: открыто — закрыть, закрыто — открыть.</summary>
    public void TogglePill()
    {
        if (IsVisible) HideIsland();
        else ShowNearPill(activate: !App.Settings.OpenOnHover);
    }

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
        if (_closing)
        {
            // передумали закрывать — разворачиваем обратно
            _closing = false;
            StartMorph(CurrentMorphRect(), FullRect(), 17, 22, ClearRegion);
        }

        if (!IsVisible)
        {
            // запоминаем окно, где печатали, — туда вставляется запись из буфера
            if (!NativeMethods.ForegroundIsOurs()) _previousForeground = NativeMethods.GetForegroundWindow();
            if (TabHome.IsChecked != true && TabWeather.IsChecked != true && TabSettings.IsChecked != true
                && TabClipboard.IsChecked != true && TabCenter.IsChecked != true && !_views.ContainsKey(_current))
                SelectTab(App.Settings.LastTab);

            ApplyTabSize(_current);
            PositionNearPill();
            new WindowInteropHelper(this).EnsureHandle();
            Rect from = PillRectLocal();
            ApplyRegion(from, 17);
            UpdateCapsule();
            if (_current == "system") App.SystemMonitor.Start();
            _shownAt = DateTime.Now;
            _capsulePressed = false;
            Show();
            StartMorph(from, FullRect(), 17, 22, ClearRegion);
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
        if (!IsVisible || _closing) return;
        _closing = true;
        DevicePopup.IsOpen = false;
        PowerConfirm.Visibility = Visibility.Collapsed;
        StartMorph(FullRect(), PillRectLocal(), 22, 17, () =>
        {
            _closing = false;
            Hide();
            ClearRegion();
            App.SystemMonitor.Stop();
            App.Settings.Save();
        });
    }

    // ---------- перевоплощение ----------

    private Rect FullRect() => new(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);

    /// <summary>Где пилюля относительно панели (в DIP). У рамки экрана — вместе с «плечами» до верха.</summary>
    private Rect PillRectLocal()
    {
        Rect p = App.Pill.PillRect();
        var local = new Rect(p.Left - Left, p.Top - Top, Math.Max(p.Width, 60), Math.Max(p.Height, 30));
        if (App.Pill.IsFlush) local = new Rect(local.Left - 10, 0, local.Width + 20, local.Bottom);
        return local;
    }

    private Rect CurrentMorphRect()
    {
        double t = Math.Clamp((DateTime.Now - _morphStart).TotalMilliseconds / 230, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3);
        return Lerp(_morphFrom, _morphTo, e);
    }

    private static Rect Lerp(Rect a, Rect b, double t) => new(
        a.Left + (b.Left - a.Left) * t, a.Top + (b.Top - a.Top) * t,
        Math.Max(1, a.Width + (b.Width - a.Width) * t), Math.Max(1, a.Height + (b.Height - a.Height) * t));

    private void StartMorph(Rect from, Rect to, double radiusFrom, double radiusTo, Action done)
    {
        _morphFrom = from;
        _morphTo = to;
        _radiusFrom = radiusFrom;
        _radiusTo = radiusTo;
        _morphDone = done;
        _morphStart = DateTime.Now;
        _morphTimer.Start();
    }

    private void MorphTick()
    {
        double t = Math.Clamp((DateTime.Now - _morphStart).TotalMilliseconds / 230, 0, 1);
        double e = 1 - Math.Pow(1 - t, 3); // быстро в начале, мягко в конце
        ApplyRegion(Lerp(_morphFrom, _morphTo, e), _radiusFrom + (_radiusTo - _radiusFrom) * e);
        if (t < 1) return;

        _morphTimer.Stop();
        Action? done = _morphDone;
        _morphDone = null;
        done?.Invoke();
    }

    private void ApplyRegion(Rect r, double radius)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int d = (int)Math.Round(radius * 2 * dpi.DpiScaleX);
        IntPtr region = CreateRoundRectRgn(
            (int)Math.Round(r.Left * dpi.DpiScaleX), (int)Math.Round(r.Top * dpi.DpiScaleY),
            (int)Math.Round(r.Right * dpi.DpiScaleX) + 1, (int)Math.Round(r.Bottom * dpi.DpiScaleY) + 1, d, d);
        SetWindowRgn(hwnd, region, true); // окно забирает регион себе
    }

    private void ClearRegion()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    /// <summary>ИИ-чатам нужно место, остальным вкладкам — компактная панель.</summary>
    private void ApplyTabSize(string key)
    {
        bool wide = AiUrls.ContainsKey(key);
        Rect wa = ScreenHelper.ToDip(ScreenHelper.ScreenOf(App.Pill).WorkingArea, App.Pill);
        // «Главная» как на картинке: плеер слева, кнопки справа
        Width = Math.Min(wide ? 940 : key == "home" ? 780 : 620, wa.Width - 20);
        Height = Math.Min(wide ? 700 : 620, wa.Height - 10);
    }

    private void PositionNearPill()
    {
        PillWindow pill = App.Pill;
        System.Windows.Forms.Screen screen = ScreenHelper.ScreenOf(pill);
        Rect wa = ScreenHelper.ToDip(screen.WorkingArea, pill);
        Rect bounds = ScreenHelper.ToDip(screen.Bounds, pill);
        Rect p = pill.PillRect();

        double centerX = p.Left + p.Width / 2;
        Left = Math.Clamp(centerX - Width / 2, wa.Left + 6, Math.Max(wa.Left + 6, wa.Right - Width - 6));

        if (pill.IsFlush)
        {
            // панель вытекает из рамки экрана, как и пилюля; верхние углы уходят за край
            Top = bounds.Top - FlushInset;
            Capsule.Margin = new Thickness(0, 8 + FlushInset, 0, 4);
            return;
        }

        Capsule.Margin = new Thickness(0, 8, 0, 4);
        bool pillInUpperHalf = p.Top + p.Height / 2 < wa.Top + wa.Height / 2;
        double top = pillInUpperHalf
            ? p.Top - 6                 // панель накрывает пилюлю и растёт вниз
            : p.Bottom - Height + 6;    // пилюля внизу — растём вверх
        Top = Math.Clamp(top, wa.Top + 4, Math.Max(wa.Top + 4, wa.Bottom - Height - 4));
    }

    private void UpdateCapsule()
    {
        CapsuleTime.Text = DateTime.Now.ToString("HH:mm");
        CapsuleEyes.EyeStyle = App.Settings.EyeStyle;
        CapsuleEyes.Visibility = App.Settings.EyesEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool _capsulePressed;
    private DateTime _shownAt;

    /// <summary>
    /// Капсула стоит ровно там, где была пилюля. Нажатие на пилюлю открывает панель, а отпускание
    /// той же кнопки попадает уже на капсулу — поэтому сворачиваем только по клику, который начался на капсуле.
    /// </summary>
    private void Capsule_Down(object sender, MouseButtonEventArgs e) =>
        _capsulePressed = (DateTime.Now - _shownAt).TotalMilliseconds > 300;

    private void Capsule_Click(object sender, MouseButtonEventArgs e)
    {
        if (!_capsulePressed) return;
        _capsulePressed = false;
        HideIsland();
    }

    /// <summary>Открыт наведением и не активирован: прячем, когда курсор ушёл с острова.</summary>
    private void CloseTick()
    {
        Point c = ScreenHelper.CursorDip(this);
        var panelRect = new Rect(Left - 10, Top - 10, Width + 20, Height + 20);
        Rect pillRect = App.Pill.PillRect();
        pillRect.Inflate(8, 8);

        // режим «по клику»: если окно так и не стало активным, ловим клик мимо острова сами
        if (!App.Settings.OpenOnHover)
        {
            bool mouseDown = (NativeMethods.GetAsyncKeyState(0x01) & 0x8000) != 0
                             || (NativeMethods.GetAsyncKeyState(0x02) & 0x8000) != 0;
            if (!IsActive && mouseDown && !panelRect.Contains(c) && !pillRect.Contains(c)
                && !DevicePopup.IsOpen && !NativeMethods.ForegroundIsOurs())
                HideIsland();
            return;
        }

        if (IsActive)
        {
            _outsideSince = null;
            return;
        }

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
        if (key is not ("home" or "control" or "system" or "weather" or "clipboard" or "settings") && !AiUrls.ContainsKey(key)) key = "home";
        _current = key;

        RadioButton tab = key switch
        {
            "control" => TabCenter,
            "system" => TabSystem,
            "weather" => TabWeather,
            "clipboard" => TabClipboard,
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
        ClipboardView.Visibility = key == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        ControlView.Visibility = key == "control" ? Visibility.Visible : Visibility.Collapsed;
        SystemView.Visibility = key == "system" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "system")
        {
            App.SystemMonitor.Start();
            UpdateSystem();
        }
        else App.SystemMonitor.Stop();
        TimerView.Visibility = Visibility.Collapsed;
        NotesView.Visibility = Visibility.Collapsed;
        ZapretView.Visibility = Visibility.Collapsed;
        if (_currentNote != null) SaveCurrentNote();
        SettingsView.Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AiHost.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
        PopOutButton.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
        ReloadButton.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;

        foreach (KeyValuePair<string, WebView2> pair in _views)
            pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;

        if (isAi && !_views.ContainsKey(key)) _ = CreateAiViewAsync(key);
        if (key == "settings") LoadSettings();
        if (key == "weather") BuildWeatherView();
        if (key == "home") { UpdateHomeClock(); RefreshSound(); UpdateNotifications(); UpdateSteam(); UpdateTimer(); }
        if (key == "clipboard") UpdateClipboard();
        if (key == "control") { _ = RefreshControlAsync(); UpdateVpn(); UpdateZapret(); }
        if (key == "home") { BuildLauncher(); UpdateWeather(); UpdateNotesTile(); }

        if (IsVisible && !_closing)
        {
            double oldW = Width, oldH = Height;
            ApplyTabSize(key);
            if (oldW != Width || oldH != Height) PositionNearPill();
        }

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
        CapsuleTime.Text = DateTime.Now.ToString("HH:mm");
        if (_current != "home") return;
        string date = DateTime.Now.ToString("dddd, d MMMM", Ru);
        HomeDate.Text = char.ToUpper(date[0], Ru) + date[1..];
        HomeHint.Text = $"{App.Settings.Hotkey} — открыть остров • колёсико над пилюлей — громкость • клик по капсуле сверху — свернуть";
    }

    // ---------- главная: плеер ----------

    public void UpdateMedia()
    {
        MediaInfo? m = App.Media.Current;
        PlayerEmpty.Visibility = m == null ? Visibility.Visible : Visibility.Collapsed;
        PlayerContent.Visibility = m == null ? Visibility.Collapsed : Visibility.Visible;

        Color accent = m?.Accent ?? Color.FromRgb(0xF2, 0xF2, 0xF5);
        PlayerCard.Background = m?.Accent is Color a
            ? new LinearGradientBrush(Color.FromRgb((byte)(a.R * 0.42), (byte)(a.G * 0.42), (byte)(a.B * 0.42)),
                                      Color.FromRgb(0x17, 0x17, 0x1D), new Point(0, 0), new Point(1, 1))
            : (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1D));
        ProgressFill.Background = new SolidColorBrush(accent);
        if (m == null) return;

        CardCover.Source = m.Cover ?? m.AppIcon;
        CardAppIcon.Source = m.AppIcon;
        CardSource.Text = (string.IsNullOrWhiteSpace(m.AppName) ? "Музыка" : m.AppName).ToUpper(Ru);
        CardSource.ToolTip = m.AppId;
        CardTitle.Text = m.Title;
        CardArtist.Text = m.Artist;
        PlayPauseButton.Content = m.IsPlaying ? "\uE769" : "\uE768";
        UpdateProgress();
    }

    private void UpdateProgress()
    {
        MediaInfo? m = App.Media.Current;
        if (m == null || m.Duration <= TimeSpan.Zero)
        {
            ProgressFill.Width = 0;
            PosText.Text = RemainText.Text = "";
            return;
        }

        TimeSpan pos = m.LivePosition;
        double width = ProgressArea.ActualWidth;
        ProgressFill.Width = width > 0 ? width * Math.Clamp(pos / m.Duration, 0, 1) : 0;
        PosText.Text = FormatTime(pos);
        RemainText.Text = "-" + FormatTime(m.Duration - pos);
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
        MuteButton.Content = muted ? "\uE74F" : "\uE767";

        List<AudioDevice> devices = App.Audio.GetOutputDevices();
        AudioDevice? current = devices.FirstOrDefault(d => d.IsDefault);
        DeviceButtonText.Text = current != null ? ShortName(current.Name) : "Нет устройства";
        DeviceButton.ToolTip = current?.Name ?? "Устройства вывода не найдены";

        DevicesPanel.Children.Clear();
        foreach (AudioDevice device in devices)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = device.IsDefault ? "\uE73E" : "",
                Width = 22,
                FontFamily = (FontFamily)FindResource("Icons"),
                FontSize = 12,
                Foreground = (Brush)FindResource("Accent"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var name = new StackPanel();
            name.Children.Add(new TextBlock { Text = ShortName(device.Name), FontSize = 13.5, Foreground = (Brush)FindResource("TextMain") });
            if (ShortName(device.Name) != device.Name)
                name.Children.Add(new TextBlock { Text = device.Name, FontSize = 11, Foreground = (Brush)FindResource("TextDim") });
            row.Children.Add(name);

            var button = new Button
            {
                Content = row,
                Style = (Style)FindResource("FlatButton"),
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(8, 6, 12, 6),
            };
            button.Click += (_, _) =>
            {
                App.Audio.SetDefaultOutput(device.Id);
                DevicePopup.IsOpen = false;
                RefreshSound();
            };
            DevicesPanel.Children.Add(button);
        }
        if (devices.Count == 0)
            DevicesPanel.Children.Add(new TextBlock { Text = "Устройства вывода не найдены", Style = (Style)FindResource("Hint"), Margin = new Thickness(8) });
    }

    /// <summary>«Динамики (JBL Quantum350 Wireless)» → «JBL Quantum350 Wireless».</summary>
    private static string ShortName(string name)
    {
        int open = name.IndexOf('(');
        int close = name.LastIndexOf(')');
        if (open >= 0 && close > open + 1)
        {
            string inner = name[(open + 1)..close].Trim();
            // «Наушники (Oculus Virtual Audio Device)» — внутри скобок только тип драйвера, оставляем начало
            if (!inner.Contains("Audio Device", StringComparison.OrdinalIgnoreCase)) return inner;
            return name[..open].Trim();
        }
        return name;
    }

    private void DeviceButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshSound();
        DevicePopup.IsOpen = !DevicePopup.IsOpen;
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

    // ---------- буфер обмена ----------

    public void UpdateClipboard()
    {
        if (_current != "clipboard") return;
        ClipList.Children.Clear();
        string filter = ClipSearch.Text.Trim();

        IEnumerable<ClipItem> items = App.ClipboardHistory.Items
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.Time);
        if (filter.Length > 0)
            items = items.Where(i => i.Text?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true);

        int count = 0;
        foreach (ClipItem item in items)
        {
            count++;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement content = item.Image != null
                ? new Image { Source = item.Image, MaxHeight = 90, HorizontalAlignment = HorizontalAlignment.Left, Stretch = Stretch.Uniform }
                : new TextBlock
                {
                    Text = item.Text!.Length > 400 ? item.Text[..400] : item.Text,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxHeight = 40,
                    FontSize = 13.5,
                    Foreground = (Brush)FindResource("TextMain"),
                    FontFamily = (FontFamily)FindResource("UiFont"),
                };
            grid.Children.Add(content);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, 0, 0, 0) };
            var pin = IconButton(item.Pinned ? "\uE77A" : "\uE718", item.Pinned ? "Открепить" : "Закрепить");
            if (item.Pinned) pin.Foreground = (Brush)FindResource("Accent");
            pin.Click += (_, e) => { App.ClipboardHistory.TogglePin(item); e.Handled = true; };
            var del = IconButton("\uE74D", "Удалить");
            del.Click += (_, e) => { App.ClipboardHistory.Remove(item); e.Handled = true; };
            buttons.Children.Add(pin);
            buttons.Children.Add(del);
            Grid.SetColumn(buttons, 1);
            grid.Children.Add(buttons);

            var card = new Border
            {
                Child = grid,
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 8, 9),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
                ToolTip = "Нажми, чтобы вставить",
            };
            card.MouseEnter += (_, _) => card.Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
            card.MouseLeave += (_, _) => card.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x22));
            card.MouseLeftButtonUp += async (_, _) =>
            {
                HideIsland();
                await App.ClipboardHistory.PasteAsync(item, _previousForeground);
            };
            ClipList.Children.Add(card);
        }

        if (count == 0)
            ClipList.Children.Add(Text(filter.Length > 0 ? "Ничего не нашлось" : "Скопируй что-нибудь (Ctrl+C) — оно появится здесь.", 13.5, dim: true));
    }

    private Button IconButton(string glyph, string tip) => new()
    {
        Content = glyph,
        ToolTip = tip,
        Style = (Style)FindResource("FlatButton"),
        FontFamily = (FontFamily)FindResource("Icons"),
        FontSize = 13,
        Padding = new Thickness(8, 5, 8, 5),
        Background = Brushes.Transparent,
    };

    private void ClipSearch_TextChanged(object sender, TextChangedEventArgs e) => UpdateClipboard();

    private void ClipClear_Click(object sender, RoutedEventArgs e) => App.ClipboardHistory.ClearUnpinned();

    // ---------- Steam ----------

    public void UpdateSteam()
    {
        if (_current != "home") return;
        SteamService steam = App.Steam;
        bool any = steam.RunningAppId != 0 || steam.Downloads.Count > 0;
        SteamCard.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;

        SteamCardIcon.Source = steam.SteamIcon;
        SteamList.Children.Clear();
        if (steam.RunningAppId != 0)
        {
            TimeSpan t = DateTime.Now - steam.RunningSince;
            SteamList.Children.Add(Text($"▶ Играешь в {steam.RunningName} · {(int)t.TotalHours} ч {t.Minutes:00} мин", 14));
        }
        foreach (SteamDownload d in steam.Downloads)
        {
            var row = new StackPanel { Margin = new Thickness(0, 8, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.Children.Add(Text(d.Total > 0
                ? $"{d.Name} — {d.Progress * 100:0}% ({d.Done / 1073741824.0:0.0} из {d.Total / 1073741824.0:0.0} ГБ)"
                : $"{d.Name} — готовится к загрузке", 13.5));
            var bar = new Grid { Height = 4, Margin = new Thickness(0, 5, 0, 0) };
            bar.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(2) });
            bar.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x66, 0xC0, 0xF4)),
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(0, 560 * d.Progress),
            });
            row.Children.Add(bar);
            row.ToolTip = "Открыть загрузки Steam";
            row.MouseLeftButtonUp += (_, _) => SteamService.OpenDownloads();
            SteamList.Children.Add(row);
        }
    }

    // ---------- таймер выключения ----------

    public void UpdateTimer()
    {
        PowerTimerService timer = App.PowerTimer;
        TimerTileState.Visibility = timer.Active ? Visibility.Visible : Visibility.Collapsed;
        if (timer.AfterTrack) TimerTileState.Text = "после трека";
        else if (timer.Target != null)
        {
            TimeSpan left = timer.Remaining;
            TimerTileState.Text = left.TotalHours >= 1 ? $"через {(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}" : $"через {left.Minutes}:{left.Seconds:00}";
        }
        TimerStatusRow.Visibility = timer.Active ? Visibility.Visible : Visibility.Collapsed;
        TimerHint.Visibility = timer.Active ? Visibility.Collapsed : Visibility.Visible;
        if (!timer.Active) return;

        string action = PowerTimerService.ActionName(timer.Action);
        if (timer.AfterTrack)
        {
            TimerStatus.Text = $"{action} после текущего трека";
        }
        else
        {
            TimeSpan r = timer.Remaining;
            string left = r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes}:{r.Seconds:00}";
            TimerStatus.Text = $"{action} через {left} (в {timer.Target:HH:mm})";
        }
    }

    private PowerAction SelectedAction() =>
        ActRestart.IsChecked == true ? PowerAction.Restart
        : ActSleep.IsChecked == true ? PowerAction.Sleep
        : ActHibernate.IsChecked == true ? PowerAction.Hibernate
        : PowerAction.Shutdown;

    private void TimerPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string minutes })
            App.PowerTimer.StartIn(TimeSpan.FromMinutes(int.Parse(minutes)), SelectedAction());
    }

    private void TimerAfterTrack_Click(object sender, RoutedEventArgs e)
    {
        if (App.Media.Current == null)
        {
            TimerHint.Text = "Сейчас ничего не играет — включи музыку и нажми ещё раз.";
            return;
        }
        App.PowerTimer.StartAfterTrack(SelectedAction());
    }

    private void TimerCustomBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) TimerCustom_Click(sender, e);
    }

    private void TimerCustom_Click(object sender, RoutedEventArgs e)
    {
        string text = TimerCustomBox.Text.Trim();
        if (TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out TimeSpan at))
        {
            DateTime target = DateTime.Today + at;
            if (target <= DateTime.Now) target = target.AddDays(1);
            App.PowerTimer.StartIn(target - DateTime.Now, SelectedAction());
        }
        else if (int.TryParse(text, out int minutes) && minutes > 0 && minutes < 24 * 60)
        {
            App.PowerTimer.StartIn(TimeSpan.FromMinutes(minutes), SelectedAction());
        }
        else
        {
            TimerHint.Visibility = Visibility.Visible;
            TimerHint.Text = "Не понял время. Напиши число минут (45) или время (01:30).";
            return;
        }
        TimerCustomBox.Text = "";
    }

    private void TimerPlus_Click(object sender, RoutedEventArgs e) => App.PowerTimer.AddMinutes(15);

    private void TimerCancel_Click(object sender, RoutedEventArgs e) => App.PowerTimer.Cancel();

    // ---------- уведомления ----------

    public void UpdateNotifications()
    {
        NotificationsList.Children.Clear();
        if (!App.Notifications.Available)
        {
            NotificationsList.Children.Add(Text("Не нашёл базу уведомлений Windows — эта функция здесь не работает.", 13, dim: true));
            return;
        }

        int shown = 0;
        foreach (NotificationItem n in App.Notifications.History)
        {
            if (shown++ >= 6) break;

            var row = new Grid { Margin = new Thickness(0, 4, 0, 4), Cursor = Cursors.Hand, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new Image { Source = n.AppIcon, Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top });
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock
            {
                Text = n.Title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextMain"), FontFamily = (FontFamily)FindResource("UiFont"),
            });
            if (!string.IsNullOrEmpty(n.Body))
                texts.Children.Add(new TextBlock
                {
                    Text = n.Body, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = (Brush)FindResource("TextDim"), FontFamily = (FontFamily)FindResource("UiFont"),
                });
            Grid.SetColumn(texts, 1);
            row.Children.Add(texts);
            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            var time = Text(n.Time.ToString("HH:mm"), 12, dim: true);
            time.Margin = new Thickness(10, 1, 4, 0);
            right.Children.Add(time);
            var close = IconButton("\uE711", "Закрыть уведомление");
            close.FontSize = 10;
            close.Padding = new Thickness(6, 3, 6, 3);
            close.Click += (_, e) =>
            {
                App.Notifications.Dismiss(n);
                e.Handled = true;
            };
            right.Children.Add(close);
            Grid.SetColumn(right, 2);
            row.Children.Add(right);

            row.ToolTip = $"{n.AppName}: нажми, чтобы открыть";
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (n.IsDiscord) App.Notifications.ResetDiscord();
                n.OpenApp();
                HideIsland();
            };
            NotificationsList.Children.Add(row);
        }

        ClearNotificationsButton.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (shown == 0)
            NotificationsList.Children.Add(Text("Пока пусто. Новые уведомления (Discord, мессенджеры, почта) появятся здесь и карточкой на пилюле.", 13, dim: true));
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e) => App.Notifications.ClearHistory();

    private void BuildNotificationApps()
    {
        NotificationApps.Children.Clear();
        AppSettings s = App.Settings;
        foreach (KeyValuePair<string, string> app in s.KnownNotificationApps)
        {
            var box = new CheckBox
            {
                Content = app.Value,
                ToolTip = app.Key,
                IsChecked = !s.MutedNotificationApps.Contains(app.Key),
                Style = (Style)FindResource("SettingsCheck"),
                Margin = new Thickness(0, 3, 0, 3),
            };
            box.Click += (_, _) =>
            {
                if (box.IsChecked == true) s.MutedNotificationApps.Remove(app.Key);
                else if (!s.MutedNotificationApps.Contains(app.Key)) s.MutedNotificationApps.Add(app.Key);
                s.Save();
            };
            NotificationApps.Children.Add(box);
        }
        if (s.KnownNotificationApps.Count == 0)
            NotificationApps.Children.Add(Text("Список появится, когда придут первые уведомления.", 13, dim: true));
    }

    // ---------- погода ----------

    public void UpdateWeather()
    {
        WeatherData? w = App.Weather.Current;
        HomeWeatherIcon.Visibility = w == null ? Visibility.Collapsed : Visibility.Visible;
        if (w != null) HomeWeatherIcon.Symbol = w.Symbol;
        HomeWeather.Text = w == null
            ? (App.Settings.WeatherLat == null ? "Погода: выбери город в настройках" : "Погода загружается…")
            : $"{w.TempText}  {w.Text} · {w.City}";
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
                ? "Город не выбран. Открой настройки → «Погода» и найди свой город."
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

        WeatherContent.Children.Add(Text(w.City, 15, dim: true));

        var now = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        now.Children.Add(new WeatherIcon { Symbol = w.Symbol, Width = 76, Height = 76 });
        now.Children.Add(new TextBlock
        {
            Text = w.TempText, FontSize = 48, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 0, 0, 0),
            Foreground = (Brush)FindResource("TextMain"), VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        });
        var details = new StackPanel { Margin = new Thickness(20, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(Text(w.Text, 16));
        details.Children.Add(Text($"Ощущается как {Math.Round(w.Feels):0}°", 13, dim: true));
        details.Children.Add(Text($"Ветер {w.Wind:0.#} м/с · влажность {w.Humidity:0}%", 13, dim: true));
        if (w.Precipitation > 0) details.Children.Add(Text($"Осадки {w.Precipitation:0.#} мм/ч", 13, dim: true));
        now.Children.Add(details);
        WeatherContent.Children.Add(now);

        // по часам
        WeatherContent.Children.Add(Text("По часам", 14, bold: true, top: 12));
        var hours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        foreach (HourForecast h in w.Hours)
        {
            var cell = new StackPanel { Width = 52 };
            cell.Children.Add(Text(h.Time.ToString("HH:mm"), 11.5, dim: true, center: true));
            cell.Children.Add(new WeatherIcon { Symbol = h.Symbol, Width = 28, Height = 28, Margin = new Thickness(0, 4, 0, 4) });
            cell.Children.Add(Text($"{Math.Round(h.Temp):0}°", 13.5, center: true));
            hours.Children.Add(cell);
        }
        WeatherContent.Children.Add(new ScrollViewer
        {
            Content = hours,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        // по дням
        WeatherContent.Children.Add(Text("На неделю", 14, bold: true, top: 16));
        foreach (DayForecast d in w.Days)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string day = d.Date.Date == DateTime.Today ? "Сегодня"
                : d.Date.Date == DateTime.Today.AddDays(1) ? "Завтра"
                : d.Date.ToString("ddd, d MMM", Ru);
            var c0 = Text(day, 13.5);
            var c1 = new WeatherIcon { Symbol = d.Symbol, Width = 24, Height = 24 };
            var c2 = Text(WeatherSymbols.Text(d.Symbol), 13, dim: true);
            c2.TextTrimming = TextTrimming.CharacterEllipsis;
            c2.TextWrapping = TextWrapping.NoWrap;
            var c3 = Text($"{Math.Round(d.Max):0}° / {Math.Round(d.Min):0}°", 13.5, bold: true);
            Grid.SetColumn(c1, 1);
            Grid.SetColumn(c2, 2);
            Grid.SetColumn(c3, 3);
            row.Children.Add(c0);
            row.Children.Add(c1);
            row.Children.Add(c2);
            row.Children.Add(c3);
            WeatherContent.Children.Add(row);
        }

        WeatherContent.Children.Add(Text($"Обновлено в {w.Updated:HH:mm} · данные MET Norway", 11.5, dim: true, top: 14));
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
        AutoStartAdminBox.IsChecked = s.AutoStartElevated;
        AutoStartAdminBox.IsEnabled = s.AutoStart;
        AutoHideBox.IsChecked = s.AutoHide;
        OpenOnHoverBox.IsChecked = s.OpenOnHover;
        EyesBox.IsChecked = s.EyesEnabled;
        ModeBoth.IsChecked = s.PillMode == "both";
        ModeEyes.IsChecked = s.PillMode == "eyes";
        ModeClock.IsChecked = s.PillMode == "clock";
        MusicPillBox.IsChecked = s.MusicInPill;
        WeatherPillBox.IsChecked = s.WeatherInPill;
        HotkeyBox.Text = s.Hotkey;
        NotificationsBox.IsChecked = s.NotificationsEnabled;
        BuildVpnServers();
        VpnDnsBox.Text = s.VpnDns;
        UpdateZapret();
        BuildEyeStyles();
        BuildNotificationApps();
        WeatherCityText.Text = s.WeatherCity != null ? $"Сейчас: {s.WeatherCity}" : "Город не выбран";
        _loadingSettings = false;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings s = App.Settings;

        bool autoStart = AutoStartBox.IsChecked == true;
        bool elevated = AutoStartAdminBox.IsChecked == true;
        if (autoStart != s.AutoStart || elevated != s.AutoStartElevated) AutoStart.Apply(autoStart, elevated);
        s.AutoStart = autoStart;
        s.AutoStartElevated = elevated;
        AutoStartAdminBox.IsEnabled = autoStart;
        s.AutoHide = AutoHideBox.IsChecked == true;
        s.OpenOnHover = OpenOnHoverBox.IsChecked == true;
        s.EyesEnabled = EyesBox.IsChecked == true;
        s.PillMode = ModeEyes.IsChecked == true ? "eyes" : ModeClock.IsChecked == true ? "clock" : "both";
        s.MusicInPill = MusicPillBox.IsChecked == true;
        s.WeatherInPill = WeatherPillBox.IsChecked == true;
        s.NotificationsEnabled = NotificationsBox.IsChecked == true;

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

    // ---------- стили глаз ----------

    private void BuildEyeStyles()
    {
        EyeStylesPanel.Children.Clear();
        foreach (string style in EyesControl.Styles)
        {
            bool selected = App.Settings.EyeStyle == style;
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            var eyes = new EyesControl { EyeStyle = style, HorizontalAlignment = HorizontalAlignment.Center };
            eyes.LayoutTransform = new ScaleTransform(1.4, 1.4);
            content.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 7, 10, 7),
                Child = eyes,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            content.Children.Add(new TextBlock
            {
                Text = EyesControl.StyleName(style), FontSize = 12, Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)FindResource(selected ? "TextMain" : "TextDim"),
            });

            var tile = new Border
            {
                Child = content,
                Width = 100,
                Padding = new Thickness(6, 10, 6, 8),
                Margin = new Thickness(0, 0, 8, 8),
                CornerRadius = new CornerRadius(12),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(selected ? Color.FromRgb(0x2A, 0x30, 0x52) : Color.FromRgb(0x1C, 0x1C, 0x22)),
                BorderBrush = selected ? (Brush)FindResource("Accent") : Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
            };
            tile.MouseLeftButtonUp += (_, _) =>
            {
                App.Settings.EyeStyle = style;
                App.Settings.Save();
                App.Pill.ApplySettings();
                UpdateCapsule();
                BuildEyeStyles();
            };
            EyeStylesPanel.Children.Add(tile);
        }
    }

    // ---------- ярлыки ----------

    private void BuildLauncher()
    {
        LauncherPanel.Children.Clear();
        foreach (LauncherItem item in App.Settings.Launcher.ToList())
        {
            var tile = new Button
            {
                Style = (Style)FindResource("FlatButton"),
                Width = 54,
                Height = 54,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x25)),
                Content = new Image { Source = ShellHelper.GetIcon(item.Path), Width = 34, Height = 34 },
                ToolTip = item.Name,
            };
            tile.Click += (_, _) =>
            {
                HideIsland();
                ShellHelper.Open(item.Path);
            };

            var menu = new ContextMenu();
            var open = new MenuItem { Header = "Открыть" };
            open.Click += (_, _) => ShellHelper.Open(item.Path);
            var show = new MenuItem { Header = "Показать в папке" };
            show.Click += (_, _) => ShellHelper.ShowInExplorer(item.Path);
            var remove = new MenuItem { Header = "Убрать ярлык" };
            remove.Click += (_, _) =>
            {
                App.Settings.Launcher.Remove(item);
                App.Settings.Save();
                BuildLauncher();
            };
            menu.Items.Add(open);
            menu.Items.Add(show);
            menu.Items.Add(new Separator());
            menu.Items.Add(remove);
            tile.ContextMenu = menu;
            LauncherPanel.Children.Add(tile);
        }

        // плитка «+»
        var plusContent = new Grid { Width = 54, Height = 54 };
        plusContent.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            StrokeDashArray = new DoubleCollection { 3, 3 },
            StrokeThickness = 1.2,
            RadiusX = 10,
            RadiusY = 10,
        });
        plusContent.Children.Add(new TextBlock
        {
            Text = "\uE710", FontFamily = (FontFamily)FindResource("Icons"), FontSize = 16,
            Foreground = (Brush)FindResource("TextDim"),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        var plus = new Button
        {
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 8, 8),
            Background = Brushes.Transparent,
            Content = plusContent,
            ToolTip = "Добавить ярлык (или перетащи сюда файл, папку, программу)",
        };
        var addMenu = new ContextMenu();
        var addFile = new MenuItem { Header = "Программа или файл…" };
        addFile.Click += (_, _) => AddLauncherFromDialog(folder: false);
        var addFolder = new MenuItem { Header = "Папка…" };
        addFolder.Click += (_, _) => AddLauncherFromDialog(folder: true);
        addMenu.Items.Add(addFile);
        addMenu.Items.Add(addFolder);
        plus.ContextMenu = addMenu;
        plus.Click += (_, _) =>
        {
            addMenu.PlacementTarget = plus;
            addMenu.IsOpen = true;
        };
        LauncherPanel.Children.Add(plus);

        DesktopIconsButton.Content = ShellHelper.DesktopIconsVisible ? "Скрыть значки стола" : "Показать значки стола";
    }

    private void AddLauncherFromDialog(bool folder)
    {
        if (folder)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка для ярлыка", Multiselect = true };
            if (dialog.ShowDialog(this) == true)
                foreach (string path in dialog.FolderNames) AddLauncher(path);
        }
        else
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Программа или файл для ярлыка",
                Filter = "Программы и ярлыки|*.exe;*.lnk;*.url;*.bat;*.cmd|Все файлы|*.*",
                Multiselect = true,
                DereferenceLinks = false,
            };
            if (dialog.ShowDialog(this) == true)
                foreach (string path in dialog.FileNames) AddLauncher(path);
        }
    }

    private void AddLauncher(string path)
    {
        if (App.Settings.Launcher.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        App.Settings.Launcher.Add(new LauncherItem { Path = path, Name = ShellHelper.DisplayName(path) });
        App.Settings.Save();
        BuildLauncher();
    }

    private void Launcher_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void Launcher_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (string path in paths) AddLauncher(path);
    }

    private void DesktopIcons_Click(object sender, RoutedEventArgs e)
    {
        ShellHelper.SetDesktopIcons(!ShellHelper.DesktopIconsVisible);
        BuildLauncher();
        _ = RefreshControlAsync();
    }

    // ---------- центр управления ----------

    private static readonly Brush TileOff = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x25));
    private static readonly Brush TileOn = new SolidColorBrush(Color.FromRgb(0x3D, 0x5A, 0xE0));

    private async Task RefreshControlAsync()
    {
        Radio? wifi = await RadioService.FindAsync(RadioKind.WiFi);
        Radio? bt = await RadioService.FindAsync(RadioKind.Bluetooth);

        SetTile(WifiTile, WifiState, wifi);
        SetTile(BluetoothTile, BluetoothState, bt);

        bool icons = ShellHelper.DesktopIconsVisible;
        IconsTile.Background = icons ? TileOff : TileOn;
        IconsState.Text = icons ? "Видны" : "Спрятаны";

        RadioHint.Visibility = wifi == null && bt == null ? Visibility.Visible : Visibility.Collapsed;
        RadioHint.Text = "Windows не дала доступ к Wi-Fi и Bluetooth. Проверь: Параметры → Конфиденциальность → Радио → разрешить приложениям управлять радио.";
    }

    private static void SetTile(Button tile, TextBlock state, Radio? radio)
    {
        if (radio == null)
        {
            tile.Background = TileOff;
            tile.Opacity = 0.5;
            state.Text = "Нет устройства";
            return;
        }
        bool on = radio.State == RadioState.On;
        tile.Opacity = 1;
        tile.Background = on ? TileOn : TileOff;
        state.Text = on ? "Включён" : "Выключен";
    }

    private async void WifiTile_Click(object sender, RoutedEventArgs e)
    {
        await RadioService.ToggleAsync(RadioKind.WiFi);
        await RefreshControlAsync();
    }

    private async void BluetoothTile_Click(object sender, RoutedEventArgs e)
    {
        await RadioService.ToggleAsync(RadioKind.Bluetooth);
        await RefreshControlAsync();
    }

    private void Power_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        switch (action)
        {
            case "lock":
                HideIsland();
                LockWorkStation();
                break;
            case "sleep":
                HideIsland();
                PowerTimerService.Execute(PowerAction.Sleep);
                break;
            default:
                // выключение и перезагрузка — только после подтверждения
                _pendingPower = action;
                PowerConfirmText.Text = action == "restart"
                    ? "Перезагрузить компьютер сейчас? Несохранённые данные в программах пропадут."
                    : "Выключить компьютер сейчас? Несохранённые данные в программах пропадут.";
                PowerConfirmButton.Content = action == "restart" ? "Перезагрузить" : "Выключить";
                PowerConfirm.Visibility = Visibility.Visible;
                break;
        }
    }

    private void PowerConfirm_Click(object sender, RoutedEventArgs e)
    {
        PowerConfirm.Visibility = Visibility.Collapsed;
        PowerAction action = _pendingPower == "restart" ? PowerAction.Restart : PowerAction.Shutdown;
        _pendingPower = null;
        PowerTimerService.Execute(action);
    }

    private void PowerCancel_Click(object sender, RoutedEventArgs e)
    {
        _pendingPower = null;
        PowerConfirm.Visibility = Visibility.Collapsed;
    }

    // ---------- быстрые действия ----------

    private void ShowSubView(FrameworkElement view)
    {
        HomeView.Visibility = Visibility.Collapsed;
        TimerView.Visibility = view == TimerView ? Visibility.Visible : Visibility.Collapsed;
        NotesView.Visibility = view == NotesView ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        TimerView.Visibility = Visibility.Collapsed;
        NotesView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        UpdateNotesTile();
    }

    private void TimerTile_Click(object sender, RoutedEventArgs e)
    {
        ShowSubView(TimerView);
        UpdateTimer();
    }

    private async void RecordTile_Click(object sender, RoutedEventArgs e)
    {
        if (App.Recorder.IsRecording)
        {
            App.Recorder.Stop();
            return;
        }
        HideIsland();
        await Task.Delay(400); // даём панели свернуться, чтобы она не попала в кадр
        App.Recorder.Start(ScreenHelper.ScreenOf(App.Pill));
    }

    private void AudioTile_Click(object sender, RoutedEventArgs e)
    {
        if (App.AudioRecorder.IsRecording)
        {
            App.AudioRecorder.Stop();
            return;
        }
        if (App.Recorder.IsRecording) return; // экран уже пишется со звуком

        var menu = new ContextMenu { PlacementTarget = AudioTile, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach ((AudioSourceKind kind, string title) in new[]
                 {
                     (AudioSourceKind.Speakers, "Колонки — что слышно"),
                     (AudioSourceKind.Microphone, "Микрофон"),
                     (AudioSourceKind.Both, "Колонки + микрофон"),
                 })
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => App.AudioRecorder.Start(kind);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    public void UpdateRecording()
    {
        bool audio = App.AudioRecorder.IsRecording;
        AudioTileText.Text = audio ? "Остановить запись" : "Запись звука";
        AudioTileIcon.Text = audio ? "\uE71A" : "\uE720";
        AudioTileState.Visibility = audio ? Visibility.Visible : Visibility.Collapsed;
        AudioTileState.Text = audio ? AudioRecorderService.SourceName(App.AudioRecorder.Source) : "";

        bool rec = App.Recorder.IsRecording;
        RecordTileText.Text = rec ? "Остановить запись" : "Запись экрана";
        RecordTileIcon.Text = rec ? "\uE71A" : "\uE714";
        RecordTileState.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void AreaTile_Click(object sender, RoutedEventArgs e)
    {
        HideIsland();
        await Task.Delay(350);
        ScreenTools.WinShift('S');
    }

    private async void FullTile_Click(object sender, RoutedEventArgs e)
    {
        HideIsland();
        await Task.Delay(400);
        try
        {
            string path = ScreenTools.TakeFullScreenshot();
            App.Pill.ShowNotification(new NotificationItem
            {
                AppId = "screenshot",
                AppName = "Скриншот",
                AppIcon = ShellHelper.GetIcon(path),
                Title = "Скриншот сохранён и скопирован",
                Body = System.IO.Path.GetFileName(path) + " — нажми, чтобы открыть",
                Time = DateTime.Now,
                Click = () => ShellHelper.Open(path),
            });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    // ---------- заметки ----------

    private Note? _currentNote;
    private DispatcherTimer? _noteSaveTimer;

    private void UpdateNotesTile()
    {
        int count = App.Notes.Notes.Count(n => !string.IsNullOrWhiteSpace(n.Text));
        NotesTileState.Text = count == 0 ? "пусто" : $"{count} шт.";
    }

    private void NotesTile_Click(object sender, RoutedEventArgs e)
    {
        ShowSubView(NotesView);
        ShowNotesList();
    }

    private void ShowNotesList()
    {
        if (_currentNote != null) SaveCurrentNote();
        _currentNote = null;
        NoteEditor.Visibility = Visibility.Collapsed;
        NotesListScroll.Visibility = Visibility.Visible;
        NoteDeleteButton.Visibility = Visibility.Collapsed;
        NoteNewButton.Visibility = Visibility.Visible;
        NotesHeader.Text = "Заметки";

        App.Notes.Notes.RemoveAll(n => string.IsNullOrWhiteSpace(n.Text));
        NotesList.Children.Clear();
        foreach (Note note in App.Notes.Notes.OrderByDescending(n => n.Updated))
        {
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock
            {
                Text = note.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextMain"), FontFamily = (FontFamily)FindResource("UiFont"),
            });
            texts.Children.Add(new TextBlock
            {
                Text = (note.Updated.Date == DateTime.Today ? note.Updated.ToString("HH:mm") : note.Updated.ToString("d MMM", Ru))
                       + (note.Preview.Length > 0 ? "  " + note.Preview : ""),
                FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextDim"), FontFamily = (FontFamily)FindResource("UiFont"),
            });
            var card = new Border
            {
                Child = texts,
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1D)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 0, 0, 6),
                Cursor = Cursors.Hand,
            };
            card.MouseLeftButtonUp += (_, _) => OpenNote(note);
            NotesList.Children.Add(card);
        }
        if (NotesList.Children.Count == 0)
            NotesList.Children.Add(Text("Заметок пока нет. Нажми «+ Новая».", 13.5, dim: true));
        UpdateNotesTile();
    }

    private void OpenNote(Note note)
    {
        _currentNote = note;
        NotesListScroll.Visibility = Visibility.Collapsed;
        NoteEditor.Visibility = Visibility.Visible;
        NoteDeleteButton.Visibility = Visibility.Visible;
        NoteNewButton.Visibility = Visibility.Collapsed;
        NotesHeader.Text = note.Updated.ToString("d MMMM, HH:mm", Ru);
        NoteEditor.Text = note.Text;
        NoteEditor.CaretIndex = NoteEditor.Text.Length;
        Activate();
        NoteEditor.Focus();
    }

    private void NoteNew_Click(object sender, RoutedEventArgs e) => OpenNote(App.Notes.Create());

    private void NoteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote == null) return;
        Note note = _currentNote;
        _currentNote = null;
        App.Notes.Delete(note);
        ShowNotesList();
    }

    private void NotesBack_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote != null) ShowNotesList();
        else Back_Click(sender, e);
    }

    private void NoteEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_currentNote == null) return;
        _currentNote.Text = NoteEditor.Text;
        _currentNote.Updated = DateTime.Now;
        // сохраняем через секунду после последнего нажатия
        _noteSaveTimer ??= CreateNoteSaveTimer();
        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    private DispatcherTimer CreateNoteSaveTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            App.Notes.Save();
        };
        return t;
    }

    private void SaveCurrentNote()
    {
        _noteSaveTimer?.Stop();
        App.Notes.Save();
    }

    // ---------- система ----------

    public void UpdateSystem()
    {
        if (_current != "system" || !IsVisible) return;
        SystemContent.Children.Clear();
        SystemSnapshot? snap = App.SystemMonitor.Current;

        if (snap == null)
        {
            SystemContent.Children.Add(Text("Собираю данные с датчиков… первый раз это занимает пару секунд.", 13.5, dim: true, top: 6));
            return;
        }

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        grid.Children.Add(ChipCard("Процессор", "\uE950", snap.Cpu, null));
        grid.Children.Add(ChipCard("Видеокарта", "\uE7F4", snap.Gpu, null));
        SystemContent.Children.Add(grid);

        // оперативная память
        var ram = new StackPanel();
        ram.Children.Add(CardHeader("\uE964", "Оперативная память"));
        double ramPct = snap.RamTotalGb > 0 ? snap.RamUsedGb / snap.RamTotalGb * 100 : 0;
        var ramRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var ramText = Text($"{snap.RamUsedGb:0.0} из {snap.RamTotalGb:0.0} ГБ", 13.5, dim: true);
        DockPanel.SetDock(ramText, Dock.Right);
        ramRow.Children.Add(ramText);
        ramRow.Children.Add(Text($"{ramPct:0}%", 22, bold: true));
        ram.Children.Add(ramRow);
        ram.Children.Add(Bar(ramPct, LoadColor(ramPct)));
        SystemContent.Children.Add(Card(ram));

        // все датчики температуры
        var temps = new StackPanel();
        temps.Children.Add(CardHeader("\uE9CA", "Температуры по датчикам"));
        if (snap.Temperatures.Count > 0)
        {
            double max = snap.Temperatures.Max(t => t.Celsius);
            TempSensor hottest = snap.Temperatures.First(t => t.Celsius == max);
            var summary = Text($"Самое горячее: {hottest.Group} — {hottest.Name}, {max:0}°", 13, dim: true);
            summary.Margin = new Thickness(0, 0, 0, 6);
            temps.Children.Add(summary);
            foreach (IGrouping<string, TempSensor> group in snap.Temperatures.GroupBy(t => t.Group))
            {
                temps.Children.Add(Text(group.Key, 12.5, bold: true, top: 8));
                foreach (TempSensor t in group.OrderByDescending(t => t.Celsius))
                {
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 0) };
                    var value = new TextBlock
                    {
                        Text = $"{t.Celsius:0}°", FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(TempColor(t.Celsius)),
                    };
                    DockPanel.SetDock(value, Dock.Right);
                    row.Children.Add(value);
                    row.Children.Add(Text(t.Name, 13, dim: true));
                    temps.Children.Add(row);
                }
            }
        }
        else
        {
            temps.Children.Add(Text("Датчики температуры недоступны.", 13, dim: true));
        }
        SystemContent.Children.Add(Card(temps));

        // подсказка про права администратора
        if (!snap.IsAdmin || snap.Cpu?.Temperature == null)
        {
            var hint = new StackPanel();
            hint.Children.Add(Text(snap.IsAdmin
                ? "Температура процессора не читается. Установи драйвер PawnIO (через него LibreHardwareMonitor читает датчики CPU) и перезапусти остров."
                : "Температуру процессора и часть датчиков платы Windows отдаёт только программам с правами администратора (и с установленным драйвером PawnIO).", 13, dim: true));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            if (!snap.IsAdmin)
            {
                var admin = new Button { Content = "Перезапустить от администратора", Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0, 0, 8, 0) };
                admin.Click += (_, _) =>
                {
                    if (SystemMonitorService.RestartAsAdmin()) App.Quit();
                };
                buttons.Children.Add(admin);
            }
            var pawn = new Button { Content = "Скачать PawnIO", Style = (Style)FindResource("FlatButton"), Margin = new Thickness(0) };
            pawn.Click += (_, _) => ShellHelper.Open("https://github.com/namazso/PawnIO.Setup/releases");
            buttons.Children.Add(pawn);
            hint.Children.Add(buttons);
            SystemContent.Children.Add(Card(hint));
        }
    }

    private Border Card(UIElement child) => new()
    {
        Child = child,
        Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1D)),
        CornerRadius = new CornerRadius(16),
        Padding = new Thickness(14, 12, 14, 12),
        Margin = new Thickness(0, 0, 0, 10),
    };

    private StackPanel CardHeader(string glyph, string title)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 14,
            Foreground = (Brush)FindResource("TextDim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0),
        });
        header.Children.Add(Text(title, 13.5, bold: true));
        return header;
    }

    private Border ChipCard(string title, string glyph, ChipStats? chip, string? _)
    {
        var body = new StackPanel();
        body.Children.Add(CardHeader(glyph, title));
        if (chip == null)
        {
            body.Children.Add(Text("Нет данных", 13, dim: true));
            var empty = Card(body);
            empty.Margin = new Thickness(0, 0, title == "Процессор" ? 5 : 0, 10);
            empty.Margin = new Thickness(title == "Процессор" ? 0 : 5, 0, title == "Процессор" ? 5 : 0, 10);
            return empty;
        }

        var name = Text(chip.Name, 11.5, dim: true);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        name.ToolTip = chip.Name;
        body.Children.Add(name);

        // температура крупно
        var temp = new TextBlock
        {
            Text = chip.Temperature is double t ? $"{t:0}°" : "—°",
            FontSize = 34, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0),
            Foreground = new SolidColorBrush(chip.Temperature is double tc ? TempColor(tc) : Color.FromRgb(0xA0, 0xA0, 0xAA)),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            ToolTip = chip.Temperature == null ? "Нужны права администратора" : null,
        };
        body.Children.Add(temp);

        double load = chip.Load ?? 0;
        var loadRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var loadValue = Text(chip.Load is double l ? $"{l:0}%" : "—", 13, bold: true);
        DockPanel.SetDock(loadValue, Dock.Right);
        loadRow.Children.Add(loadValue);
        loadRow.Children.Add(Text("Нагрузка", 13, dim: true));
        body.Children.Add(loadRow);
        body.Children.Add(Bar(load, LoadColor(load)));

        body.Children.Add(StatRow("Частота", chip.ClockMHz is double mhz ? (mhz >= 1000 ? $"{mhz / 1000:0.00} ГГц" : $"{mhz:0} МГц") : "—"));
        if (chip.Power is double w) body.Children.Add(StatRow("Мощность", $"{w:0} Вт"));
        if (chip.MemoryTotalGb is double total && total > 0)
            body.Children.Add(StatRow("Видеопамять", $"{chip.MemoryUsedGb ?? 0:0.0} / {total:0.0} ГБ"));

        Border card = Card(body);
        card.Margin = new Thickness(title == "Процессор" ? 0 : 5, 0, title == "Процессор" ? 5 : 0, 10);
        return card;
    }

    private DockPanel StatRow(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var v = Text(value, 13, bold: true);
        DockPanel.SetDock(v, Dock.Right);
        row.Children.Add(v);
        row.Children.Add(Text(label, 13, dim: true));
        return row;
    }

    private static Grid Bar(double percent, Color color)
    {
        var bar = new Grid { Height = 5, Margin = new Thickness(0, 5, 0, 0) };
        bar.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(2.5) });
        var fill = new Border { Background = new SolidColorBrush(color), CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left };
        bar.SizeChanged += (_, _) => fill.Width = bar.ActualWidth * Math.Clamp(percent / 100, 0, 1);
        bar.Children.Add(fill);
        return bar;
    }

    private static Color TempColor(double c) =>
        c >= 85 ? Color.FromRgb(0xFF, 0x5A, 0x52) : c >= 70 ? Color.FromRgb(0xFF, 0xB0, 0x40) : c >= 55 ? Color.FromRgb(0xF5, 0xD7, 0x5A) : Color.FromRgb(0x6E, 0xE0, 0x9A);

    private static Color LoadColor(double p) =>
        p >= 90 ? Color.FromRgb(0xFF, 0x5A, 0x52) : p >= 70 ? Color.FromRgb(0xFF, 0xB0, 0x40) : Color.FromRgb(0x7C, 0x9C, 0xFF);

    // ---------- VPN ----------

    public void UpdateVpn()
    {
        VpnService vpn = App.Vpn;
        VpnTile.Background = vpn.IsConnected ? TileOn : TileOff;
        VpnState.Text = vpn.IsBusy ? "Подключаю…"
            : vpn.IsConnected ? (vpn.Exit != null ? $"{vpn.Exit.Country}" : "Подключён")
            : App.Settings.VpnServers.Count == 0 ? "Добавь ссылку в ⚙" : "Выключен";
        VpnTile.ToolTip = vpn.IsConnected ? $"{vpn.ServerName} · IP {vpn.Exit?.Ip}" : vpn.ActiveServer?.Name;
        if (_current == "settings") BuildVpnServers();
    }

    private async void VpnTile_Click(object sender, RoutedEventArgs e)
    {
        if (App.Settings.VpnServers.Count == 0)
        {
            SelectTab("settings");
            return;
        }
        await App.Vpn.ToggleAsync();
    }

    private void VpnAdd_Click(object sender, RoutedEventArgs e)
    {
        int added = 0;
        var errors = new List<string>();
        foreach (string raw in VpnLinkBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                VlessLink link = VlessLink.Parse(raw);
                App.Settings.VpnServers.Add(new VpnServer { Name = link.Name, Link = raw });
                added++;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        App.Settings.Save();
        VpnLinkBox.Text = "";
        VpnAddStatus.Text = added > 0
            ? $"Добавлено: {added}" + (errors.Count > 0 ? $", не распознано: {errors.Count}" : "")
            : errors.Count > 0 ? errors[0] : "Вставь ссылку vless://";
        BuildVpnServers();
        App.Pill.UpdateVpn();
        UpdateVpn();
    }

    private void BuildVpnServers()
    {
        VpnServersList.Children.Clear();
        List<VpnServer> list = App.Settings.VpnServers;
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            VpnServer server = list[i];
            bool active = i == Math.Clamp(App.Settings.VpnActive, 0, list.Count - 1);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var del = IconButton("\uE74D", "Удалить сервер");
            del.Click += (_, _) =>
            {
                if (active && App.Vpn.IsConnected) App.Vpn.Disconnect();
                list.RemoveAt(index);
                App.Settings.VpnActive = Math.Clamp(App.Settings.VpnActive, 0, Math.Max(0, list.Count - 1));
                App.Settings.Save();
                BuildVpnServers();
                App.Pill.UpdateVpn();
                UpdateVpn();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);

            var radio = new RadioButton
            {
                Content = server.Name + (active && App.Vpn.IsConnected ? "  — подключён" : ""),
                GroupName = "VpnServer",
                IsChecked = active,
                Style = (Style)FindResource("SettingsRadio"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            radio.GroupName = "VpnServer";
            radio.Checked += async (_, _) =>
            {
                if (App.Settings.VpnActive == index) return;
                App.Settings.VpnActive = index;
                App.Settings.Save();
                if (App.Vpn.IsConnected)
                {
                    // переключаемся на выбранный сервер
                    App.Vpn.Disconnect();
                    await App.Vpn.ConnectAsync();
                }
                App.Pill.UpdateVpn();
            };
            row.Children.Add(radio);
            VpnServersList.Children.Add(row);
        }
        if (list.Count == 0)
            VpnServersList.Children.Add(Text("Серверов пока нет.", 13, dim: true));
        if (!VpnService.XrayAvailable)
            VpnServersList.Children.Add(Text("⚠ Рядом с программой нет папки xray — скачай свежую сборку целиком.", 12.5, dim: true, top: 6));
    }

    private async void VpnDnsSave_Click(object sender, RoutedEventArgs e) => await SaveDnsAsync(VpnDnsBox.Text);

    private async void VpnDnsPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string dns }) return;
        VpnDnsBox.Text = dns;
        await SaveDnsAsync(dns);
    }

    private async Task SaveDnsAsync(string text)
    {
        List<string> servers = VpnService.ParseDns(text);
        App.Settings.VpnDns = string.Join(", ", servers);
        App.Settings.Save();
        VpnDnsBox.Text = App.Settings.VpnDns;
        VpnDnsStatus.Text = servers.Count == 0 ? "DNS: как в системе" : $"DNS сохранён: {servers.Count} серв.";
        if (App.Vpn.IsConnected)
        {
            VpnDnsStatus.Text += " — переподключаю VPN…";
            await App.Vpn.ReconnectIfConnectedAsync();
            VpnDnsStatus.Text = $"DNS применён ({(servers.Count == 0 ? "системный" : string.Join(", ", servers))})";
        }
    }

    // ---------- zapret ----------

    public void UpdateZapret()
    {
        ZapretService z = App.Zapret;
        ZapretTile.Background = z.IsRunning ? TileOn : TileOff;
        ZapretTileState.Text = z.IsBusy ? z.Status
            : z.IsRunning ? ZapretService.ShortName(App.Settings.ZapretStrategy ?? "general")
            : z.Installed ? "Выключен" : "Не скачан";
        ZapretTile.ToolTip = z.Status;

        if (_current != "settings") return;
        ZapretInstallButton.Content = z.Installed ? $"Обновить ({App.Settings.ZapretVersion ?? "?"})" : "Скачать zapret";
        ZapretInstallButton.IsEnabled = !z.IsBusy;
        ZapretStatusText.Text = z.Status.Length > 0 ? z.Status
            : z.Installed ? (SystemMonitorService.IsAdmin ? "Готов к работе" : "Скачан. Для запуска нужны права администратора")
            : "Ещё не скачан — нажми «Скачать zapret» (или просто включи кнопкой в пилюле)";
        ZapretAutoBox.IsChecked = App.Settings.ZapretAutoStart;
        ZapretPillBox.IsChecked = App.Settings.ZapretShowInPill;
        VpnPillBox.IsChecked = App.Settings.VpnShowInPill;
        ZapretRemoveButton.Visibility = z.Installed ? Visibility.Visible : Visibility.Collapsed;

        ZapretStrategies.Children.Clear();
        foreach (string strategy in z.Strategies())
        {
            bool selected = strategy == (App.Settings.ZapretStrategy ?? "general");
            var b = new Button
            {
                Content = ZapretService.ShortName(strategy),
                ToolTip = strategy + ".bat",
                Style = (Style)FindResource("FlatButton"),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = new Thickness(9, 4, 9, 4),
                FontSize = 12,
                Background = selected ? new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x80)) : new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x2A)),
            };
            b.Click += (_, _) =>
            {
                App.Settings.ZapretStrategy = strategy;
                App.Settings.Save();
                if (z.IsRunning)
                {
                    z.Stop();
                    z.Start();
                }
                UpdateZapret();
            };
            ZapretStrategies.Children.Add(b);
        }
    }

    private async void ZapretInstall_Click(object sender, RoutedEventArgs e)
    {
        await App.Zapret.InstallOrUpdateAsync();
        UpdateZapret();
        App.Pill.UpdateZapret();
    }

    private async void ZapretFind_Click(object sender, RoutedEventArgs e)
    {
        await App.Zapret.FindWorkingStrategyAsync();
        UpdateZapret();
    }

    private void ZapretFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(ZapretService.Root);
        ShellHelper.Open(ZapretService.Root);
    }

    private void ZapretOptions_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.ZapretAutoStart = ZapretAutoBox.IsChecked == true;
        App.Settings.ZapretShowInPill = ZapretPillBox.IsChecked == true;
        App.Settings.Save();
        App.Pill.UpdateZapret();
    }

    private async void ZapretTile_Click(object sender, RoutedEventArgs e)
    {
        bool wanted = !App.Zapret.IsRunning;
        await App.Zapret.ToggleAsync();
        if (wanted && !App.Zapret.IsRunning) App.Pill.ShowZapretProblem();
    }

    // ---------- zapret: дополнительно (опции service.bat) ----------

    private void VpnPill_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.VpnShowInPill = VpnPillBox.IsChecked == true;
        App.Settings.Save();
        App.Pill.UpdateVpn();
    }

    private async void ZapretRemove_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Остановить zapret, удалить его службы и все скачанные файлы?\nПотом его можно скачать заново.",
                "Удалить zapret", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        string result = await Task.Run(App.Zapret.Uninstall);
        UpdateZapret();
        App.Pill.UpdateZapret();
        ZapretStatusText.Text = result;
    }

    private void ZapretAdvanced_Click(object sender, RoutedEventArgs e)
    {
        SettingsView.Visibility = Visibility.Collapsed;
        ZapretView.Visibility = Visibility.Visible;
        ZapretToolMessage.Text = "";
        BuildZapretAdvanced();
    }

    private void ZapretBack_Click(object sender, RoutedEventArgs e)
    {
        ZapretView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        UpdateZapret();
    }

    /// <summary>Выполнить действие zapret в фоне и показать итог.</summary>
    private async void ZapretAction(Func<string> action, bool rebuild = true)
    {
        ZapretToolMessage.Text = "Выполняю…";
        try
        {
            ZapretToolMessage.Text = await Task.Run(action);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ZapretToolMessage.Text = "Ошибка: " + ex.Message;
        }
        if (rebuild) BuildZapretAdvanced();
        App.Pill.UpdateZapret();
    }

    private async void ZapretActionAsync(Func<Task<string>> action)
    {
        ZapretToolMessage.Text = "Выполняю…";
        try
        {
            ZapretToolMessage.Text = await action();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            ZapretToolMessage.Text = "Ошибка: " + ex.Message;
        }
        BuildZapretAdvanced();
    }

    private Button SmallButton(string text, Action click, bool accent = false)
    {
        var b = new Button
        {
            Content = text,
            Style = (Style)FindResource("FlatButton"),
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(10, 5, 10, 5),
            FontSize = 12.5,
        };
        if (accent) b.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x45, 0x80));
        b.Click += (_, _) => click();
        return b;
    }

    private void BuildZapretAdvanced()
    {
        ZapretAdvanced.Children.Clear();
        ZapretService z = App.Zapret;

        if (!z.Installed)
        {
            ZapretAdvanced.Children.Add(Card(Text("zapret ещё не скачан — вернись назад и нажми «Скачать zapret».", 13.5, dim: true)));
            return;
        }
        if (!SystemMonitorService.IsAdmin)
        {
            var warn = new StackPanel();
            warn.Children.Add(Text("Большинство опций меняют системные настройки — нужны права администратора.", 13, dim: true));
            var wrap = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            wrap.Children.Add(SmallButton("Перезапустить от администратора", () => { if (SystemMonitorService.RestartAsAdmin()) App.Quit(); }, accent: true));
            warn.Children.Add(wrap);
            ZapretAdvanced.Children.Add(Card(warn));
        }

        // статус и служба
        var service = new StackPanel();
        service.Children.Add(CardHeader("\uE9D9", "Статус и служба Windows"));
        foreach (string line in ZapretTools.StatusLines()) service.Children.Add(Text(line, 13, dim: true, top: 2));
        service.Children.Add(Text("Служба запускает выбранную стратегию вместе с Windows — работает даже без острова.", 12.5, dim: true, top: 8));
        var svcButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        string strategy = App.Settings.ZapretStrategy ?? "general";
        svcButtons.Children.Add(SmallButton($"Установить службу ({ZapretService.ShortName(strategy)})", () => ZapretAction(() => ZapretTools.InstallService(strategy)), accent: true));
        svcButtons.Children.Add(SmallButton("Удалить службы", () => ZapretAction(ZapretTools.RemoveServices)));
        svcButtons.Children.Add(SmallButton("Обновить статус", BuildZapretAdvanced));
        service.Children.Add(svcButtons);
        ZapretAdvanced.Children.Add(Card(service));

        // игровой фильтр
        var game = new StackPanel();
        game.Children.Add(CardHeader("\uE7FC", "Игровой фильтр"));
        game.Children.Add(Text("Обход для игр и других сервисов на портах выше 1023.", 12.5, dim: true));
        var modes = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach ((string mode, string title) in new[] { ("disabled", "Выключен"), ("all", "TCP и UDP"), ("tcp", "Только TCP"), ("udp", "Только UDP") })
        {
            var rb = new RadioButton
            {
                Content = title, GroupName = "ZapretGameMode", IsChecked = App.Settings.ZapretGameMode == mode,
                Style = (Style)FindResource("SettingsRadio"), FontSize = 13,
            };
            rb.GroupName = "ZapretGameMode";
            rb.Checked += (_, _) =>
            {
                if (App.Settings.ZapretGameMode == mode) return;
                App.Settings.ZapretGameMode = mode;
                App.Settings.Save();
                RestartZapretIfRunning("Игровой фильтр: " + title);
            };
            modes.Children.Add(rb);
        }
        game.Children.Add(modes);
        var ports = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        TextBox tcpBox = PortBox(App.Settings.ZapretGameTcp);
        TextBox udpBox = PortBox(App.Settings.ZapretGameUdp);
        ports.Children.Add(Text("TCP:", 13, dim: true));
        ports.Children.Add(tcpBox);
        ports.Children.Add(Text("UDP:", 13, dim: true));
        ports.Children.Add(udpBox);
        ports.Children.Add(SmallButton("Сохранить порты", () =>
        {
            string? tcp = ZapretTools.ValidateRange(tcpBox.Text);
            string? udp = ZapretTools.ValidateRange(udpBox.Text);
            if (tcp == null || udp == null)
            {
                ZapretToolMessage.Text = "Неверные порты. Пример: 1024-65535 или 1024-1934,1936-65535";
                return;
            }
            App.Settings.ZapretGameTcp = tcp;
            App.Settings.ZapretGameUdp = udp;
            App.Settings.Save();
            RestartZapretIfRunning("Порты сохранены");
        }));
        game.Children.Add(ports);
        ZapretAdvanced.Children.Add(Card(game));

        // IPSet
        var ipset = new StackPanel();
        ipset.Children.Add(CardHeader("\uE968", "IPSet фильтр"));
        ipset.Children.Add(Text("loaded — обход для IP из списка, none — ни для каких IP, any — для любых IP. Полезно для проверки, если какой-то сайт без zapret работает, а с ним — нет.", 12.5, dim: true));
        string current = ZapretTools.IpsetMode();
        var ipModes = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach ((string mode, string title) in new[] { ("loaded", "loaded (по списку)"), ("none", "none"), ("any", "any") })
        {
            var rb = new RadioButton
            {
                Content = title, IsChecked = current == mode, Style = (Style)FindResource("SettingsRadio"), FontSize = 13,
            };
            rb.GroupName = "ZapretIpset";
            rb.Checked += (_, _) =>
            {
                if (ZapretTools.IpsetMode() == mode) return;
                try { ZapretToolMessage.Text = ZapretTools.SetIpsetMode(mode); }
                catch (Exception ex) { ZapretToolMessage.Text = "Ошибка: " + ex.Message; }
                RestartZapretIfRunning(null);
            };
            ipModes.Children.Add(rb);
        }
        ipset.Children.Add(ipModes);
        var ipButtons = new WrapPanel();
        ipButtons.Children.Add(SmallButton("Обновить список IPSet", () => ZapretActionAsync(ZapretTools.UpdateIpsetAsync)));
        ipset.Children.Add(ipButtons);
        ZapretAdvanced.Children.Add(Card(ipset));

        // фейки
        var fakes = new StackPanel();
        fakes.Children.Add(CardHeader("\uE8AB", "Подмена фейков"));
        fakes.Children.Add(Text("Какой фейковый пакет отправлять для Discord UDP и для игрового UDP. Если голос в Discord не подключается — попробуй другой.", 12.5, dim: true));
        List<string> names = ZapretTools.FakeNames();
        foreach ((bool discord, string title) in new[] { (true, "Discord UDP"), (false, "Игровой UDP") })
        {
            string? cur = ZapretTools.CurrentFake(discord);
            var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var label = Text($"{title}:", 13);
            label.Width = 110;
            row.Children.Add(label);
            var combo = new ComboBox { MinWidth = 260, FontSize = 12.5 };
            foreach (string n in names) combo.Items.Add(n);
            combo.SelectedItem = cur;
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is string chosen && chosen != cur)
                {
                    ZapretToolMessage.Text = ZapretTools.ReplaceFake(discord, chosen);
                    RestartZapretIfRunning(null);
                }
            };
            row.Children.Add(combo);
            fakes.Children.Add(row);
        }
        ZapretAdvanced.Children.Add(Card(fakes));

        // hosts и Discord
        var extra = new StackPanel();
        extra.Children.Add(CardHeader("\uE774", "hosts и Discord"));
        extra.Children.Add(Text("hosts чинит веб-версию Telegram и голосовые чаты Discord. Остров дописывает свой блок в конец файла и сохраняет копию hosts.wikar-backup.", 12.5, dim: true));
        var extraButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        extraButtons.Children.Add(SmallButton("Проверить и обновить hosts", () => ZapretActionAsync(ZapretTools.UpdateHostsAsync)));
        extraButtons.Children.Add(SmallButton("Убрать записи из hosts", () => ZapretAction(ZapretTools.RemoveHostsBlock)));
        extraButtons.Children.Add(SmallButton("Очистить кэш Discord", () => ZapretAction(ZapretTools.ClearDiscordCache)));
        extra.Children.Add(extraButtons);
        ZapretAdvanced.Children.Add(Card(extra));

        // обновления
        var upd = new StackPanel();
        upd.Children.Add(CardHeader("\uE895", "Обновления"));
        upd.Children.Add(Text($"Установлена версия: {App.Settings.ZapretVersion ?? "?"}", 13, dim: true));
        var updButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        updButtons.Children.Add(SmallButton("Проверить обновления", () => ZapretActionAsync(async () =>
        {
            string? latest = await ZapretTools.LatestVersionAsync();
            string installed = (App.Settings.ZapretVersion ?? "").TrimStart('v', 'V');
            if (latest == null) return "Не удалось узнать последнюю версию";
            return latest == installed ? $"Установлена последняя версия ({latest})" : $"Доступна версия {latest} — нажми «Обновить» на прошлом экране";
        })));
        upd.Children.Add(updButtons);
        ZapretAdvanced.Children.Add(Card(upd));

        // диагностика
        var diag = new StackPanel();
        diag.Children.Add(CardHeader("\uE9F9", "Диагностика"));
        diag.Children.Add(Text("Те же проверки, что «Run Diagnostics»: службы, конфликтующие программы, прокси, DNS, драйвер.", 12.5, dim: true));
        var diagResults = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var runDiag = SmallButton("Запустить диагностику", () => { }, accent: true);
        runDiag.Click += async (_, _) =>
        {
            diagResults.Children.Clear();
            diagResults.Children.Add(Text("Проверяю…", 13, dim: true));
            List<DiagItem> items = await Task.Run(ZapretTools.Diagnose);
            diagResults.Children.Clear();
            foreach (DiagItem item in items.OrderBy(i => i.Level == DiagLevel.Ok ? 2 : i.Level == DiagLevel.Warning ? 1 : 0))
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var mark = new TextBlock
                {
                    Text = item.Level == DiagLevel.Ok ? "\uE73E" : item.Level == DiagLevel.Warning ? "\uE7BA" : "\uE711",
                    FontFamily = (FontFamily)FindResource("Icons"), FontSize = 12, Width = 22, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = new SolidColorBrush(item.Level == DiagLevel.Ok ? Color.FromRgb(0x6E, 0xE0, 0x9A)
                        : item.Level == DiagLevel.Warning ? Color.FromRgb(0xF5, 0xC8, 0x5A) : Color.FromRgb(0xFF, 0x5A, 0x52)),
                };
                row.Children.Add(mark);
                if (item.Fix != null && item.FixTitle != null)
                {
                    Func<string> fix = item.Fix;
                    var fixButton = SmallButton(item.FixTitle, () => ZapretAction(fix));
                    fixButton.Margin = new Thickness(8, 0, 0, 0);
                    DockPanel.SetDock(fixButton, Dock.Right);
                    row.Children.Add(fixButton);
                }
                row.Children.Add(Text(item.Text, 13, dim: item.Level == DiagLevel.Ok));
                diagResults.Children.Add(row);
            }
        };
        var diagButtons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        diagButtons.Children.Add(runDiag);
        diag.Children.Add(diagButtons);
        diag.Children.Add(diagResults);
        ZapretAdvanced.Children.Add(Card(diag));
    }

    private static TextBox PortBox(string value) => new()
    {
        Text = value,
        Width = 150,
        Margin = new Thickness(6, 0, 12, 6),
        Padding = new Thickness(6, 3, 6, 3),
        FontSize = 12.5,
        Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x24)),
        Foreground = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF5)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
        CaretBrush = Brushes.White,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
    };

    private void RestartZapretIfRunning(string? message)
    {
        if (App.Zapret.IsRunning)
        {
            App.Zapret.Stop();
            App.Zapret.Start();
            if (message != null) ZapretToolMessage.Text = message + " · zapret перезапущен";
        }
        else if (message != null)
        {
            ZapretToolMessage.Text = message;
        }
    }
}

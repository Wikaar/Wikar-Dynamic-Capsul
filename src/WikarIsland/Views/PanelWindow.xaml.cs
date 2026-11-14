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
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>
/// Раскрытый остров: вкладки «Главная», ИИ-чаты (веб-версии в WebView2) и настройки.
/// Это обычное непрозрачное окно — WebView2 не умеет рисоваться в прозрачных окнах WPF.
/// </summary>
public partial class PanelWindow : Window
{
    private static readonly Dictionary<string, string> AiUrls = new()
    {
        ["chatgpt"] = "https://chatgpt.com/",
        ["claude"] = "https://claude.ai/new",
        ["gemini"] = "https://gemini.google.com/app",
    };

    private static Task<CoreWebView2Environment>? _environment;

    private readonly Dictionary<string, WebView2> _views = new();
    private readonly DispatcherTimer _closeTimer;
    private readonly DispatcherTimer _clockTimer;
    private string _current = "home";
    private bool _loadingSettings;
    private DateTime? _outsideSince;

    public PanelWindow()
    {
        InitializeComponent();

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _closeTimer.Tick += (_, _) => CloseTick();

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateHome();

        Deactivated += OnDeactivated;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) HideIsland();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _closeTimer.Start(); _clockTimer.Start(); UpdateHome(); }
            else { _closeTimer.Stop(); _clockTimer.Stop(); }
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
            if (!_views.ContainsKey(_current) && _current == "home") SelectTab(App.Settings.LastTab);
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

        double centerX = pill.Left + pill.Width / 2;
        Left = Math.Clamp(centerX - Width / 2, wa.Left + 6, Math.Max(wa.Left + 6, wa.Right - Width - 6));

        bool pillInUpperHalf = pill.Top + pill.Height / 2 < wa.Top + wa.Height / 2;
        double top = pillInUpperHalf
            ? pill.Top + pill.Height + 2   // раскрываемся вниз
            : pill.Top - Height - 2;       // пилюля внизу — раскрываемся вверх
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
        var pillRect = new Rect(App.Pill.Left, App.Pill.Top, App.Pill.Width, App.Pill.Height);

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
        if (key != "home" && key != "settings" && !AiUrls.ContainsKey(key)) key = "home";
        _current = key;

        RadioButton tab = key switch
        {
            "chatgpt" => TabChatGpt,
            "claude" => TabClaude,
            "gemini" => TabGemini,
            "settings" => TabSettings,
            _ => TabHome,
        };
        if (tab.IsChecked != true) tab.IsChecked = true; // вызовет Tab_Checked ещё раз, это безопасно

        bool isAi = AiUrls.ContainsKey(key);
        HomeView.Visibility = key == "home" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = key == "settings" ? Visibility.Visible : Visibility.Collapsed;
        AiHost.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
        PopOutButton.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;
        ReloadButton.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;

        foreach (KeyValuePair<string, WebView2> pair in _views)
            pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;

        if (isAi && !_views.ContainsKey(key)) _ = CreateAiViewAsync(key);
        if (key == "settings") LoadSettings();

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

    // ---------- главная ----------

    private void UpdateHome()
    {
        if (_current != "home") return;
        var ru = new CultureInfo("ru-RU");
        DateTime now = DateTime.Now;
        HomeTime.Text = now.ToString("HH:mm");
        HomeDate.Text = now.ToString("dddd, d MMMM", ru);
        HomeHint.Text =
            $"{App.Settings.Hotkey} — открыть остров с клавиатуры\n" +
            "Наведи на пилюлю — остров раскроется, уведи курсор — свернётся\n" +
            "Правый клик по пилюле — переместить, настройки, выход";
    }

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
        HotkeyBox.Text = s.Hotkey;
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

        s.Save();
        App.Pill.ApplySettings();
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

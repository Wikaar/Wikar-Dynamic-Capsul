using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>Настройки: профиль (имя, пол, обращение), тема и неоновая окантовка.</summary>
public partial class PanelWindow
{
    public static readonly (string hex, string name)[] NeonPresets =
    {
        ("", "Выкл"),
        ("#22D3EE", "Голубой"),
        ("#3B82F6", "Синий"),
        ("#A855F7", "Фиолетовый"),
        ("#EC4899", "Розовый"),
        ("#EF4444", "Красный"),
        ("#F97316", "Оранжевый"),
        ("#FACC15", "Жёлтый"),
        ("#22C55E", "Зелёный"),
        ("#FFFFFF", "Белый"),
    };

    /// <summary>Цвет неона из настроек или null, если окантовка выключена или цвет испорчен.</summary>
    public static Color? NeonColor()
    {
        string hex = App.Settings.NeonColor;
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch
        {
            return null;
        }
    }

    private void LoadProfileAndLook()
    {
        AppSettings s = App.Settings;
        UserNameBox.Text = s.UserName;
        GenderMale.IsChecked = s.UserGender == "m";
        GenderFemale.IsChecked = s.UserGender == "f";
        GenderNone.IsChecked = s.UserGender is not ("m" or "f");
        AddrSir.IsChecked = s.AddressStyle is not ("title" or "gos" or "name" or "custom");
        AddrTitle.IsChecked = s.AddressStyle == "title";
        AddrGos.IsChecked = s.AddressStyle == "gos";
        AddrName.IsChecked = s.AddressStyle == "name";
        AddrCustom.IsChecked = s.AddressStyle == "custom";
        AddressCustomBox.Text = s.AddressCustom;
        AddressCustomBox.Visibility = s.AddressStyle == "custom" ? Visibility.Visible : Visibility.Collapsed;
        UpdateAddressPreview();

        ThemeDark.IsChecked = s.UiTheme != "light";
        ThemeLight.IsChecked = s.UiTheme == "light";
        ThemeRestartRow.Visibility = Visibility.Collapsed;
        NeonPillBox.IsChecked = s.NeonPill;
        NeonPanelBox.IsChecked = s.NeonPanel;
        NeonCustomBox.Text = s.NeonColor;
        BuildNeonColors();
        ApplyPanelOutline();
    }

    // ---------- профиль ----------

    private void SaveProfile()
    {
        if (_loadingSettings) return;
        AppSettings s = App.Settings;
        s.UserName = UserNameBox.Text.Trim();
        s.UserGender = GenderMale.IsChecked == true ? "m" : GenderFemale.IsChecked == true ? "f" : "";
        s.AddressStyle = AddrTitle.IsChecked == true ? "title" : AddrGos.IsChecked == true ? "gos"
            : AddrName.IsChecked == true ? "name" : AddrCustom.IsChecked == true ? "custom" : "sir";
        s.AddressCustom = AddressCustomBox.Text.Trim();
        AddressCustomBox.Visibility = s.AddressStyle == "custom" ? Visibility.Visible : Visibility.Collapsed;
        s.Save();
        UpdateAddressPreview();
    }

    private void Profile_Changed(object sender, RoutedEventArgs e) => SaveProfile();

    private void Profile_TextChanged(object sender, TextChangedEventArgs e) => SaveProfile();

    private void UpdateAddressPreview()
    {
        if (AddressPreview == null) return;
        AddressPreview.Text = "«" + UserAddress.Apply("Всё выполнено, сэр.") + "»";
    }

    private async void ProfileListen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await App.Speech.SpeakAsync("Всё выполнено, сэр.");
        }
        catch (Exception ex)
        {
            AddressPreview.Text = "Не удалось озвучить: " + ex.Message;
        }
    }

    // ---------- тема ----------

    private void UiTheme_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        string theme = ThemeLight.IsChecked == true ? "light" : "dark";
        if (App.Settings.UiTheme == theme) return;
        App.Settings.UiTheme = theme;
        App.Settings.Save();
        // тема выбирается при запуске: показываем кнопку, если выбранная отличается от той, что сейчас на экране
        ThemeRestartRow.Visibility = (theme == "light") != Theme.IsLight ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ThemeRestart_Click(object sender, RoutedEventArgs e) => App.Restart();

    // ---------- неон ----------

    private void BuildNeonColors()
    {
        NeonColors.Children.Clear();
        string current = App.Settings.NeonColor.Trim();
        foreach ((string hex, string name) in NeonPresets)
        {
            bool selected = string.Equals(current, hex, StringComparison.OrdinalIgnoreCase);
            var dot = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, hex.Length > 0 ? 0 : 6, 0),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (hex.Length > 0)
            {
                var c = (Color)ColorConverter.ConvertFromString(hex);
                dot.Background = new SolidColorBrush(c);
                dot.BorderBrush = new SolidColorBrush(c);
                dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = c, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.9 };
            }
            else
            {
                dot.BorderBrush = new SolidColorBrush(Theme.Rgb(0x8A, 0x8A, 0x94));
            }
            object content = dot;
            if (hex.Length == 0)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(dot);
                sp.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
                content = sp;
            }
            var b = new Button
            {
                Content = content,
                ToolTip = name,
                Style = (Style)FindResource("FlatButton"),
                Margin = new Thickness(0, 0, 6, 6),
                Padding = hex.Length > 0 ? new Thickness(7, 6, 7, 6) : new Thickness(9, 5, 11, 5),
                FontSize = 12.5,
                Background = selected ? new SolidColorBrush(Theme.Rgb(0x3A, 0x45, 0x80)) : new SolidColorBrush(Theme.Rgb(0x24, 0x24, 0x2A)),
            };
            b.Click += (_, _) => SetNeon(hex);
            NeonColors.Children.Add(b);
        }
    }

    private void SetNeon(string hex)
    {
        App.Settings.NeonColor = hex;
        App.Settings.Save();
        NeonCustomBox.Text = hex;
        BuildNeonColors();
        ApplyPanelOutline();
        App.Pill.ApplySettings();
    }

    private void NeonCustom_Click(object sender, RoutedEventArgs e)
    {
        string text = NeonCustomBox.Text.Trim();
        if (text.Length > 0 && !text.StartsWith('#')) text = "#" + text;
        try
        {
            if (text.Length > 0) ColorConverter.ConvertFromString(text);
        }
        catch
        {
            NeonCustomBox.Text = App.Settings.NeonColor;
            return;
        }
        SetNeon(text.ToUpperInvariant());
    }

    private void Neon_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        App.Settings.NeonPill = NeonPillBox.IsChecked == true;
        App.Settings.NeonPanel = NeonPanelBox.IsChecked == true;
        App.Settings.Save();
        ApplyPanelOutline();
        App.Pill.ApplySettings();
    }

    /// <summary>Окантовка раскрытой капсулы: неон, если выбран, иначе тонкая линия (в светлой теме — чтобы край был виден).</summary>
    public void ApplyMusicOutline() => ApplyPanelOutline();

    private void ApplyPanelOutline()
    {
        Color? neon = NeonColor();
        if (PillWindow.MusicOutlineColor() is Color music && (App.Settings.NeonPanel || neon == null))
        {
            PanelOutline.BorderBrush = new SolidColorBrush(music);
            PanelOutline.BorderThickness = new Thickness(2);
            PanelOutline.Visibility = Visibility.Visible;
        }
        else if (neon is Color c && App.Settings.NeonPanel)
        {
            PanelOutline.BorderBrush = new SolidColorBrush(c);
            PanelOutline.BorderThickness = new Thickness(2);
            PanelOutline.Visibility = Visibility.Visible;
        }
        else if (Theme.IsLight)
        {
            PanelOutline.BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0));
            PanelOutline.BorderThickness = new Thickness(1.5);
            PanelOutline.Visibility = Visibility.Visible;
        }
        else
        {
            PanelOutline.Visibility = Visibility.Collapsed;
        }
    }
}

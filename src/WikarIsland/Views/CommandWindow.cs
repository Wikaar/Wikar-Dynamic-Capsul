using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>Быстрая строка под капсулой: команды острова или вопрос к ИИ.</summary>
public sealed class CommandWindow : Window
{
    private readonly TextBox _input;
    private readonly StackPanel _hints;
    private readonly TextBlock _status;
    private bool _closing;

    private static readonly Brush Dim = new SolidColorBrush(Theme.FgRgb(0x8A, 0x8A, 0x94));
    private static readonly Brush Main = new SolidColorBrush(Theme.FgRgb(0xF2, 0xF2, 0xF5));
    private static readonly FontFamily Ui = new("Segoe UI Variable Text, Segoe UI");

    public CommandWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Width = 560;
        SizeToContent = SizeToContent.Height;
        Title = "Wikar Island — быстрая строка";

        _input = new TextBox
        {
            FontSize = 17,
            FontFamily = Ui,
            Foreground = Main,
            CaretBrush = Main,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _input.TextChanged += (_, _) => UpdateHints();
        _input.PreviewKeyDown += Input_PreviewKeyDown;

        var placeholder = new TextBlock
        {
            Text = "Команда или вопрос к ИИ…",
            FontSize = 17,
            FontFamily = Ui,
            Foreground = Dim,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        _input.TextChanged += (_, _) => placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var icon = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Theme.Rgb(0x9C, 0xB0, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var row = new DockPanel { Height = 34 };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        var field = new Grid();
        field.Children.Add(placeholder);
        field.Children.Add(_input);
        row.Children.Add(field);

        _status = new TextBlock
        {
            FontSize = 12.5,
            FontFamily = Ui,
            Foreground = new SolidColorBrush(Theme.Rgb(0xF5, 0xC8, 0x5A)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _hints = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

        var content = new StackPanel();
        content.Children.Add(row);
        content.Children.Add(_status);
        content.Children.Add(_hints);

        Content = new Border
        {
            Background = new SolidColorBrush(Theme.Argb(0xF5, 0x16, 0x16, 0x1A)),
            BorderBrush = new SolidColorBrush(Theme.Argb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 10, 18, 12),
            Margin = new Thickness(16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 },
            Child = content,
        };

        Deactivated += (_, _) => CloseBar();
        UpdateHints();
    }

    /// <summary>Показать под капсулой и сразу поставить курсор в строку.</summary>
    public void ShowUnder(Rect pillScreenRect)
    {
        Left = pillScreenRect.Left + pillScreenRect.Width / 2 - Width / 2;
        Top = pillScreenRect.Bottom - 4;
        Opacity = 0;
        Show();
        Activate();
        NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        _input.Focus();
        Keyboard.Focus(_input);
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseBar();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string text = _input.Text;
            if (text.Trim().Length == 0) return;
            string? error;
            try
            {
                error = CommandService.Execute(text);
            }
            catch (Exception ex)
            {
                App.LogError(ex);
                error = "Ошибка: " + ex.Message;
            }
            if (error == null)
            {
                CloseBar();
                return;
            }
            _status.Text = error;
            _status.Visibility = Visibility.Visible;
        }
        else if (e.Key == Key.Tab)
        {
            // подставить первую подсказку
            if (_hints.Children.OfType<FrameworkElement>().FirstOrDefault()?.Tag is string example)
            {
                _input.Text = example.Split(" / ")[0].Split(" · ")[0] + " ";
                _input.CaretIndex = _input.Text.Length;
            }
            e.Handled = true;
        }
    }

    private void UpdateHints()
    {
        _status.Visibility = Visibility.Collapsed;
        _hints.Children.Clear();
        string q = _input.Text.Trim().ToLowerInvariant();
        var items = CommandService.Examples
            .Where(x => q.Length == 0 || x.example.StartsWith(q) || x.example.Split(' ', '/', '·').Any(w => w.Length > 0 && q.StartsWith(w.Trim()) && w.Trim().Length > 2))
            .Take(q.Length == 0 ? 8 : 4)
            .ToList();
        if (q.Length > 0 && items.Count == 0)
            items.Add(("Enter", $"спросить {AiName(App.Settings.CommandAi)}"));

        foreach ((string example, string description) in items)
        {
            var line = new DockPanel { Margin = new Thickness(28, 2, 0, 2), Tag = example };
            var desc = new TextBlock { Text = description, FontSize = 12, FontFamily = Ui, Foreground = Dim, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(desc, Dock.Right);
            line.Children.Add(desc);
            line.Children.Add(new TextBlock { Text = example, FontSize = 13, FontFamily = Ui, Foreground = Main, VerticalAlignment = VerticalAlignment.Center });
            _hints.Children.Add(line);
        }
        if (q.Length == 0)
            _hints.Children.Add(new TextBlock
            {
                Text = "Enter — выполнить · Tab — подставить · Esc — закрыть",
                FontSize = 11.5, FontFamily = Ui, Foreground = Dim, Margin = new Thickness(28, 6, 0, 0),
            });
    }

    public static string AiName(string key) => key switch
    {
        "claude" => "Claude",
        "gemini" => "Gemini",
        "deepseek" => "DeepSeek",
        _ => "ChatGPT",
    };

    private void CloseBar()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}

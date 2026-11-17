using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using WikarIsland.Services;

namespace WikarIsland.Views;

/// <summary>
/// Отдельное окно с чатом («↗ В окне») или всплывающее окно сайта (например, вход через Google).
/// </summary>
public partial class AiWindow : Window
{
    private readonly string? _url;
    private Task? _init;

    public AiWindow(string? url)
    {
        InitializeComponent();
        Web.DefaultBackgroundColor = System.Drawing.Theme.Argb(255, 20, 20, 23);
        _url = url;
        if (url != null)
            Loaded += async (_, _) => await InitAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.SetDwmInt(new WindowInteropHelper(this).Handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, Theme.IsLight ? 0 : 1);
    }

    public Task InitAsync() => _init ??= InitCoreAsync();

    private async Task InitCoreAsync()
    {
        try
        {
            CoreWebView2Environment env = await PanelWindow.GetEnvironmentAsync();
            await Web.EnsureCoreWebView2Async(env);

            CoreWebView2 core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.WindowCloseRequested += (_, _) => Close();
            core.DocumentTitleChanged += (_, _) =>
                Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Wikar Island" : core.DocumentTitle;
            PanelWindow.HookNewWindows(core);

            if (_url != null) core.Navigate(_url);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            MessageBox.Show(this, "Не удалось запустить встроенный браузер (WebView2).\n\n" + ex.Message,
                "Wikar Island", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

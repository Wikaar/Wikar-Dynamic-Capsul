using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace WikarIsland.Services;

/// <summary>
/// Блокировщик рекламы во встроенном браузере острова: uBlock Origin Lite, если он лежит в папке adblock
/// рядом с программой (кладёт сборка). Работает только там, где WebView2 поддерживает расширения;
/// не получилось — остров работает как раньше, а YouTube пропускает рекламу кнопкой «Пропустить».
/// </summary>
public static class AdBlock
{
    private static Task? _setup;

    /// <summary>Папка с распакованным расширением (где лежит manifest.json) или null.</summary>
    public static string? ExtensionFolder
    {
        get
        {
            try
            {
                string root = Path.Combine(AppContext.BaseDirectory, "adblock");
                if (!Directory.Exists(root)) return null;
                string? manifest = Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();
                return manifest == null ? null : Path.GetDirectoryName(manifest);
            }
            catch
            {
                return null;
            }
        }
    }

    public static bool Available => ExtensionFolder != null;

    /// <summary>Что сейчас с блокировщиком — для подсказки в настройках.</summary>
    public static string Status { get; private set; } = "";

    /// <summary>Один раз за запуск: установить или включить/выключить расширение в общем профиле.</summary>
    public static Task EnsureAsync(CoreWebView2 core) => _setup ??= SetupAsync(core);

    /// <summary>Поменяли галочку в настройках — применить сразу.</summary>
    public static async Task ApplySettingAsync(CoreWebView2? core)
    {
        _setup = null;
        if (core != null) await EnsureAsync(core);
    }

    private static async Task SetupAsync(CoreWebView2 core)
    {
        try
        {
            string? folder = ExtensionFolder;
            CoreWebView2Profile profile = core.Profile;
            IReadOnlyList<CoreWebView2BrowserExtension> installed = await profile.GetBrowserExtensionsAsync();
            CoreWebView2BrowserExtension? ours = installed.FirstOrDefault(x =>
                x.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("uBO", StringComparison.OrdinalIgnoreCase));
            bool want = App.Settings.VideoAdBlock;

            if (ours == null)
            {
                if (!want)
                {
                    Status = "Выключен.";
                    return;
                }
                if (folder == null)
                {
                    Status = "Нет папки adblock рядом с программой — блокировщик не установлен. Реклама пропускается кнопкой «Пропустить».";
                    return;
                }
                ours = await profile.AddBrowserExtensionAsync(folder);
                Status = $"Работает: {ours.Name}.";
                App.LogVoice($"Блокировщик рекламы установлен: {ours.Name}");
                return;
            }
            if (ours.IsEnabled != want) await ours.EnableAsync(want);
            Status = want ? $"Работает: {ours.Name}." : "Выключен.";
        }
        catch (Exception ex)
        {
            // старый WebView2 или расширение не поддерживается — живём без него
            Status = "Встроенный браузер не принял блокировщик: " + ex.Message + " Реклама пропускается кнопкой «Пропустить».";
            App.LogError(new InvalidOperationException("Блокировщик рекламы не установился", ex));
        }
    }
}

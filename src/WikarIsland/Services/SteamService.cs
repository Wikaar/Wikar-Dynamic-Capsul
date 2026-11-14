using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WikarIsland.Services;

public sealed class SteamDownload
{
    public int AppId { get; init; }
    public string Name { get; init; } = "";
    public long Done { get; init; }
    public long Total { get; init; }
    public double Progress => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>
/// Steam без API и без входа в аккаунт: реестр (какая игра запущена, что обновляется)
/// и файлы appmanifest_*.acf в библиотеках (название и прогресс загрузки).
/// </summary>
public sealed partial class SteamService
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly Dictionary<int, string> _names = new();
    private HashSet<int> _updating = new();
    private string? _steamPath;
    private List<string> _libraries = new();

    public bool Installed => _steamPath != null;
    public ImageSource? SteamIcon { get; private set; }
    public List<SteamDownload> Downloads { get; private set; } = new();
    public int RunningAppId { get; private set; }
    public string RunningName { get; private set; } = "";
    public DateTime RunningSince { get; private set; }

    public event Action? Changed;
    public event Action<int, string>? Finished;

    [GeneratedRegex("\"(\\w+)\"\\s+\"([^\"]*)\"")]
    private static partial Regex KeyValue();

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPath();

    public void Start()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? path = key?.GetValue("SteamPath") as string;
            if (path == null || !Directory.Exists(path)) return;
            _steamPath = path.Replace('/', '\\');
            LoadLibraries();
            LoadIcon();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return;
        }

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    public static void Launch(int appId)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    public static void OpenDownloads()
    {
        try
        {
            Process.Start(new ProcessStartInfo("steam://open/downloads") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private void LoadLibraries()
    {
        _libraries = new List<string> { _steamPath! };
        string vdf = Path.Combine(_steamPath!, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return;
        foreach (Match m in LibraryPath().Matches(File.ReadAllText(vdf)))
        {
            string lib = m.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(lib) && !_libraries.Contains(lib, StringComparer.OrdinalIgnoreCase))
                _libraries.Add(lib);
        }
    }

    private void LoadIcon()
    {
        try
        {
            string exe = Path.Combine(_steamPath!, "steam.exe");
            if (!File.Exists(exe)) return;
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null) return;
            BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            SteamIcon = src;
        }
        catch
        {
            // без иконки
        }
    }

    private Dictionary<string, string>? ReadManifest(int appId)
    {
        foreach (string lib in _libraries)
        {
            string file = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(file)) continue;
            try
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in KeyValue().Matches(File.ReadAllText(file)))
                    values.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
                return values;
            }
            catch (IOException)
            {
                return null; // Steam как раз пишет файл
            }
        }
        return null;
    }

    private string NameOf(int appId)
    {
        if (_names.TryGetValue(appId, out string? name)) return name;
        name = ReadManifest(appId)?.GetValueOrDefault("name") ?? $"Игра {appId}";
        _names[appId] = name;
        return name;
    }

    private void Tick()
    {
        try
        {
            using RegistryKey? steam = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (steam == null) return;

            // запущенная игра
            int running = steam.GetValue("RunningAppID") is int r ? r : 0;
            if (running != RunningAppId)
            {
                RunningAppId = running;
                RunningName = running != 0 ? NameOf(running) : "";
                RunningSince = DateTime.Now;
            }

            // загрузки и обновления
            var updating = new HashSet<int>();
            var downloads = new List<SteamDownload>();
            using (RegistryKey? apps = steam.OpenSubKey("Apps"))
            {
                if (apps != null)
                {
                    foreach (string sub in apps.GetSubKeyNames())
                    {
                        if (!int.TryParse(sub, out int id)) continue;
                        using RegistryKey? app = apps.OpenSubKey(sub);
                        if (app?.GetValue("Updating") is not int u || u == 0) continue;

                        updating.Add(id);
                        Dictionary<string, string>? manifest = ReadManifest(id);
                        long.TryParse(manifest?.GetValueOrDefault("BytesDownloaded"), out long done);
                        long.TryParse(manifest?.GetValueOrDefault("BytesToDownload"), out long total);
                        downloads.Add(new SteamDownload { AppId = id, Name = NameOf(id), Done = done, Total = total });
                    }
                }
            }

            foreach (int id in _updating)
            {
                if (updating.Contains(id)) continue;
                string? flags = ReadManifest(id)?.GetValueOrDefault("StateFlags");
                if (flags == "4") Finished?.Invoke(id, NameOf(id)); // 4 = полностью установлена
            }

            _updating = updating;
            Downloads = downloads;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }
}

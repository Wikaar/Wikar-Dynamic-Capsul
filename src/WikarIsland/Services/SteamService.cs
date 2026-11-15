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

    /// <summary>Установленные игры из всех библиотек (для запуска голосом).</summary>
    public IEnumerable<(int id, string name)> InstalledGames()
    {
        if (_steamPath == null) yield break;
        foreach (string lib in _libraries)
        {
            string dir = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(dir)) continue;
            foreach (string file in Directory.EnumerateFiles(dir, "appmanifest_*.acf"))
            {
                string? name = null;
                int id = 0;
                try
                {
                    foreach (Match m in KeyValue().Matches(File.ReadAllText(file)))
                    {
                        if (m.Groups[1].Value.Equals("appid", StringComparison.OrdinalIgnoreCase)) int.TryParse(m.Groups[2].Value, out id);
                        else if (m.Groups[1].Value.Equals("name", StringComparison.OrdinalIgnoreCase)) name ??= m.Groups[2].Value;
                    }
                }
                catch
                {
                    continue;
                }
                // служебное: Steamworks, редистрибутивы, Proton
                if (id == 0 || name == null || id == 228980 || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)) continue;
                yield return (id, name);
            }
        }
    }

    private static bool IsSteamRunning()
    {
        Process[] list = Process.GetProcessesByName("steam");
        foreach (Process p in list) p.Dispose();
        return list.Length > 0;
    }

    // StateFlags: 2 — нужно обновление, 256 — идёт обновление, 512 — на паузе, 1024 — обновление начато
    private static bool IsDownloading(Dictionary<string, string> manifest)
    {
        if (!int.TryParse(manifest.GetValueOrDefault("StateFlags"), out int flags)) return false;
        if ((flags & 512) != 0) return false;
        return (flags & (256 | 1024)) != 0;
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

            // Steam закрыт — отметки «Updating» и «RunningAppID» в реестре могли остаться с прошлого раза
            bool steamRunning = IsSteamRunning();

            // запущенная игра
            int running = steamRunning && steam.GetValue("RunningAppID") is int r ? r : 0;
            if (running != RunningAppId)
            {
                RunningAppId = running;
                RunningName = running != 0 ? NameOf(running) : "";
                RunningSince = DateTime.Now;
            }

            // загрузки и обновления
            var updating = new HashSet<int>();
            var downloads = new List<SteamDownload>();
            using (RegistryKey? apps = steamRunning ? steam.OpenSubKey("Apps") : null)
            {
                if (apps != null)
                {
                    foreach (string sub in apps.GetSubKeyNames())
                    {
                        if (!int.TryParse(sub, out int id)) continue;
                        using RegistryKey? app = apps.OpenSubKey(sub);
                        if (app?.GetValue("Updating") is not int u || u == 0) continue;

                        // настоящая загрузка: есть appmanifest и в StateFlags стоит «нужно обновить» или «качается»,
                        // а не «на паузе»; иначе это хвост от прошлых сессий
                        Dictionary<string, string>? manifest = ReadManifest(id);
                        if (manifest == null || !IsDownloading(manifest)) continue;
                        updating.Add(id);
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

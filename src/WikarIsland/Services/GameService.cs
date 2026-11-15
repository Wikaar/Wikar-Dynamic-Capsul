using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace WikarIsland.Services;

/// <summary>
/// Игровой режим капсулы: пока на переднем плане игра на весь экран, показываем FPS и нагрузку.
/// FPS считает PresentMon от Intel (папка presentmon рядом с программой) — он читает кадры
/// через ETW для DirectX, Vulkan и OpenGL. ETW нужны права администратора
/// (или группа «Пользователи журналов производительности»). Нагрузку и температуру даёт SystemMonitor.
/// </summary>
public sealed class GameService : IDisposable
{
    /// <summary>То, что разворачивается на весь экран, но игрой не является.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "browser", "iexplore", "arc",
        "vlc", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayer64", "potplayermini", "potplayermini64",
        "wmplayer", "video.ui", "microsoft.media.player", "mpv", "kmplayer", "stremio", "netflix", "plex",
        "applicationframehost", "explorer", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "lockapp", "textinputhost", "powerpnt", "obs64", "obs32", "telegram", "discord", "zoom", "teams", "ms-teams",
        "wikarisland", "steamwebhelper", "steam", "epicgameslauncher", "battle.net", "eadesktop", "upc",
        "galaxyclient", "riotclientux", "devenv", "code", "photoshop", "premiere", "afterfx", "resolve",
    };

    private readonly object _lock = new();
    private readonly List<double> _frames = new(); // мс между кадрами, последние ~2 секунды
    private Process? _presentMon;
    private int _msColumn = -1;
    private DateTime _lastFrame;
    private string? _lastError;
    private DateTime? _lostSince;

    public bool Active { get; private set; }
    public int Pid { get; private set; }
    public string Name { get; private set; } = "";
    public double? Fps { get; private set; }

    /// <summary>Почему FPS не показывается (нет PresentMon, нет прав). Null — всё в порядке.</summary>
    public string? FpsProblem { get; private set; }

    public event Action? Changed;

    public static string PresentMonPath => Path.Combine(AppContext.BaseDirectory, "presentmon", "PresentMon.exe");
    public static bool PresentMonAvailable => File.Exists(PresentMonPath);

    /// <summary>Раз в секунду из таймера пилюли.</summary>
    public void Tick()
    {
        try
        {
            if (!App.Settings.GameHud)
            {
                if (Active) End();
                return;
            }

            int pid = DetectGame(out string name);
            if (pid != 0)
            {
                _lostSince = null;
                if (!Active || pid != Pid) Begin(pid, name);
            }
            else if (Active)
            {
                // свернули игру (Alt+Tab) — ждём несколько секунд; закрыли — выходим сразу
                _lostSince ??= DateTime.Now;
                if (!IsAlive(Pid) || (DateTime.Now - _lostSince.Value).TotalSeconds > 4) End();
            }

            if (!Active) return;
            ComputeFps();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static int DetectGame(out string name)
    {
        name = "";
        if (!FocusService.FullscreenAppActive()) return 0;

        IntPtr fg = NativeMethods.GetForegroundWindow();
        if (fg == IntPtr.Zero) return 0;
        NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == 0 || pid == Environment.ProcessId) return 0;

        try
        {
            using Process p = Process.GetProcessById((int)pid);
            if (NotGames.Contains(p.ProcessName)) return 0;
            name = p.ProcessName;
            if (App.Steam?.RunningAppId > 0 && !string.IsNullOrEmpty(App.Steam.RunningName)) name = App.Steam.RunningName;
            else
            {
                try
                {
                    string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
                }
                catch
                {
                    // античит или чужой пользователь — остаёмся с именем процесса
                }
            }
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void Begin(int pid, string name)
    {
        if (Active) StopPresentMon();
        Active = true;
        Pid = pid;
        Name = name;
        Fps = null;
        App.SystemMonitor.Start("game");
        StartPresentMon(pid);
        Changed?.Invoke();
    }

    public void End()
    {
        if (!Active) return;
        Active = false;
        Pid = 0;
        Fps = null;
        StopPresentMon();
        App.SystemMonitor.Stop("game");
        Changed?.Invoke();
    }

    // ---------- PresentMon ----------

    private void StartPresentMon(int pid)
    {
        lock (_lock)
        {
            _frames.Clear();
            _msColumn = -1;
            _lastError = null;
        }
        if (!PresentMonAvailable)
        {
            FpsProblem = "Нет PresentMon рядом с программой — поставь свежую сборку целиком";
            return;
        }

        FpsProblem = null;
        try
        {
            var psi = new ProcessStartInfo(PresentMonPath,
                $"--process_id {pid} --output_stdout --no_console_stats --stop_existing_session " +
                "--session_name WikarIsland --terminate_on_proc_exit --v1_metrics")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => OnLine(e.Data);
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) _lastError = e.Data.Trim();
            };
            proc.Exited += (_, _) =>
            {
                if (proc != _presentMon || !Active) return;
                bool gotFrames;
                lock (_lock) gotFrames = _msColumn >= 0;
                if (!gotFrames)
                    FpsProblem = SystemMonitorService.IsAdmin
                        ? "PresentMon не запустился" + (_lastError != null ? $": {_lastError}" : "")
                        : "Для FPS нужны права администратора";
            };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _presentMon = proc;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            FpsProblem = "PresentMon не запустился: " + ex.Message;
        }
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        string[] cells = line.Split(',');
        lock (_lock)
        {
            if (_msColumn < 0)
            {
                // первая строка — заголовок; v1: MsBetweenPresents, v2: FrameTime
                _msColumn = Array.FindIndex(cells, c => c.Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase));
                if (_msColumn < 0) _msColumn = Array.FindIndex(cells, c => c.Trim().Equals("FrameTime", StringComparison.OrdinalIgnoreCase));
                return;
            }
            if (_msColumn >= cells.Length) return;
            if (!double.TryParse(cells[_msColumn], NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) || ms <= 0 || ms > 5000) return;
            _frames.Add(ms);
            _lastFrame = DateTime.Now;
            // хватит последних ~2 секунд даже на 500 FPS
            if (_frames.Count > 1000) _frames.RemoveRange(0, _frames.Count - 1000);
        }
    }

    /// <summary>
    /// FPS по последней секунде кадров. Считаем по времени между кадрами, а не по скорости прихода строк:
    /// вывод PresentMon в трубу буферизуется и приходит пачками.
    /// </summary>
    private void ComputeFps()
    {
        lock (_lock)
        {
            if (_frames.Count == 0 || (DateTime.Now - _lastFrame).TotalSeconds > 3)
            {
                Fps = null;
                return;
            }
            double sum = 0;
            int n = 0;
            for (int i = _frames.Count - 1; i >= 0 && sum < 1000; i--)
            {
                sum += _frames[i];
                n++;
            }
            Fps = sum > 0 ? n * 1000.0 / sum : null;
        }
    }

    private void StopPresentMon()
    {
        Process? p = _presentMon;
        _presentMon = null;
        if (p == null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // уже закрылся
        }
        finally
        {
            p.Dispose();
        }
    }

    public void Dispose() => StopPresentMon();
}

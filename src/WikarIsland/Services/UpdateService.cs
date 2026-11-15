using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WikarIsland.Services;

/// <summary>Обновление острова из GitHub Releases: проверка, скачивание, замена файлов и перезапуск.</summary>
public sealed class UpdateService
{
    private const string Api = "https://api.github.com/repos/Wikaar/Wikar-DynamicIsland/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "WikarIsland");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        return http;
    }

    public static Version Current
    {
        get
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public string? LatestTag { get; private set; }
    public string? LatestNotes { get; private set; }
    public string? AssetUrl { get; private set; }
    public bool Available { get; private set; }
    public bool IsBusy { get; private set; }

    public event Action? Changed;

    /// <summary>Есть ли версия новее установленной.</summary>
    public async Task<bool> CheckAsync()
    {
        string json = await Http.GetStringAsync(Api);
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        LatestTag = root.GetProperty("tag_name").GetString();
        LatestNotes = root.TryGetProperty("body", out JsonElement body) ? body.GetString() : null;
        AssetUrl = root.GetProperty("assets").EnumerateArray()
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault(u => u != null && u.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));

        Available = AssetUrl != null && Version.TryParse(LatestTag?.TrimStart('v', 'V'), out Version? latest) && latest > Current;
        App.Settings.LastUpdateCheck = DateTime.Now;
        App.Settings.Save();
        Changed?.Invoke();
        return Available;
    }

    /// <summary>Тихая проверка раз в сутки при запуске.</summary>
    public async Task AutoCheckAsync()
    {
        if (!App.Settings.AutoUpdateCheck) return;
        if (App.Settings.LastUpdateCheck is { } last && (DateTime.Now - last).TotalHours < 20) return;
        try
        {
            if (await CheckAsync() && LatestTag != App.Settings.SkippedUpdate)
                App.Pill.ShowUpdateAvailable(LatestTag!);
        }
        catch
        {
            // нет сети — проверим в следующий раз
        }
    }

    /// <summary>Скачать новую версию, заменить файлы и перезапустить остров.</summary>
    public async Task InstallAsync(IProgress<string> progress)
    {
        if (AssetUrl == null) throw new InvalidOperationException("Сначала проверь обновления");
        IsBusy = true;
        Changed?.Invoke();
        try
        {
            string work = WorkDir;
            if (Directory.Exists(work)) Directory.Delete(work, true);
            string extract = Path.Combine(work, "files");
            Directory.CreateDirectory(extract);
            string zip = Path.Combine(work, "update.zip");

            progress.Report("Скачиваю…");
            using (HttpResponseMessage response = await Http.GetAsync(AssetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;
                await using Stream src = await response.Content.ReadAsStreamAsync();
                await using FileStream dst = File.Create(zip);
                byte[] buffer = new byte[81920];
                long done = 0;
                int read, lastPercent = -1;
                while ((read = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read));
                    done += read;
                    int percent = total > 0 ? (int)(done * 100 / total) : -1;
                    if (percent != lastPercent && percent % 5 == 0)
                    {
                        lastPercent = percent;
                        progress.Report($"Скачиваю… {percent}%");
                    }
                }
            }

            progress.Report("Распаковываю…");
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, extract, overwriteFiles: true));
            if (!File.Exists(Path.Combine(extract, "WikarIsland.exe")))
                throw new InvalidOperationException("В архиве нет WikarIsland.exe");

            string target = AppContext.BaseDirectory.TrimEnd('\\');
            if (!CanWrite(target) && !SystemMonitorService.IsAdmin)
                throw new UnauthorizedAccessException($"Нет прав на запись в {target}. Перезапусти остров от администратора и обнови снова.");

            // Новая версия сама заменяет старую: ждёт, пока этот процесс закроется, копирует свои файлы
            // в папку острова и запускает её. Никаких скриптов — их антивирусы принимают за загрузчики вирусов.
            progress.Report("Перезапускаю…");
            Process.Start(new ProcessStartInfo(Path.Combine(extract, "WikarIsland.exe"),
                $"--apply-update \"{target}\" --update-from {Environment.ProcessId}")
            {
                UseShellExecute = false,
                WorkingDirectory = extract,
            });
            App.Quit();
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
        }
    }

    private static string WorkDir => Path.Combine(Path.GetTempPath(), "WikarIslandUpdate");

    /// <summary>
    /// Запуск с --apply-update: это распакованная новая версия. Ждём закрытия старой, копируем файлы,
    /// запускаем остров из его папки. Возвращает true, если это был такой запуск.
    /// </summary>
    public static bool TryApplyUpdate(string[] args)
    {
        int i = Array.IndexOf(args, "--apply-update");
        if (i < 0 || i + 1 >= args.Length) return false;
        string target = args[i + 1].TrimEnd('\\');
        string source = AppContext.BaseDirectory.TrimEnd('\\');
        string log = Path.Combine(AppSettings.DataDir, "update.log");
        var report = new StringBuilder($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source} -> {target}\n");

        try
        {
            int p = Array.IndexOf(args, "--update-from");
            if (p >= 0 && p + 1 < args.Length && int.TryParse(args[p + 1], out int pid))
            {
                try
                {
                    using Process old = Process.GetProcessById(pid);
                    old.WaitForExit(30000);
                }
                catch
                {
                    // уже закрылся
                }
            }

            // ядро VPN из папки острова держит свои файлы — закрываем
            foreach (Process x in Process.GetProcessesByName("xray"))
            {
                using (x)
                {
                    try
                    {
                        string? path = x.MainModule?.FileName;
                        if (path == null || path.StartsWith(target, StringComparison.OrdinalIgnoreCase)) x.Kill();
                    }
                    catch
                    {
                        // нет доступа — не наш
                    }
                }
            }

            int copied = 0, failed = 0;
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                bool ok = false;
                for (int attempt = 0; attempt < 20 && !ok; attempt++)
                {
                    try
                    {
                        File.Copy(file, dest, overwrite: true);
                        ok = true;
                    }
                    catch (IOException) when (attempt < 19)
                    {
                        System.Threading.Thread.Sleep(500); // файл ещё занят закрывающимся островом
                    }
                    catch (Exception ex)
                    {
                        report.AppendLine($"  ! {dest}: {ex.Message}");
                        break;
                    }
                }
                if (ok) copied++;
                else failed++;
            }
            report.AppendLine($"  скопировано: {copied}, ошибок: {failed}");
        }
        catch (Exception ex)
        {
            report.AppendLine("  ошибка: " + ex);
        }

        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            File.WriteAllText(log, report.ToString());
        }
        catch
        {
            // без лога
        }

        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(target, "WikarIsland.exe")) { UseShellExecute = true, WorkingDirectory = target });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        return true;
    }

    /// <summary>После обновления убираем распакованную копию из временной папки.</summary>
    public static void CleanupAfterUpdate()
    {
        try
        {
            if (!Directory.Exists(WorkDir)) return;
            if (AppContext.BaseDirectory.StartsWith(WorkDir, StringComparison.OrdinalIgnoreCase)) return;
            Directory.Delete(WorkDir, true);
        }
        catch
        {
            // копия ещё занята — уберём в следующий раз
        }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

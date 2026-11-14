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
            string work = Path.Combine(Path.GetTempPath(), "WikarIslandUpdate");
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

            // скрипт ждёт выхода острова, копирует файлы поверх и запускает новую версию
            string script = Path.Combine(work, "update.cmd");
            string log = Path.Combine(AppSettings.DataDir, "update.log");
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine($":wait");
            sb.AppendLine($"tasklist /FI \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)");
            sb.AppendLine("taskkill /F /IM xray.exe >nul 2>&1");
            sb.AppendLine($"robocopy \"{extract}\" \"{target}\" /E /R:10 /W:1 /NP /NFL /NDL > \"{log}\"");
            sb.AppendLine($"start \"\" \"{Path.Combine(target, "WikarIsland.exe")}\"");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(false));

            progress.Report("Перезапускаю…");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = work,
            });
            App.Quit();
        }
        finally
        {
            IsBusy = false;
            Changed?.Invoke();
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

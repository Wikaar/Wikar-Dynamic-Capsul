using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WikarIsland.Services;

/// <summary>
/// Игры и программы, которые просто лежат в папке на диске (E:\World of Warcraft, D:\Games\…):
/// их нет ни в «Пуске», ни в «Программах и компонентах», поэтому раньше Капсулка отвечала «не найдено».
/// </summary>
public static class GameFolders
{
    private static readonly string[] Skip =
    {
        "Windows", "Users", "$Recycle.Bin", "System Volume Information", "ProgramData", "Recovery", "PerfLogs",
        "$WinREAgent", "Config.Msi", "Intel", "AMD", "NVIDIA", "Drivers", "OneDriveTemp", "$SysReset", "$Windows.~BT", "$Windows.~WS",
    };

    private static readonly string[] JunkExe =
    {
        "unins", "uninst", "crash", "report", "setup", "install", "update", "redist", "vc_redist", "dxsetup", "helper", "launcherpatcher",
        "errorreporter", "bugreport", "ue4prereq", "dotnet", "easyanticheat", "battleye", "cefprocess", "webhelper", "renderer",
    };

    /// <summary>Папки первого и второго уровня на всех локальных дисках (E:\Игра, D:\Games\Игра).</summary>
    public static List<string> DriveFolders(int depth = 2)
    {
        var result = new List<string>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            }
            catch
            {
                continue;
            }
            Walk(d.RootDirectory.FullName, depth, result);
        }
        return result;
    }

    private static void Walk(string dir, int depth, List<string> result)
    {
        IEnumerable<string> subs;
        try
        {
            subs = Directory.EnumerateDirectories(dir).ToList();
        }
        catch
        {
            return; // нет доступа
        }
        foreach (string sub in subs)
        {
            string name = Path.GetFileName(sub);
            if (name.StartsWith('.') || name.StartsWith('$') || Skip.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            try
            {
                var attr = new DirectoryInfo(sub).Attributes;
                if (attr.HasFlag(FileAttributes.ReparsePoint) || (attr.HasFlag(FileAttributes.Hidden) && attr.HasFlag(FileAttributes.System))) continue;
            }
            catch
            {
                continue;
            }
            result.Add(sub);
            if (depth > 1) Walk(sub, depth - 1, result);
        }
    }

    /// <summary>Игры Blizzard, лежащие в папках на дисках, но не записанные в реестр (перенесённые, скопированные).</summary>
    public static List<(BattleNetService.Game game, string exe)> BlizzardGames(IEnumerable<string> folders)
    {
        var result = new List<(BattleNetService.Game, string)>();
        List<string> list = folders.ToList();
        foreach (BattleNetService.Game game in BattleNetService.Known)
        {
            if (game.Processes.Length == 0) continue;
            string full = AppIndex.Fold(game.Name);
            List<string> keys = game.Keys.Select(AppIndex.Fold).Append(full).ToList();
            foreach (string dir in list)
            {
                string name = AppIndex.Fold(Path.GetFileName(dir));
                // «World of Warcraft» подходит и для Classic: у Blizzard обе версии лежат в одной папке (_retail_, _classic_)
                bool match = name.Length >= 5 && (keys.Any(k => name.Contains(k, StringComparison.Ordinal)) || full.StartsWith(name, StringComparison.Ordinal) && name.Length >= 8);
                if (!match) continue;
                string? exe = ExeInside(dir, game.Processes);
                if (exe == null) continue;
                result.Add((game, exe));
                break;
            }
        }
        return result;
    }

    /// <summary>exe с одним из имён прямо в папке или на уровень глубже (_retail_, _classic_, _forever_, bin…).</summary>
    private static string? ExeInside(string dir, string[] names)
    {
        foreach (string n in names)
        {
            string direct = Path.Combine(dir, n + ".exe");
            if (File.Exists(direct)) return direct;
        }
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(dir))
            foreach (string n in names)
            {
                string p = Path.Combine(sub, n + ".exe");
                if (File.Exists(p)) return p;
            }
        }
        catch
        {
            // нет доступа
        }
        return null;
    }

    /// <summary>
    /// Запасной поиск для «запусти …»: папка на диске с похожим названием и главный exe в ней.
    /// Возвращает (название папки, путь к exe) или null.
    /// </summary>
    public static (string name, string exe)? FindProgram(string query)
    {
        string q = AppIndex.Fold(query);
        if (q.Length < 3) return null;
        string? bestDir = null;
        double best = 0;
        foreach (string dir in DriveFolders())
        {
            double s = AppIndex.Similarity(query, Path.GetFileName(dir));
            if (s > best)
            {
                best = s;
                bestDir = dir;
            }
        }
        if (bestDir == null || best < 0.8) return null;
        string? exe = MainExe(bestDir, query);
        return exe == null ? null : (Path.GetFileName(bestDir), exe);
    }

    /// <summary>Главный exe папки: с похожим на папку или запрос именем, иначе самый большой (не установщик и не отчёт об ошибках).</summary>
    private static string? MainExe(string dir, string query)
    {
        var exes = new List<FileInfo>();
        void Collect(string d, int depth)
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(d, "*.exe"))
                {
                    string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (!JunkExe.Any(j => n.Contains(j))) exes.Add(new FileInfo(f));
                }
                if (depth > 1) foreach (string sub in Directory.EnumerateDirectories(d)) Collect(sub, depth - 1);
            }
            catch
            {
                // нет доступа
            }
        }
        Collect(dir, 3);
        if (exes.Count == 0) return null;
        string folder = Path.GetFileName(dir);
        FileInfo? named = exes
            .Select(f => (f, s: Math.Max(AppIndex.Similarity(query, Path.GetFileNameWithoutExtension(f.Name)),
                                         AppIndex.Similarity(folder, Path.GetFileNameWithoutExtension(f.Name)))))
            .Where(x => x.s >= 0.7)
            .OrderByDescending(x => x.s).ThenByDescending(x => x.f.Length)
            .Select(x => x.f).FirstOrDefault();
        return (named ?? exes.OrderByDescending(f => f.Length).First()).FullName;
    }
}

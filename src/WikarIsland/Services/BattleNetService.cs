using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WikarIsland.Services;

/// <summary>
/// Игры Battle.net: запуск сразу в игру, без кнопки «Играть».
/// Battle.net.exe --exec="launch КОД" просит клиент запустить игру. Если клиент ещё не запущен,
/// первая команда только открывает его, поэтому, когда клиент поднимется, команда повторяется.
/// Кнопки клиента UI Automation не видит, так что щёлкать по «Играть» не нужно и не получится.
/// </summary>
public static class BattleNetService
{
    /// <summary>Код запуска, по каким словам узнаём игру в списке установленных и как называется её процесс.</summary>
    public sealed record Game(string Code, string Name, string[] Keys, string[] Processes, params string[] Aliases);

    public static readonly Game[] Known =
    {
        new("WoWC", "World of Warcraft Classic", new[] { "world of warcraft classic", "wow classic" }, new[] { "WowClassic" }, "WoW Classic", "Вов Классик"),
        new("WoW", "World of Warcraft", new[] { "world of warcraft" }, new[] { "Wow" }, "WoW", "Вов", "Варкрафт"),
        new("Fen", "Diablo IV", new[] { "diablo iv", "diablo 4" }, new[] { "Diablo IV" }, "Diablo 4", "Диабло 4"),
        new("D3", "Diablo III", new[] { "diablo iii", "diablo 3" }, new[] { "Diablo III64", "Diablo III" }),
        new("OSI", "Diablo II: Resurrected", new[] { "diablo ii", "diablo 2" }, new[] { "D2R" }),
        new("ANBS", "Diablo Immortal", new[] { "diablo immortal" }, new[] { "DiabloImmortal" }),
        new("Pro", "Overwatch 2", new[] { "overwatch" }, new[] { "Overwatch" }, "Овервотч"),
        new("WTCG", "Hearthstone", new[] { "hearthstone" }, new[] { "Hearthstone" }),
        new("Hero", "Heroes of the Storm", new[] { "heroes of the storm" }, new[] { "HeroesOfTheStorm_x64", "HeroesOfTheStorm" }),
        new("S2", "StarCraft II", new[] { "starcraft ii", "starcraft 2" }, new[] { "SC2_x64", "SC2" }),
        new("S1", "StarCraft", new[] { "starcraft" }, new[] { "StarCraft" }),
        new("W3", "Warcraft III: Reforged", new[] { "warcraft iii", "warcraft 3" }, new[] { "Warcraft III" }),
        new("RTRO", "Blizzard Arcade Collection", new[] { "arcade collection" }, Array.Empty<string>()),
    };

    /// <summary>Все процессы игр Blizzard — чтобы «освободи память» и «закрой всё» их не трогали.</summary>
    public static IEnumerable<string> GameProcesses => Known.SelectMany(g => g.Processes);

    /// <summary>Путь к Battle.net.exe или null, если клиента нет.</summary>
    public static string? ClientPath()
    {
        foreach (string? dir in UninstallValues("Battle.net", "InstallLocation"))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string exe = Path.Combine(dir, "Battle.net.exe");
            if (File.Exists(exe)) return exe;
        }
        foreach (Environment.SpecialFolder f in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            string exe = Path.Combine(Environment.GetFolderPath(f), "Battle.net", "Battle.net.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Установленные игры Blizzard (по записям «Программы и компоненты»).</summary>
    public static List<Game> InstalledGames()
    {
        var result = new List<Game>();
        if (ClientPath() == null) return result;
        var names = new List<string>();
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root == null) continue;
                foreach (string sub in root.GetSubKeyNames())
                {
                    using RegistryKey? k = root.OpenSubKey(sub);
                    string publisher = k?.GetValue("Publisher") as string ?? "";
                    string display = k?.GetValue("DisplayName") as string ?? "";
                    if (publisher.Contains("Blizzard", StringComparison.OrdinalIgnoreCase) && display.Length > 0) names.Add(display);
                }
            }
            catch
            {
                // нет доступа к ветке — пропускаем
            }
        }
        foreach (string display in names)
        {
            string d = display.ToLowerInvariant();
            // первое совпадение по порядку списка: Classic раньше обычного WoW, Diablo IV раньше Diablo
            Game? g = Known.FirstOrDefault(x => x.Keys.Any(k => d.Contains(k, StringComparison.Ordinal)));
            if (g != null && !result.Contains(g)) result.Add(g);
        }
        return result;
    }

    /// <summary>Запустить игру сразу, без кнопки «Играть».</summary>
    public static void Launch(Game game)
    {
        string? exe = ClientPath();
        if (exe == null)
        {
            AppIndex.OpenUri("battlenet://" + game.Code);
            return;
        }
        bool clientRunning = Process.GetProcessesByName("Battle.net").Length > 0;
        Exec(exe, game.Code);
        if (clientRunning) return;

        // клиент только стартует: первая команда открыла его, но игру не запустила — повторяем, когда он готов
        _ = Task.Run(async () =>
        {
            DateTime until = DateTime.Now.AddSeconds(90);
            while (DateTime.Now < until)
            {
                await Task.Delay(3000);
                if (IsRunning(game)) return;
                if (ClientWindowReady())
                {
                    await Task.Delay(4000);
                    if (!IsRunning(game)) Exec(exe, game.Code);
                    return;
                }
            }
        });
    }

    public static bool IsRunning(Game game) =>
        game.Processes.Any(p => Process.GetProcessesByName(p).Length > 0);

    private static bool ClientWindowReady()
    {
        foreach (Process p in Process.GetProcessesByName("Battle.net"))
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero) return true;
            }
            catch
            {
                // процесс закрылся
            }
            finally
            {
                p.Dispose();
            }
        }
        return false;
    }

    private static void Exec(string exe, string code)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe, $"--exec=\"launch {code}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    private static IEnumerable<string?> UninstallValues(string subKey, string value)
    {
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            string? v = null;
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey? k = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + subKey);
                v = k?.GetValue(value) as string;
            }
            catch
            {
                // нет доступа
            }
            yield return v;
        }
    }
}

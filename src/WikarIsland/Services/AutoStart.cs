using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace WikarIsland.Services;

/// <summary>
/// Запуск вместе с Windows.
/// Обычный — ключ Run текущего пользователя. С правами администратора — задача в Планировщике
/// «при входе в систему» с наивысшими правами: так Windows не спрашивает разрешение при каждом старте.
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WikarIsland";
    private const string TaskName = "WikarIsland";

    public static void Apply(bool enabled, bool elevated)
    {
        SetRunKey(enabled && !elevated);
        SetTask(enabled && elevated);
    }

    /// <summary>Совместимость со старыми вызовами.</summary>
    public static void Set(bool enabled) => Apply(enabled, App.Settings?.AutoStartElevated == true);

    private static void SetRunKey(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                                    ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } path)
                key.SetValue(ValueName, $"\"{path}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // нет доступа к реестру — тихо пропускаем
        }
    }

    private static bool TaskExists()
    {
        try
        {
            using Process? p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            p?.WaitForExit(5000);
            return p?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SetTask(bool enabled)
    {
        if (!enabled && !TaskExists()) return;
        if (enabled && Environment.ProcessPath is null) return;

        string args = enabled
            ? $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:10 /TR \"\\\"{Environment.ProcessPath}\\\"\""
            : $"/Delete /F /TN \"{TaskName}\"";
        try
        {
            // задачу с наивысшими правами создаёт только администратор — Windows один раз спросит разрешение
            var info = new ProcessStartInfo("schtasks.exe", args) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (SystemMonitorService.IsAdmin)
            {
                info.UseShellExecute = false;
            }
            else
            {
                info.UseShellExecute = true;
                info.Verb = "runas";
            }
            Process.Start(info)?.WaitForExit(10000);
        }
        catch (Exception ex)
        {
            App.LogError(ex); // отказались в окне UAC
        }
    }
}

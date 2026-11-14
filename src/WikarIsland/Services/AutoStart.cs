using System;
using Microsoft.Win32;

namespace WikarIsland.Services;

/// <summary>Запуск вместе с Windows через ключ Run текущего пользователя.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WikarIsland";

    public static void Set(bool enabled)
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
}

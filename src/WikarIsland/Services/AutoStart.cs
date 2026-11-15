using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace WikarIsland.Services;

/// <summary>
/// Запуск вместе с Windows.
/// Обычный — ключ Run текущего пользователя. С правами администратора — задача в Планировщике
/// «при входе в систему» с наивысшими правами: так Windows не спрашивает разрешение при каждом старте.
/// Задача описывается XML целиком: у schtasks /Create по умолчанию «только от сети» (ноутбук на батарее не запустит),
/// «остановить через 72 часа» и пониженный приоритет — из-за этого остров при входе мог не стартовать или пропадать.
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

    /// <summary>
    /// При каждом старте: автозапуск должен указывать на этот exe (папку могли переложить или распаковать новую версию).
    /// Задачу с наивысшими правами без окна UAC можно пересоздать, только когда мы сами запущены от администратора;
    /// если задачи нет вовсе, а прав нет — запасной вариант: обычный автозапуск, чтобы остров хотя бы стартовал.
    /// </summary>
    public static void Ensure(bool elevated)
    {
        try
        {
            if (!elevated)
            {
                SetRunKey(true);
                return;
            }

            string? path = Environment.ProcessPath;
            if (path == null) return;
            string? xml = TaskXml();
            bool exists = xml != null || TaskExists();
            bool pathOk = xml == null ? exists : xml.Contains(SecurityElement.Escape(path), StringComparison.OrdinalIgnoreCase);
            bool settingsOk = xml != null && xml.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", StringComparison.Ordinal);

            if (SystemMonitorService.IsAdmin)
            {
                if (!pathOk || !settingsOk) SetTask(true);
                SetRunKey(false);
            }
            else if (exists && pathOk)
            {
                SetRunKey(false);
            }
            else
            {
                SetRunKey(true);
                App.LogError(new InvalidOperationException(
                    "Задача автозапуска с правами администратора не найдена или указывает на старую папку — включён обычный автозапуск. " +
                    "Чтобы вернуть запуск от администратора, сними и поставь галочку в настройках."));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

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

    /// <summary>XML существующей задачи через COM Планировщика (null — нет задачи или нет доступа).</summary>
    private static string? TaskXml()
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("Schedule.Service");
            if (type == null) return null;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            dynamic task = folder.GetTask(TaskName);
            return (string)task.Xml;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildXml(string exePath)
    {
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name) ?? "";
        string exe = SecurityElement.Escape(exePath) ?? "";
        string dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "") ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Wikar Island — запуск при входе в Windows</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                  <Delay>PT5S</Delay>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>4</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static void SetTask(bool enabled)
    {
        if (!enabled && !TaskExists()) return;
        if (enabled && Environment.ProcessPath is null) return;

        string? xmlFile = null;
        string args;
        if (enabled)
        {
            xmlFile = Path.Combine(Path.GetTempPath(), "WikarIsland-autostart.xml");
            File.WriteAllText(xmlFile, BuildXml(Environment.ProcessPath!), Encoding.Unicode); // schtasks ждёт UTF-16
            args = $"/Create /F /TN \"{TaskName}\" /XML \"{xmlFile}\"";
        }
        else
        {
            args = $"/Delete /F /TN \"{TaskName}\"";
        }

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
            using Process? p = Process.Start(info);
            if (p != null && p.WaitForExit(10000) && p.ExitCode != 0)
                App.LogError(new InvalidOperationException($"schtasks {(enabled ? "/Create" : "/Delete")} завершился с кодом {p.ExitCode}"));
        }
        catch (Exception ex)
        {
            App.LogError(ex); // отказались в окне UAC
        }
        finally
        {
            if (xmlFile != null)
            {
                try
                {
                    File.Delete(xmlFile);
                }
                catch
                {
                    // не страшно
                }
            }
        }
    }
}

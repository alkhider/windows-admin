using Microsoft.Win32;
using System.ServiceProcess;
using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class SystemTaskService
{
    public IReadOnlyList<ServiceItem> GetServices()
    {
        try
        {
            return ServiceController.GetServices()
                .OrderBy(s => s.DisplayName)
                .Select(s => new ServiceItem
                {
                    Name = s.ServiceName,
                    DisplayName = s.DisplayName,
                    Status = s.Status.ToString(),
                    StartType = SafeStartType(s)
                })
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not read Windows services. " + ex.Message, ex);
        }
    }

    public OperationResult SetServiceRunning(string serviceName, bool start)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            if (start)
            {
                if (service.Status != ServiceControllerStatus.Running)
                {
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }

                return OperationResult.Success($"{service.DisplayName} is running.");
            }

            if (service.Status != ServiceControllerStatus.Stopped)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }

            return OperationResult.Success($"{service.DisplayName} was stopped.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public IReadOnlyList<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();
        ReadRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Current user", items);
        ReadRunKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "This PC", items);
        items.AddRange(ReadPrefetchHints());
        return items.OrderBy(i => i.Name).ToList();
    }

    public async Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync()
    {
        var items = GetStartupItems().ToList();
        items.AddRange(await ReadLogonScheduledTasksAsync());
        return items.OrderBy(i => i.Name).ToList();
    }

    private static IEnumerable<StartupItem> ReadPrefetchHints()
    {
        try
        {
            var prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
            if (!Directory.Exists(prefetch))
            {
                return [];
            }

            return Directory.EnumerateFiles(prefetch, "*.pf")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(15)
                .Select(f => new StartupItem
                {
                    Name = Path.GetFileNameWithoutExtension(f.Name).Replace('-', ' '),
                    Command = $"Last run hint · {f.LastWriteTime:g}",
                    Location = "Prefetch"
                })
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<StartupItem>> ReadLogonScheduledTasksAsync()
    {
        var result = await HiddenProcess.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-ScheduledTask | Where-Object { $_.Triggers.CimClass.CimClassName -match 'Logon|Boot' -and $_.State -ne 'Disabled' } | Select-Object -First 40 TaskName, TaskPath, State | ConvertTo-Csv -NoTypeInformation\"",
            timeoutMs: 90_000);
        if (result.ExitCode != 0)
        {
            return [];
        }

        var list = new List<StartupItem>();
        foreach (var line in result.Output.Replace("\r", "").Split('\n').Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var cols = line.Split(',').Select(c => c.Trim('"')).ToArray();
            if (cols.Length < 2)
            {
                continue;
            }

            list.Add(new StartupItem
            {
                Name = cols[0],
                Command = cols.Length > 1 ? cols[1] : "",
                Location = "Task Scheduler · " + (cols.Length > 2 ? cols[2] : "")
            });
        }

        return list;
    }

    public async Task<OperationResult> RestartAsync(int delaySeconds = 60)
    {
        var result = await HiddenProcess.RunAsync("shutdown.exe", $"/r /t {delaySeconds} /c \"Restart scheduled from Windows Admin\"");
        return result.ExitCode == 0
            ? OperationResult.Success($"Restart scheduled in {delaySeconds} seconds. Use Cancel restart to abort.")
            : OperationResult.Fail(result.Error.Trim().Length > 0 ? result.Error.Trim() : "Could not schedule restart.");
    }

    public async Task<OperationResult> ShutdownAsync(int delaySeconds = 60)
    {
        var result = await HiddenProcess.RunAsync("shutdown.exe", $"/s /t {delaySeconds} /c \"Shutdown scheduled from Windows Admin\"");
        return result.ExitCode == 0
            ? OperationResult.Success($"Shutdown scheduled in {delaySeconds} seconds. Use Cancel restart to abort.")
            : OperationResult.Fail(result.Error.Trim().Length > 0 ? result.Error.Trim() : "Could not schedule shutdown.");
    }

    public async Task<OperationResult> CancelPowerAsync()
    {
        var result = await HiddenProcess.RunAsync("shutdown.exe", "/a");
        return result.ExitCode == 0
            ? OperationResult.Success("The scheduled restart or shutdown was cancelled.")
            : OperationResult.Fail("Nothing was pending to cancel.");
    }

    private static void ReadRunKey(RegistryKey root, string path, string location, List<StartupItem> items)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                return;
            }

            foreach (var name in key.GetValueNames())
            {
                items.Add(new StartupItem
                {
                    Name = name,
                    Command = key.GetValue(name)?.ToString() ?? "",
                    Location = location
                });
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string SafeStartType(ServiceController service)
    {
        try
        {
            return service.StartType.ToString();
        }
        catch
        {
            return "Unknown";
        }
    }
}

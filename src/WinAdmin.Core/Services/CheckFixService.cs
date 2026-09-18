using Microsoft.Win32;
using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class CheckFixService(
    HealthService health,
    LiveMetricsService metrics,
    StorageService storage,
    StorageSenseService sense,
    SystemTaskService tasks,
    ProcessService processes,
    WindowsUpdateService updates,
    FirewallService firewall)
{
    public async Task<CheckScanResult> ScanAsync(IProgress<string>? progress = null)
    {
        var result = new CheckScanResult();
        void Step(string name)
        {
            result.Steps.Add(name);
            progress?.Report(name);
        }

        Step("Storage");
        var report = health.GetReport();
        var live = metrics.Sample();
        var hottest = report.Drives.OrderByDescending(d => d.UsedPercent).FirstOrDefault();
        var cleanup = storage.GetCleanupTargets()
            .Where(t => t.CanClean)
            .ToList();
        var reclaimable = cleanup.Sum(t => t.SizeBytes);
        var senseSettings = sense.GetSettings();

        if (hottest is not null && hottest.UsedPercent >= 85)
        {
            result.Findings.Add(Issue(
                "disk-full", "Storage", hottest.UsedPercent >= 95 ? "issue" : "warn",
                $"Free space on {hottest.Name.TrimEnd('\\')}",
                $"{hottest.Name} is {hottest.UsedPercent}% full ({StorageService.FormatBytes(hottest.FreeBytes)} free).",
                "storage-sense", "Run Storage Sense", safe: true, confirm: true));
        }
        else
        {
            result.Findings.Add(Ok("disk-ok", "Storage", "Disk space looks fine", hottest is null
                ? "No local drive was measured."
                : $"{hottest.Name} has {StorageService.FormatBytes(hottest.FreeBytes)} free."));
        }

        if (reclaimable >= 200L * 1024 * 1024)
        {
            result.Findings.Add(Issue(
                "temp-files", "Storage", "warn",
                "Temporary files can be cleaned",
                $"About {StorageService.FormatBytes(reclaimable)} in temp folders and Recycle Bin can be removed.",
                "cleanup-temp", "Clean temp files", safe: true));
        }
        else
        {
            result.Findings.Add(Ok("temp-ok", "Storage", "Temp files are light",
                $"About {StorageService.FormatBytes(reclaimable)} could be reclaimed."));
        }

        if (!senseSettings.Enabled)
        {
            result.Findings.Add(Issue(
                "sense-off", "Storage", "warn",
                "Storage Sense is off",
                "Windows can automatically free disk space. Turn it on from this portal.",
                "enable-sense", "Turn on Storage Sense", safe: true));
        }

        Step("Protection");
        if (report.Defender.Available && (!report.Defender.AntivirusEnabled || !report.Defender.RealTimeProtection))
        {
            result.Findings.Add(Issue(
                "defender", "Protection", "issue",
                "Defender real-time protection is off",
                "Start Microsoft Defender from this portal. No Windows Security popup.",
                "start-defender", "Turn protection on", safe: true));
        }
        else if (report.Defender.Available)
        {
            result.Findings.Add(Ok("defender-ok", "Protection", "Defender is on", report.Defender.SignatureAge));
        }

        if (!firewall.IsEnabled())
        {
            result.Findings.Add(Issue(
                "firewall", "Protection", "issue",
                "Windows Firewall is off",
                $"Active profiles: {firewall.GetProfileSummary()}.",
                "enable-firewall", "Turn firewall on", safe: true));
        }
        else
        {
            result.Findings.Add(Ok("firewall-ok", "Protection", "Firewall is on", firewall.GetProfileSummary()));
        }

        foreach (var service in report.CriticalServices.Where(s =>
                     !string.Equals(s.Status, "Running", StringComparison.OrdinalIgnoreCase)))
        {
            result.Findings.Add(Issue(
                "svc-" + service.Name, "Services", "issue",
                $"{service.DisplayName} is {service.Status}",
                "This is a watched Windows service. Start it without opening services.msc.",
                "start-service", "Start service", safe: true, extra: service.Name));
        }

        Step("Updates");
        var updateCount = 0;
        try
        {
            var pending = await updates.GetAvailableAsync().WaitAsync(TimeSpan.FromSeconds(25));
            updateCount = pending.Count;
        }
        catch
        {
            // WU scan can time out
        }

        report = health.GetReport(updateCount);
        if (updateCount > 0)
        {
            result.Findings.Add(Issue(
                "updates", "Updates", "warn",
                $"{updateCount} Windows update(s) waiting",
                "Install from this portal. Windows Update Agent stays in the background.",
                "install-updates", "Install updates", confirm: true));
        }
        else
        {
            result.Findings.Add(Ok("updates-ok", "Updates", "No pending updates found", "Scan completed with Windows Update Agent."));
        }

        if (report.PendingReboot)
        {
            result.Findings.Add(Issue(
                "reboot", "Updates", "issue",
                "A restart is pending",
                "Finish the restart to complete servicing. You can cancel from System tasks.",
                "reboot", "Restart in 60 seconds", confirm: true));
        }
        else
        {
            result.Findings.Add(Ok("reboot-ok", "Updates", "No restart waiting", "Windows is not holding a reboot."));
        }

        Step("Performance");
        var highPerf = await IsHighPerformancePlanAsync();
        if (live.MemoryPercent >= 85 || report.Memory.UsedPercent >= 85)
        {
            var mem = Math.Max(live.MemoryPercent, report.Memory.UsedPercent);
            result.Findings.Add(Issue(
                "memory", "Performance", mem >= 92 ? "issue" : "warn",
                "Memory is under pressure",
                $"Memory is {mem}% used ({StorageService.FormatBytes(live.MemoryUsedBytes)} in use).",
                "open-processes", "Open processes"));
        }
        else
        {
            result.Findings.Add(Ok("memory-ok", "Performance", "Memory is healthy", $"{live.MemoryPercent}% used."));
        }

        var top = processes.GetTopByCpu();
        if (top is not null && top.CpuPercent >= 25
            && !top.Name.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            && !top.Name.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase)
            && !top.Name.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            result.Findings.Add(Issue(
                "high-cpu", "Performance", top.CpuPercent >= 50 ? "issue" : "warn",
                $"{top.Name} is using the CPU",
                $"{top.Name} (PID {top.Id}) is at {top.CpuPercent:0.0}%. Confirm before ending the task.",
                "end-process", "End this task", confirm: true, extra: top.Id.ToString()));
        }

        if (live.CpuPercent >= 85)
        {
            result.Findings.Add(Issue(
                "cpu-hot", "Performance", "warn",
                "CPU is busy",
                $"Total CPU is {live.CpuPercent}%. A performance boost can reduce visual effects and use the High performance plan.",
                "performance-boost", "Boost performance", safe: true));
        }
        else if (!IsBestPerformanceVisuals() || !highPerf)
        {
            result.Findings.Add(Issue(
                "perf-tune", "Performance", "warn",
                "Windows is not set for best performance",
                "Same idea as the Windows Performance troubleshooter: fewer visual effects and the High performance power plan.",
                "performance-boost", "Boost performance", safe: true));
        }
        else
        {
            result.Findings.Add(Ok("perf-ok", "Performance", "Performance settings look tuned",
                $"CPU {live.CpuPercent}% · visual effects already favor speed."));
        }

        Step("Startup");
        var startup = tasks.GetStartupItems()
            .Where(s => s.Location is "Current user" or "This PC")
            .ToList();
        if (startup.Count >= 10)
        {
            result.Findings.Add(Issue(
                "startup", "Startup", "warn",
                $"{startup.Count} programs start with Windows",
                "Too many Run-key apps can slow logon. Review them on System tasks.",
                "open-tasks", "Review startup"));
        }
        else
        {
            result.Findings.Add(Ok("startup-ok", "Startup", "Startup list is reasonable", $"{startup.Count} Run-key items."));
        }

        if (report.RecentErrors.Count >= 8)
        {
            result.Findings.Add(Issue(
                "events", "Health", "warn",
                $"{report.RecentErrors.Count} recent error events",
                "Review System errors. This does not change the log.",
                "open-events", "Open events"));
        }

        var issues = result.Findings.Count(f => f.Severity == "issue");
        var warns = result.Findings.Count(f => f.Severity == "warn");
        result.Score = Math.Clamp(100 - issues * 12 - warns * 6, 5, 100);
        result.Label = result.Score >= 85 ? "Healthy" : result.Score >= 65 ? "Fair" : "Needs attention";
        Step("Done");
        return result;
    }

    public async Task<OperationResult> FixAsync(CheckFinding finding, IProgress<JobProgress>? progress = null)
    {
        try
        {
            return finding.Action switch
            {
                "storage-sense" => await sense.RunCleanupAsync(),
                "cleanup-temp" => storage.Clean(["user-temp", "windows-temp", "recycle"], progress),
                "enable-sense" => sense.SaveSettings(new StorageSenseSettings { Enabled = true, RecycleDays = 30, DownloadsDays = 0 }),
                "start-defender" => StartDefender(),
                "enable-firewall" => firewall.EnableFirewall(),
                "start-service" => tasks.SetServiceRunning(finding.Extra, true),
                "install-updates" => await updates.InstallAllAsync(progress),
                "reboot" => await tasks.RestartAsync(),
                "end-process" => EndPid(finding.Extra),
                "performance-boost" => await BoostPerformanceAsync(),
                "flush-dns" => await FlushDnsAsync(),
                _ => OperationResult.Fail("This finding is review-only.")
            };
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public bool CanAutoFix(CheckFinding finding) =>
        finding.Safe && !finding.NeedsConfirm && finding.State == "Open"
        && finding.Action is "cleanup-temp" or "enable-sense" or "start-defender" or "enable-firewall"
            or "start-service" or "performance-boost" or "storage-sense";

    private OperationResult StartDefender()
    {
        var first = tasks.SetServiceRunning("WinDefend", true);
        _ = tasks.SetServiceRunning("WdNisSvc", true);
        return first;
    }

    private OperationResult EndPid(string extra)
    {
        if (!int.TryParse(extra, out var pid))
        {
            return OperationResult.Fail("No process id on this finding.");
        }

        return processes.EndTask(pid);
    }

    private async Task<OperationResult> BoostPerformanceAsync()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", true);
            key?.SetValue("VisualFXSetting", 2, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            return OperationResult.Fail("Could not set visual effects. " + ex.Message);
        }

        var power = await HiddenProcess.RunAsync("powercfg.exe", "/setactive SCHEME_MAX", timeoutMs: 20_000);
        if (power.ExitCode != 0)
        {
            power = await HiddenProcess.RunAsync(
                "powercfg.exe",
                "/setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
                timeoutMs: 20_000);
        }

        return power.ExitCode == 0
            ? OperationResult.Success("Visual effects set for best performance and the High performance plan is active.")
            : OperationResult.Success("Visual effects set for best performance. The High performance plan was not available on this PC.");
    }

    private static async Task<OperationResult> FlushDnsAsync()
    {
        var result = await HiddenProcess.RunAsync("ipconfig.exe", "/flushdns", timeoutMs: 15_000);
        return result.ExitCode == 0
            ? OperationResult.Success("DNS cache flushed.")
            : OperationResult.Fail("Could not flush DNS.");
    }

    private static bool IsBestPerformanceVisuals()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects");
            return Convert.ToInt32(key?.GetValue("VisualFXSetting") ?? 0) == 2;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsHighPerformancePlanAsync()
    {
        try
        {
            var result = await HiddenProcess.RunAsync("powercfg.exe", "/getactivescheme", timeoutMs: 8_000);
            var text = (result.Output + " " + result.Error).ToLowerInvariant();
            return text.Contains("high performance") || text.Contains("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        }
        catch
        {
            return false;
        }
    }

    private static CheckFinding Issue(
        string id, string category, string severity, string title, string detail,
        string action, string label, bool safe = false, bool confirm = false, string extra = "") =>
        new()
        {
            Id = id,
            Category = category,
            Severity = severity,
            Title = title,
            Detail = detail,
            Action = action,
            ActionLabel = label,
            Safe = safe,
            NeedsConfirm = confirm,
            Extra = extra
        };

    private static CheckFinding Ok(string id, string category, string title, string detail) =>
        new()
        {
            Id = id,
            Category = category,
            Severity = "ok",
            Title = title,
            Detail = detail,
            Action = "",
            ActionLabel = "",
            State = "Fixed"
        };
}

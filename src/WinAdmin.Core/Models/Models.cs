namespace WinAdmin.Core.Models;

public sealed class HealthReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public string ComputerName { get; init; } = Environment.MachineName;
    public string UserName { get; init; } = Environment.UserName;
    public bool IsAdministrator { get; init; }
    public OsInfo Os { get; init; } = new();
    public HardwareInfo Hardware { get; init; } = new();
    public MemoryInfo Memory { get; init; } = new();
    public CpuInfo Cpu { get; init; } = new();
    public IReadOnlyList<DriveStatus> Drives { get; init; } = [];
    public DefenderStatus Defender { get; init; } = new();
    public bool PendingReboot { get; init; }
    public TimeSpan Uptime { get; init; }
    public int PendingUpdates { get; set; }
    public int HealthScore { get; set; }
    public string HealthLabel { get; set; } = "Unknown";
    public IReadOnlyList<string> Findings { get; set; } = [];
    public IReadOnlyList<EventItem> RecentErrors { get; init; } = [];
    public IReadOnlyList<ServiceItem> CriticalServices { get; init; } = [];
}

public sealed class OsInfo
{
    public string Caption { get; init; } = "";
    public string Version { get; init; } = "";
    public string Build { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string InstallDate { get; init; } = "";
}

public sealed class HardwareInfo
{
    public string Manufacturer { get; init; } = "";
    public string Model { get; init; } = "";
    public string Processor { get; init; } = "";
}

public sealed class MemoryInfo
{
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public int UsedPercent => TotalBytes == 0 ? 0 : (int)Math.Round(UsedBytes * 100d / TotalBytes);
}

public sealed class CpuInfo
{
    public int LogicalProcessors { get; init; } = Environment.ProcessorCount;
    public int LoadPercent { get; init; }
}

public sealed class DriveStatus
{
    public string Name { get; init; } = "";
    public string Label { get; init; } = "";
    public string Format { get; init; } = "";
    public string Type { get; init; } = "";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
    public int UsedPercent => TotalBytes == 0 ? 0 : (int)Math.Round(UsedBytes * 100d / TotalBytes);
}

public sealed class DefenderStatus
{
    public bool Available { get; init; }
    public bool AntivirusEnabled { get; init; }
    public bool RealTimeProtection { get; init; }
    public string SignatureAge { get; init; } = "Unknown";
}

public sealed class EventItem
{
    public DateTime Time { get; init; }
    public string Source { get; init; } = "";
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class WindowsUpdateItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kb { get; init; } = "";
    public string Severity { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool IsDownloaded { get; init; }
    public bool RebootRequired { get; init; }
}

public sealed class UpdateHistoryItem
{
    public DateTime Date { get; init; }
    public string Title { get; init; } = "";
    public string Result { get; init; } = "";
}

public sealed class InstalledApp
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string UninstallString { get; init; } = "";
    public string ProductCode { get; init; } = "";
    public string InstallLocation { get; init; } = "";
    public bool QuietUninstall { get; init; }
    public bool IsMsi { get; init; }
}

public sealed class CatalogApp
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Version { get; init; } = "";
    public string Source { get; init; } = "";
}

public sealed class CleanupTarget
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Description { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool CanClean { get; init; } = true;
}

public sealed class LargeFolder
{
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
}

public sealed class StorageOverview
{
    public IReadOnlyList<DriveStatus> Drives { get; init; } = [];
    public IReadOnlyList<CleanupTarget> CleanupTargets { get; init; } = [];
    public IReadOnlyList<LargeFolder> LargeFolders { get; init; } = [];
    public string? IndexStatus { get; init; }
    public long ReclaimableBytes => CleanupTargets.Where(t => t.CanClean).Sum(t => t.SizeBytes);
}

public sealed class ServiceItem
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "";
    public string StartType { get; init; } = "";
}

public sealed class StartupItem
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string Location { get; init; } = "";
}

public sealed class ClockStatus
{
    public DateTime LocalTime { get; init; }
    public DateTime UtcTime { get; init; }
    public string TimeZoneId { get; init; } = "";
    public string TimeZoneName { get; init; } = "";
    public bool IsDaylightSaving { get; init; }
}

public sealed class TimeZoneChoice
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class AdminJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string State { get; set; } = "Running";
    public int Percent { get; set; }
    public string Message { get; set; } = "Starting…";
    public DateTime StartedAt { get; init; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }
}

public sealed class JobProgress
{
    public int Percent { get; init; }
    public string Message { get; init; } = "";
}

public sealed class OperationResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
    public bool RebootRequired { get; init; }

    public static OperationResult Success(string message, bool reboot = false) =>
        new() { Ok = true, Message = message, RebootRequired = reboot };

    public static OperationResult Fail(string message) =>
        new() { Ok = false, Message = message };
}

public sealed class IndexedFileItem
{
    public string Path { get; init; } = "";
    public long SizeBytes { get; init; }
    public DateTime? Modified { get; init; }
    public string Kind { get; init; } = "";
}

public sealed class IndexerStatus
{
    public bool Available { get; init; }
    public string Status { get; init; } = "";
    public string? UrlBeingIndexed { get; init; }
    public string Service { get; init; } = "";
    public string? LastError { get; init; }
    public bool QueryOk { get; init; }
}

public sealed class StorageSenseSettings
{
    public bool Enabled { get; init; }
    public int RecycleDays { get; init; }
    public int DownloadsDays { get; init; }
}

public sealed class ProcessItem
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public long WorkingSetBytes { get; init; }
    public double CpuSeconds { get; init; }
    public double CpuPercent { get; init; }
    public long DiskBytesPerSec { get; init; }
    public int ThreadCount { get; init; }
}

public sealed class ThermalZone
{
    public string Name { get; init; } = "";
    public int Celsius { get; init; }
    public string Source { get; init; } = "";
}

public sealed class ScheduledTaskItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string State { get; init; } = "";
    public string Trigger { get; init; } = "";
}

public sealed class FirewallRuleItem
{
    public string Name { get; init; } = "";
    public string Direction { get; init; } = "";
    public bool Enabled { get; init; }
    public string Action { get; init; } = "";
}

public sealed class CertificateItem
{
    public string Subject { get; init; } = "";
    public string Store { get; init; } = "";
    public DateTime NotAfter { get; init; }
    public int DaysLeft { get; init; }
}

public sealed class DeviceItem
{
    public string Name { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public string Status { get; init; } = "";
    public int ProblemCode { get; init; }
}

public sealed class TransferJobItem
{
    public string Name { get; init; } = "";
    public string State { get; init; } = "";
    public long BytesTransferred { get; init; }
}

public sealed class LiveMetrics
{
    public int CpuPercent { get; init; }
    public int CpuFrequencyMhz { get; init; }
    public int MemoryPercent { get; init; }
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
    public int DiskPercent { get; init; }
    public int DiskQueue { get; init; }
    public long DiskBytesPerSec { get; init; }
    public long NetworkBytesPerSec { get; init; }
    public int GpuPercent { get; init; }
    public int? CpuTempC { get; init; }
    public string TemperatureStatus { get; init; } = "";
    public IReadOnlyList<ThermalZone> ThermalZones { get; init; } = [];
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public string? TopProcessName { get; init; }
    public double TopProcessCpu { get; init; }
    public EventItem? LastCriticalEvent { get; init; }
}

public sealed class TelemetrySample
{
    public DateTime At { get; init; } = DateTime.UtcNow;
    public int CpuPercent { get; init; }
    public int MemoryPercent { get; init; }
    public int DiskBusyPercent { get; init; }
    public int DiskUsedPercent { get; init; }
    public int GpuPercent { get; init; }
    public double TopProcessCpu { get; init; }
    public string TopProcessName { get; init; } = "";
    public int ErrorCount { get; init; }
    public bool PendingReboot { get; init; }
    public bool DefenderOff { get; init; }
    public int UpdateCount { get; init; }
}

public sealed class RecommendationFeedback
{
    public DateTime At { get; init; } = DateTime.UtcNow;
    public string Kind { get; init; } = "";
    public bool Accepted { get; init; }
    public float Disk { get; init; }
    public float Memory { get; init; }
    public float Cpu { get; init; }
    public float Gpu { get; init; }
    public float Reboot { get; init; }
    public float DefenderOff { get; init; }
    public float Errors { get; init; }
    public float Updates { get; init; }
    public float TopCpu { get; init; }
}

public sealed class CheckFinding
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public string Severity { get; init; } = "issue";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Action { get; init; } = "";
    public string ActionLabel { get; init; } = "Fix";
    public bool NeedsConfirm { get; init; }
    public bool Safe { get; init; }
    public string Extra { get; init; } = "";
    public string State { get; set; } = "Open";
    public string ResultMessage { get; set; } = "";
}

public sealed class CheckScanResult
{
    public DateTime At { get; init; } = DateTime.Now;
    public int Score { get; set; }
    public string Label { get; set; } = "Unknown";
    public List<string> Steps { get; } = [];
    public List<CheckFinding> Findings { get; } = [];
    public int IssueCount => Findings.Count(f => f.Severity is "issue" or "warn");
    public int PassedCount => Findings.Count(f => f.Severity == "ok");
}

public sealed class RecommendationItem
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public string Why { get; init; } = "";
    public string ActionLabel { get; init; } = "Do this";
    public string Action { get; init; } = "";
    public string? NavigateTo { get; init; }
    public bool NeedsConfirm { get; init; }
    public double RuleScore { get; init; }
    public double Confidence { get; set; }
    public string Source { get; set; } = "Rules";
}

public sealed class FolderEntry
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public DateTime? Modified { get; init; }
}

public sealed class FolderVisit
{
    public DateTime At { get; init; } = DateTime.UtcNow;
    public string Path { get; init; } = "";
}

public sealed class FrequentLocation
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public int Visits { get; init; }
    public DateTime LastVisit { get; init; }
    public double Score { get; init; }
    public bool Pinned { get; init; }
}

public sealed class TreeFolder
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool Expanded { get; set; }
    public bool Loaded { get; set; }
    public bool IsDrive { get; set; }
    public bool MayHaveChildren { get; set; } = true;
    public List<TreeFolder> Children { get; set; } = [];
}

public sealed class MachineStamp
{
    public string MachineGuid { get; set; } = "";
    public string MachineName { get; set; } = "";
    public DateTime StampAt { get; set; } = DateTime.UtcNow;
}

public sealed class MachineResetResult
{
    public bool IsNewPc { get; init; }
    public bool DataRemoved { get; init; }
    public string Message { get; init; } = "";
}

public sealed class SupportLibrary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Why { get; init; } = "";
    public bool Required { get; init; }
    public bool Installed { get; set; }
    public string DownloadUrl { get; init; } = "";
    public string Detail { get; set; } = "";
    public string Status => Installed
        ? (string.IsNullOrWhiteSpace(Detail) ? "Ready" : "Ready · " + Detail)
        : Required
            ? (string.IsNullOrWhiteSpace(Detail) ? "Required · not found" : "Required · " + Detail)
            : (string.IsNullOrWhiteSpace(Detail) ? "Optional · not found" : "Optional · " + Detail);
}

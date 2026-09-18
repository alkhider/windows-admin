using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class HealthService
{
    private static readonly string[] WatchedServices =
    [
        "WinDefend", "wuauserv", "bits", "EventLog", "RpcSs", "Dhcp", "Dnscache", "LanmanServer", "LanmanWorkstation"
    ];

    public HealthReport GetReport(int pendingUpdates = 0)
    {
        var os = ReadOs();
        var hardware = ReadHardware();
        var memory = ReadMemory();
        var cpu = ReadCpu();
        var drives = ReadDrives();
        var defender = ReadDefender();
        var pendingReboot = IsRebootPending();
        var errors = ReadRecentErrors();
        var services = ReadCriticalServices();
        var findings = new List<string>();

        if (pendingReboot)
        {
            findings.Add("A restart is pending. Finish the restart to complete previous updates.");
        }

        if (memory.UsedPercent >= 90)
        {
            findings.Add($"Memory is under pressure ({memory.UsedPercent}% used).");
        }

        foreach (var drive in drives.Where(d => d.UsedPercent >= 90))
        {
            findings.Add($"Drive {drive.Name} is almost full ({drive.UsedPercent}% used).");
        }

        if (defender.Available && (!defender.AntivirusEnabled || !defender.RealTimeProtection))
        {
            findings.Add("Microsoft Defender real-time protection is not fully enabled.");
        }

        if (pendingUpdates > 0)
        {
            findings.Add($"{pendingUpdates} Windows update(s) are waiting to be installed.");
        }

        if (errors.Count >= 8)
        {
            findings.Add($"{errors.Count} error events were recorded in the last 3 days.");
        }

        foreach (var service in services.Where(s => !string.Equals(s.Status, "Running", StringComparison.OrdinalIgnoreCase)))
        {
            findings.Add($"Service {service.DisplayName} is {service.Status}.");
        }

        var score = 100;
        score -= pendingReboot ? 15 : 0;
        score -= memory.UsedPercent >= 90 ? 15 : memory.UsedPercent >= 80 ? 8 : 0;
        score -= drives.Count(d => d.UsedPercent >= 95) * 20;
        score -= drives.Count(d => d.UsedPercent is >= 90 and < 95) * 10;
        score -= defender.Available && !defender.RealTimeProtection ? 20 : 0;
        score -= Math.Min(20, pendingUpdates * 4);
        score -= Math.Min(15, errors.Count);
        score = Math.Clamp(score, 5, 100);

        var label = score >= 85 ? "Healthy" : score >= 65 ? "Fair" : "Needs attention";
        if (findings.Count == 0)
        {
            findings.Add("No urgent issues were found. The PC looks in good shape.");
        }

        return new HealthReport
        {
            IsAdministrator = IsAdministrator(),
            Os = os,
            Hardware = hardware,
            Memory = memory,
            Cpu = cpu,
            Drives = drives,
            Defender = defender,
            PendingReboot = pendingReboot,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            PendingUpdates = pendingUpdates,
            HealthScore = score,
            HealthLabel = label,
            Findings = findings,
            RecentErrors = errors,
            CriticalServices = services
        };
    }

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRebootPending()
    {
        try
        {
            using var wu = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            var pendingRenames = session?.GetValue("PendingFileRenameOperations") is not null;
            return wu is not null || cbs is not null || pendingRenames;
        }
        catch
        {
            return false;
        }
    }

    private static OsInfo ReadOs()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                return new OsInfo
                {
                    Caption = obj["Caption"]?.ToString()?.Trim() ?? "",
                    Version = obj["Version"]?.ToString() ?? "",
                    Build = obj["BuildNumber"]?.ToString() ?? "",
                    Architecture = obj["OSArchitecture"]?.ToString() ?? "",
                    InstallDate = FormatDmtf(obj["InstallDate"]?.ToString())
                };
            }
        }
        catch
        {
            // fall through
        }

        return new OsInfo
        {
            Caption = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString()
        };
    }

    private static HardwareInfo ReadHardware()
    {
        var manufacturer = "";
        var model = "";
        var processor = "";

        TryWmi("SELECT Manufacturer, Model FROM Win32_ComputerSystem", obj =>
        {
            manufacturer = obj["Manufacturer"]?.ToString()?.Trim() ?? "";
            model = obj["Model"]?.ToString()?.Trim() ?? "";
        });

        TryWmi("SELECT Name FROM Win32_Processor", obj =>
        {
            processor = obj["Name"]?.ToString()?.Trim() ?? "";
        });

        return new HardwareInfo { Manufacturer = manufacturer, Model = model, Processor = processor };
    }

    internal static MemoryInfo ReadMemory()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var totalKb = Convert.ToInt64(obj["TotalVisibleMemorySize"]);
                var freeKb = Convert.ToInt64(obj["FreePhysicalMemory"]);
                return new MemoryInfo { TotalBytes = totalKb * 1024, FreeBytes = freeKb * 1024 };
            }
        }
        catch
        {
            // fall through
        }

        return new MemoryInfo();
    }

    private static CpuInfo ReadCpu()
    {
        var load = 0;
        TryWmi("SELECT LoadPercentage FROM Win32_Processor", obj =>
        {
            if (obj["LoadPercentage"] is not null)
            {
                load = Convert.ToInt32(obj["LoadPercentage"]);
            }
        });

        return new CpuInfo { LoadPercent = load, LogicalProcessors = Environment.ProcessorCount };
    }

    public static IReadOnlyList<DriveStatus> ReadDrives()
    {
        var drives = new List<DriveStatus>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            drives.Add(new DriveStatus
            {
                Name = drive.Name,
                Label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                Format = drive.DriveFormat,
                Type = drive.DriveType.ToString(),
                TotalBytes = drive.TotalSize,
                FreeBytes = drive.TotalFreeSpace
            });
        }

        return drives;
    }

    private static DefenderStatus ReadDefender()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var lastUpdate = obj["AntivirusSignatureLastUpdated"]?.ToString();
                var age = "Unknown";
                if (!string.IsNullOrWhiteSpace(lastUpdate))
                {
                    var parsed = ManagementDateTimeConverter.ToDateTime(lastUpdate);
                    var days = (DateTime.Now - parsed).TotalDays;
                    age = days < 1 ? "Updated today" : $"{(int)days} day(s) ago";
                }

                return new DefenderStatus
                {
                    Available = true,
                    AntivirusEnabled = Convert.ToBoolean(obj["AntivirusEnabled"] ?? false),
                    RealTimeProtection = Convert.ToBoolean(obj["RealTimeProtectionEnabled"] ?? false),
                    SignatureAge = age
                };
            }
        }
        catch
        {
            // Defender WMI is not always available
        }

        return new DefenderStatus();
    }

    private static IReadOnlyList<EventItem> ReadRecentErrors()
    {
        var items = new List<EventItem>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[(Level=2) and TimeCreated[timediff(@SystemTime) <= 259200000]]]");
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && items.Count < 12; record = reader.ReadEvent())
            {
                using (record)
                {
                    items.Add(new EventItem
                    {
                        Time = record.TimeCreated ?? DateTime.Now,
                        Source = record.ProviderName ?? "",
                        Level = "Error",
                        Message = Truncate(record.FormatDescription() ?? "No description", 240)
                    });
                }
            }
        }
        catch
        {
            // Event log access can fail without elevation
        }

        return items.OrderByDescending(i => i.Time).ToList();
    }

    private static IReadOnlyList<ServiceItem> ReadCriticalServices()
    {
        var list = new List<ServiceItem>();
        try
        {
            foreach (var name in WatchedServices)
            {
                try
                {
                    using var sc = new System.ServiceProcess.ServiceController(name);
                    list.Add(new ServiceItem
                    {
                        Name = sc.ServiceName,
                        DisplayName = sc.DisplayName,
                        Status = sc.Status.ToString(),
                        StartType = sc.StartType.ToString()
                    });
                }
                catch
                {
                    // service may not exist on this SKU
                }
            }
        }
        catch
        {
            // ignored
        }

        return list;
    }

    private static void TryWmi(string query, Action<ManagementObject> onObject)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                onObject(obj);
                break;
            }
        }
        catch
        {
            // ignored
        }
    }

    private static string FormatDmtf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        try
        {
            return ManagementDateTimeConverter.ToDateTime(value).ToString("yyyy-MM-dd");
        }
        catch
        {
            return value;
        }
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length] + "…";
}

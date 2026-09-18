using System.Diagnostics;
using System.Management;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class LiveMetricsService(ProcessService processes, EventLogService events) : IDisposable
{
    private readonly List<PerformanceCounter> _owned = [];
    private PerformanceCounter? _cpu;
    private PerformanceCounter? _cpuFreq;
    private PerformanceCounter? _diskQueue;
    private PerformanceCounter? _diskTime;
    private PerformanceCounter? _diskBytes;
    private PerformanceCounter? _processes;
    private PerformanceCounter? _threads;
    private PerformanceCounter? _handles;
    private List<PerformanceCounter> _netCounters = [];
    private List<PerformanceCounter> _gpuCounters = [];
    private List<(string Name, PerformanceCounter Counter)> _thermalCounters = [];
    private DateTime _lastGpuRefresh = DateTime.MinValue;
    private DateTime _lastThermalRefresh = DateTime.MinValue;
    private DateTime _lastEventRefresh = DateTime.MinValue;
    private EventItem? _lastEvent;
    private IReadOnlyList<ThermalZone> _thermalZones = [];
    private bool _ready;

    public LiveMetrics Sample()
    {
        EnsureCounters();
        var memory = HealthService.ReadMemory();
        int cpu = ReadInt(_cpu);
        int freq = ReadInt(_cpuFreq);
        int diskQ = ReadInt(_diskQueue);
        int diskPct = Math.Clamp(ReadInt(_diskTime), 0, 100);
        long diskBps = ReadLong(_diskBytes);
        long net = _netCounters.Sum(ReadLong);
        int gpu = SampleGpu();
        RefreshThermal();

        ProcessItem? top = null;
        try { top = processes.GetTopByCpu(); } catch { /* ignore */ }

        if ((DateTime.UtcNow - _lastEventRefresh).TotalSeconds >= 30)
        {
            try { _lastEvent = events.GetLastCritical(); } catch { /* ignore */ }
            _lastEventRefresh = DateTime.UtcNow;
        }

        var hottest = _thermalZones.OrderByDescending(z => z.Celsius).FirstOrDefault();
        return new LiveMetrics
        {
            CpuPercent = Math.Clamp(cpu, 0, 100),
            CpuFrequencyMhz = freq,
            MemoryPercent = memory.UsedPercent,
            MemoryUsedBytes = memory.UsedBytes,
            MemoryTotalBytes = memory.TotalBytes,
            DiskPercent = diskPct,
            DiskQueue = diskQ,
            DiskBytesPerSec = diskBps,
            NetworkBytesPerSec = net,
            GpuPercent = Math.Clamp(gpu, 0, 100),
            CpuTempC = hottest?.Celsius,
            TemperatureStatus = hottest is null
                ? "Windows did not expose a thermal sensor on this PC."
                : $"Hottest zone {hottest.Name} via {hottest.Source}",
            ThermalZones = _thermalZones,
            ProcessCount = ReadInt(_processes),
            ThreadCount = ReadInt(_threads),
            HandleCount = ReadInt(_handles),
            TopProcessName = top?.Name,
            TopProcessCpu = top?.CpuPercent ?? 0,
            LastCriticalEvent = _lastEvent
        };
    }

    private void EnsureCounters()
    {
        if (_ready)
        {
            return;
        }

        _cpu = Create("Processor", "% Processor Time", "_Total");
        _cpuFreq = Create("Processor Information", "Processor Frequency", "_Total")
                   ?? Create("Processor Information", "Processor Frequency", "0,0");
        _diskQueue = Create("PhysicalDisk", "Avg. Disk Queue Length", "_Total");
        _diskTime = Create("PhysicalDisk", "% Disk Time", "_Total");
        _diskBytes = Create("PhysicalDisk", "Disk Bytes/sec", "_Total");
        _processes = Create("System", "Processes", null);
        _threads = Create("System", "Threads", null);
        _handles = Create("Process", "Handle Count", "_Total");
        _netCounters = CreateAll("Network Interface", "Bytes Total/sec",
            n => !n.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                 && !n.Contains("isatap", StringComparison.OrdinalIgnoreCase));
        _cpu?.NextValue();
        _ready = true;
    }

    private int SampleGpu()
    {
        if ((DateTime.UtcNow - _lastGpuRefresh).TotalSeconds >= 10)
        {
            Replace(ref _gpuCounters, CreateAll("GPU Engine", "Utilization Percentage",
                n => n.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("engtype_Compute", StringComparison.OrdinalIgnoreCase)));
            _lastGpuRefresh = DateTime.UtcNow;
        }

        if (_gpuCounters.Count == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var counter in _gpuCounters)
        {
            sum += ReadDouble(counter);
        }

        return (int)Math.Round(Math.Min(100, sum));
    }

    private void RefreshThermal()
    {
        if ((DateTime.UtcNow - _lastThermalRefresh).TotalSeconds < 5 && _thermalZones.Count > 0)
        {
            return;
        }

        _lastThermalRefresh = DateTime.UtcNow;
        var zones = new List<ThermalZone>();

        if (_thermalCounters.Count == 0)
        {
            _thermalCounters = CreateNamed("Thermal Zone Information", "Temperature");
        }

        foreach (var (name, counter) in _thermalCounters)
        {
            var raw = ReadDouble(counter);
            var celsius = ToCelsius(raw, kelvinIfLarge: true);
            if (celsius is >= 0 and <= 125)
            {
                zones.Add(new ThermalZone { Name = name, Celsius = celsius, Source = "PDH" });
            }
        }

        if (zones.Count == 0)
        {
            zones.AddRange(ReadAcpiThermal());
        }

        _thermalZones = zones
            .GroupBy(z => z.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(z => z.Celsius)!)
            .OrderByDescending(z => z.Celsius)
            .Take(8)
            .ToList();
    }

    private static IReadOnlyList<ThermalZone> ReadAcpiThermal()
    {
        var list = new List<ThermalZone>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            searcher.Options.Timeout = TimeSpan.FromMilliseconds(800);
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var tenthsKelvin = Convert.ToDouble(obj["CurrentTemperature"]);
                var celsius = (int)Math.Round(tenthsKelvin / 10d - 273.15);
                if (celsius is >= 0 and <= 125)
                {
                    list.Add(new ThermalZone
                    {
                        Name = obj["InstanceName"]?.ToString() ?? "ACPI zone",
                        Celsius = celsius,
                        Source = "ACPI"
                    });
                }
            }
        }
        catch
        {
            // often missing without OEM ACPI thermal objects
        }

        return list;
    }

    private static int ToCelsius(double raw, bool kelvinIfLarge)
    {
        if (raw <= 0)
        {
            return 0;
        }

        if (kelvinIfLarge && raw > 200)
        {
            return (int)Math.Round(raw - 273.15);
        }

        return (int)Math.Round(raw);
    }

    private PerformanceCounter? Create(string category, string counter, string? instance)
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(category))
            {
                return null;
            }

            var created = instance is null
                ? new PerformanceCounter(category, counter, readOnly: true)
                : new PerformanceCounter(category, counter, instance, readOnly: true);
            created.NextValue();
            _owned.Add(created);
            return created;
        }
        catch
        {
            return null;
        }
    }

    private List<PerformanceCounter> CreateAll(string category, string counter, Func<string, bool> filter)
    {
        var list = new List<PerformanceCounter>();
        try
        {
            if (!PerformanceCounterCategory.Exists(category))
            {
                return list;
            }

            var cat = new PerformanceCounterCategory(category);
            foreach (var instance in cat.GetInstanceNames().Where(filter))
            {
                try
                {
                    var created = new PerformanceCounter(category, counter, instance, readOnly: true);
                    created.NextValue();
                    list.Add(created);
                    _owned.Add(created);
                }
                catch
                {
                    // skip instance
                }
            }
        }
        catch
        {
            // category missing
        }

        return list;
    }

    private List<(string Name, PerformanceCounter Counter)> CreateNamed(string category, string counter)
    {
        var list = new List<(string, PerformanceCounter)>();
        try
        {
            if (!PerformanceCounterCategory.Exists(category))
            {
                return list;
            }

            var cat = new PerformanceCounterCategory(category);
            foreach (var instance in cat.GetInstanceNames())
            {
                try
                {
                    var created = new PerformanceCounter(category, counter, instance, readOnly: true);
                    created.NextValue();
                    list.Add((instance, created));
                    _owned.Add(created);
                }
                catch
                {
                    // skip
                }
            }
        }
        catch
        {
            // ignore
        }

        return list;
    }

    private void Replace(ref List<PerformanceCounter> target, List<PerformanceCounter> next)
    {
        target = next;
    }

    private static int ReadInt(PerformanceCounter? counter) => (int)Math.Round(ReadDouble(counter));

    private static long ReadLong(PerformanceCounter? counter) => (long)Math.Round(ReadDouble(counter));

    private static double ReadDouble(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return 0;
        }

        try { return counter.NextValue(); }
        catch { return 0; }
    }

    public void Dispose()
    {
        foreach (var counter in _owned)
        {
            try { counter.Dispose(); } catch { /* ignore */ }
        }

        _owned.Clear();
    }
}

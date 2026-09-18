using System.Diagnostics;
using System.Management;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class ProcessService
{
    private readonly Dictionary<int, (TimeSpan Cpu, long Io, DateTime At)> _last = [];
    private readonly object _gate = new();
    private IReadOnlyList<ProcessItem> _latest = [];
    private DateTime _listedAt = DateTime.MinValue;

    public IReadOnlyList<ProcessItem> List()
    {
        var paths = new Dictionary<int, string>();
        var ioByPid = new Dictionary<int, long>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ExecutablePath, ReadTransferCount, WriteTransferCount FROM Win32_Process");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                var id = Convert.ToInt32(obj["ProcessId"]);
                var path = obj["ExecutablePath"]?.ToString();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    paths[id] = path;
                }

                try
                {
                    ioByPid[id] = Convert.ToInt64(obj["ReadTransferCount"] ?? 0) + Convert.ToInt64(obj["WriteTransferCount"] ?? 0);
                }
                catch
                {
                    // ignore
                }
            }
        }
        catch
        {
            // ignore
        }

        var now = DateTime.UtcNow;
        var seen = new HashSet<int>();
        var list = new List<ProcessItem>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                seen.Add(proc.Id);
                var cpu = TimeSpan.Zero;
                try { cpu = proc.TotalProcessorTime; } catch { /* access denied */ }
                var io = ioByPid.GetValueOrDefault(proc.Id);
                double cpuPercent = 0;
                long diskPerSec = 0;
                lock (_gate)
                {
                    if (_last.TryGetValue(proc.Id, out var prev))
                    {
                        var seconds = Math.Max(0.2, (now - prev.At).TotalSeconds);
                        cpuPercent = Math.Clamp(
                            (cpu - prev.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100d,
                            0,
                            100);
                        diskPerSec = (long)Math.Max(0, (io - prev.Io) / seconds);
                    }

                    _last[proc.Id] = (cpu, io, now);
                }

                list.Add(new ProcessItem
                {
                    Id = proc.Id,
                    Name = proc.ProcessName,
                    Path = paths.GetValueOrDefault(proc.Id, ""),
                    WorkingSetBytes = proc.WorkingSet64,
                    CpuSeconds = cpu.TotalSeconds,
                    CpuPercent = Math.Round(cpuPercent, 1),
                    DiskBytesPerSec = diskPerSec,
                    ThreadCount = SafeThreads(proc)
                });
            }
            catch
            {
                // access denied
            }
            finally
            {
                proc.Dispose();
            }
        }

        lock (_gate)
        {
            foreach (var stale in _last.Keys.Where(id => !seen.Contains(id)).ToList())
            {
                _last.Remove(stale);
            }
        }

        _latest = list
            .OrderByDescending(p => p.CpuPercent)
            .ThenByDescending(p => p.WorkingSetBytes)
            .Take(150)
            .ToList();
        _listedAt = DateTime.UtcNow;
        return _latest;
    }

    public OperationResult EndTask(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            proc.Kill(entireProcessTree: true);
            return OperationResult.Success($"Process {pid} ended.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public ProcessItem? GetTopByCpu()
    {
        if ((DateTime.UtcNow - _listedAt).TotalMilliseconds > 800)
        {
            List();
        }

        return _latest.FirstOrDefault();
    }

    private static int SafeThreads(Process proc)
    {
        try { return proc.Threads.Count; }
        catch { return 0; }
    }
}

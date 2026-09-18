using System.Runtime.InteropServices;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class StorageService(SearchIndexService searchIndex)
{
    public async Task<StorageOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var drives = HealthService.ReadDrives();
        var targets = BuildCleanupTargets();
        var status = await searchIndex.GetStatusAsync().WaitAsync(cancellationToken);
        var indexed = await searchIndex.GetLargestFilesAsync(50).WaitAsync(cancellationToken);
        var large = indexed.Select(f => new LargeFolder { Path = f.Path, SizeBytes = f.SizeBytes }).ToList();
        string? indexNotice = null;
        if (!status.Available)
        {
            indexNotice = "Windows Search index is unavailable. Largest files cannot be listed without crawling the disk.";
        }
        else if (large.Count == 0)
        {
            indexNotice = $"Indexer: {status.Status}. No indexed files returned yet.";
        }

        return new StorageOverview
        {
            Drives = drives,
            CleanupTargets = targets,
            LargeFolders = large,
            IndexStatus = indexNotice
        };
    }

    public IReadOnlyList<CleanupTarget> GetCleanupTargets() => BuildCleanupTargets();

    public OperationResult Clean(IReadOnlyCollection<string> targetIds, IProgress<JobProgress>? progress = null)
    {
        var all = BuildCleanupTargets().ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var cleaned = 0L;
        var done = 0;
        var errors = new List<string>();

        foreach (var id in targetIds)
        {
            if (!all.TryGetValue(id, out var target) || !target.CanClean)
            {
                continue;
            }

            progress?.Report(new JobProgress
            {
                Percent = (int)((done * 100d) / Math.Max(1, targetIds.Count)),
                Message = $"Cleaning {target.Name}…"
            });

            try
            {
                if (id == "recycle")
                {
                    SHEmptyRecycleBin(IntPtr.Zero, null, 0x00000001 | 0x00000002 | 0x00000004);
                    cleaned += target.SizeBytes;
                }
                else if (Directory.Exists(target.Path))
                {
                    cleaned += ClearDirectory(target.Path);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{target.Name}: {ex.Message}");
            }

            done++;
        }

        progress?.Report(new JobProgress { Percent = 100, Message = "Storage cleanup finished." });
        var message = $"Freed about {FormatBytes(cleaned)}.";
        if (errors.Count > 0)
        {
            message += " Some locations could not be fully cleaned: " + string.Join(" ", errors.Take(3));
        }

        return OperationResult.Success(message);
    }

    private static List<CleanupTarget> BuildCleanupTargets()
    {
        var userTemp = Path.GetTempPath();
        var windowsTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp");
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is var profile
            ? Path.Combine(profile, "Downloads")
            : "";
        var delivery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SoftwareDistribution", "Download");
        var prefetch = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

        return
        [
            new CleanupTarget
            {
                Id = "user-temp",
                Name = "User temporary files",
                Path = userTemp,
                Description = "Temporary files for the signed-in account.",
                SizeBytes = SafeFolderSize(userTemp),
                CanClean = true
            },
            new CleanupTarget
            {
                Id = "windows-temp",
                Name = "Windows temporary files",
                Path = windowsTemp,
                Description = "System temp folder used by Windows and installers.",
                SizeBytes = SafeFolderSize(windowsTemp),
                CanClean = true
            },
            new CleanupTarget
            {
                Id = "recycle",
                Name = "Recycle Bin",
                Path = "Recycle Bin",
                Description = "Deleted files that can still be restored.",
                SizeBytes = RecycleBinSize(),
                CanClean = true
            },
            new CleanupTarget
            {
                Id = "wu-download",
                Name = "Windows Update cache",
                Path = delivery,
                Description = "Downloaded update packages that can be fetched again later.",
                SizeBytes = SafeFolderSize(delivery),
                CanClean = true
            },
            new CleanupTarget
            {
                Id = "prefetch",
                Name = "Prefetch files",
                Path = prefetch,
                Description = "Windows launch cache. Safe to clear; Windows will rebuild it.",
                SizeBytes = SafeFolderSize(prefetch),
                CanClean = true
            },
            new CleanupTarget
            {
                Id = "downloads",
                Name = "Downloads folder",
                Path = downloads,
                Description = "Shown for awareness only. Not cleaned automatically.",
                SizeBytes = SafeFolderSize(downloads, maxDepth: 2),
                CanClean = false
            }
        ];
    }

    private static long ClearDirectory(string path)
    {
        long freed = 0;
        var directory = new DirectoryInfo(path);
        if (!directory.Exists)
        {
            return 0;
        }

        foreach (var file in directory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var size = file.Length;
                file.Delete();
                freed += size;
            }
            catch
            {
                // in use
            }
        }

        foreach (var sub in directory.EnumerateDirectories())
        {
            try
            {
                freed += ClearDirectory(sub.FullName);
                sub.Delete(recursive: false);
            }
            catch
            {
                try
                {
                    sub.Delete(recursive: true);
                }
                catch
                {
                    // still in use
                }
            }
        }

        return freed;
    }

    private static long SafeFolderSize(string path, int maxDepth = 4, int maxMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return 0;
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        return WalkSize(new DirectoryInfo(path), 0, maxDepth, deadline);
    }

    private static long WalkSize(DirectoryInfo directory, int depth, int maxDepth, DateTime deadline)
    {
        if (DateTime.UtcNow > deadline || depth > maxDepth)
        {
            return 0;
        }

        long size = 0;
        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                try { size += file.Length; } catch { /* skip */ }
            }

            if (depth == maxDepth)
            {
                return size;
            }

            foreach (var sub in directory.EnumerateDirectories())
            {
                size += WalkSize(sub, depth + 1, maxDepth, deadline);
            }
        }
        catch
        {
            // permissions
        }

        return size;
    }

    private static long RecycleBinSize()
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref info) == 0)
            {
                return info.i64Size;
            }
        }
        catch
        {
            // ignored
        }

        return 0;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }
}

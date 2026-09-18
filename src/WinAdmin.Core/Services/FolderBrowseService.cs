using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class FolderBrowseService
{
    public IReadOnlyList<FolderEntry> ListDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new FolderEntry
            {
                Name = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name : $"{d.Name} {d.VolumeLabel}",
                Path = d.RootDirectory.FullName,
                IsDirectory = true,
                SizeBytes = d.TotalSize,
                Modified = null
            })
            .ToList();
    }

    public IReadOnlyList<FolderEntry> List(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return [];
        }

        var list = new List<FolderEntry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).Take(400))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    list.Add(new FolderEntry
                    {
                        Name = info.Name,
                        Path = info.FullName,
                        IsDirectory = true,
                        Modified = info.LastWriteTime
                    });
                }
                catch
                {
                    // skip
                }
            }

            foreach (var file in Directory.EnumerateFiles(path).Take(400))
            {
                try
                {
                    var info = new FileInfo(file);
                    list.Add(new FolderEntry
                    {
                        Name = info.Name,
                        Path = info.FullName,
                        IsDirectory = false,
                        SizeBytes = info.Length,
                        Modified = info.LastWriteTime
                    });
                }
                catch
                {
                    // skip
                }
            }
        }
        catch
        {
            return [];
        }

        return list
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<FolderEntry> ListFolders(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return [];
        }

        var list = new List<FolderEntry>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(path).Take(500))
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    list.Add(new FolderEntry
                    {
                        Name = info.Name,
                        Path = info.FullName,
                        IsDirectory = true,
                        Modified = info.LastWriteTime
                    });
                }
                catch
                {
                    // skip
                }
            }
        }
        catch
        {
            return [];
        }

        return list
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? ParentOf(string path)
    {
        try
        {
            var parent = Directory.GetParent(path)?.FullName;
            return parent;
        }
        catch
        {
            return null;
        }
    }

    public void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}

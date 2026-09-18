using System.Text.Json;
using Microsoft.Win32;
using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public static class MachineSetup
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static MachineResetResult PrepareThisPc()
    {
        var guid = CurrentMachineGuid();
        var name = Environment.MachineName;
        var stamp = ReadStamp();
        if (stamp is not null && string.Equals(stamp.MachineGuid, guid, StringComparison.OrdinalIgnoreCase))
        {
            return new MachineResetResult
            {
                IsNewPc = false,
                Message = "This PC already has Windows Admin data."
            };
        }

        if (stamp is not null)
        {
            TryDelete(AppPaths.ProgramData);
            TryDelete(AppPaths.LocalData);
            Directory.CreateDirectory(AppPaths.ProgramData);
            WriteStamp(new MachineStamp
            {
                MachineGuid = guid,
                MachineName = name,
                StampAt = DateTime.UtcNow
            });
            return new MachineResetResult
            {
                IsNewPc = true,
                DataRemoved = true,
                Message = "This is a new PC. Previous Windows Admin records (locations, ML, cache) were removed."
            };
        }

        Directory.CreateDirectory(AppPaths.ProgramData);
        WriteStamp(new MachineStamp
        {
            MachineGuid = guid,
            MachineName = name,
            StampAt = DateTime.UtcNow
        });
        return new MachineResetResult
        {
            IsNewPc = true,
            DataRemoved = false,
            Message = "First run on this PC. Checking support libraries before the portal opens."
        };
    }

    private static MachineStamp? ReadStamp()
    {
        try
        {
            if (!File.Exists(AppPaths.MachineStamp))
            {
                return null;
            }

            return JsonSerializer.Deserialize<MachineStamp>(File.ReadAllText(AppPaths.MachineStamp), Json);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteStamp(MachineStamp stamp)
    {
        Directory.CreateDirectory(AppPaths.ProgramData);
        File.WriteAllText(AppPaths.MachineStamp, JsonSerializer.Serialize(stamp, Json));
    }

    private static string CurrentMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var value = key?.GetValue("MachineGuid")?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch
        {
            // fall through
        }

        return Environment.MachineName;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.Delete(file); } catch { /* in use */ }
                }
            }
            catch
            {
                // leftover files stay if locked
            }
        }
    }
}

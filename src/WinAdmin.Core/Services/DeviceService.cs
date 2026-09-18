using System.Management;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class DeviceService
{
    public IReadOnlyList<DeviceItem> ListProblemDevices()
    {
        var list = new List<DeviceItem>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID, ConfigManagerErrorCode, Status FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            foreach (var obj in searcher.Get().Cast<ManagementObject>())
            {
                list.Add(new DeviceItem
                {
                    Name = obj["Name"]?.ToString() ?? "Unknown device",
                    DeviceId = obj["DeviceID"]?.ToString() ?? "",
                    Status = obj["Status"]?.ToString() ?? "",
                    ProblemCode = Convert.ToInt32(obj["ConfigManagerErrorCode"] ?? 0)
                });
            }
        }
        catch
        {
            // ignore
        }

        return list.OrderBy(d => d.Name).ToList();
    }
}

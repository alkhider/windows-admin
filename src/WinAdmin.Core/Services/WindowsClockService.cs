using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32;
using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class WindowsClockService
{
    public ClockStatus GetStatus()
    {
        var zone = TimeZoneInfo.Local;
        var now = DateTime.Now;
        return new ClockStatus
        {
            LocalTime = now,
            UtcTime = DateTime.UtcNow,
            TimeZoneId = zone.Id,
            TimeZoneName = zone.DisplayName,
            IsDaylightSaving = zone.IsDaylightSavingTime(now)
        };
    }

    public IReadOnlyList<TimeZoneChoice> GetTimeZones() =>
        TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new TimeZoneChoice { Id = zone.Id, Name = zone.DisplayName })
            .ToList();

    public OperationResult SetDateTime(DateTime localTime)
    {
        try
        {
            PrivilegeEnabler.EnableAll();
            var value = ToSystemTime(localTime);
            if (!SetLocalTime(ref value))
            {
                return OperationResult.Fail(new Win32Exception(Marshal.GetLastWin32Error()).Message);
            }

            return OperationResult.Success($"Windows clock set to {localTime:yyyy-MM-dd HH:mm:ss}.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> SetTimeZoneAsync(string timeZoneId)
    {
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            return OperationResult.Fail("That time zone is not available on this PC.");
        }

        var result = await HiddenProcess.RunAsync("tzutil.exe", $"/s \"{timeZoneId.Replace("\"", "")}\"");
        return result.ExitCode == 0
            ? OperationResult.Success($"Time zone set to {timeZoneId}.")
            : OperationResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? "Could not change the time zone." : result.Error.Trim());
    }

    public async Task<OperationResult> SyncFromInternetAsync()
    {
        try
        {
            using var timeService = new ServiceController("W32Time");
            if (timeService.Status != ServiceControllerStatus.Running)
            {
                timeService.Start();
                timeService.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            }
        }
        catch
        {
            // continue; resync may still work
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\W32Time\Parameters", writable: true);
            key?.SetValue("Type", "NTP", RegistryValueKind.String);
        }
        catch
        {
            // ignored
        }

        await HiddenProcess.RunAsync(
            "w32tm.exe",
            "/config /syncfromflags:manual /manualpeerlist:\"time.windows.com,0x9\" /update");
        var result = await HiddenProcess.RunAsync("w32tm.exe", "/resync /force /nowait");
        return result.ExitCode == 0
            ? OperationResult.Success("Windows is syncing date and time from the internet.")
            : OperationResult.Fail(string.IsNullOrWhiteSpace(result.Error) ? "Internet time sync could not start." : result.Error.Trim());
    }

    private static SYSTEMTIME ToSystemTime(DateTime value) => new()
    {
        Year = (ushort)value.Year,
        Month = (ushort)value.Month,
        Day = (ushort)value.Day,
        Hour = (ushort)value.Hour,
        Minute = (ushort)value.Minute,
        Second = (ushort)value.Second,
        Milliseconds = (ushort)value.Millisecond
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetLocalTime(ref SYSTEMTIME lpSystemTime);
}

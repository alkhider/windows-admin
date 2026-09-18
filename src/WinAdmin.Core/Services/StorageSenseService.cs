using Microsoft.Win32;
using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class StorageSenseService
{
    private const string PolicyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";

    public StorageSenseSettings GetSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PolicyPath);
            if (key is null)
            {
                return new StorageSenseSettings { Enabled = true, RecycleDays = 30, DownloadsDays = 0 };
            }

            return new StorageSenseSettings
            {
                Enabled = Convert.ToInt32(key.GetValue("01") ?? 1) == 1,
                RecycleDays = Convert.ToInt32(key.GetValue("04") ?? 30),
                DownloadsDays = Convert.ToInt32(key.GetValue("08") ?? 0)
            };
        }
        catch
        {
            return new StorageSenseSettings();
        }
    }

    public OperationResult SaveSettings(StorageSenseSettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PolicyPath, true);
            if (key is null)
            {
                return OperationResult.Fail("Could not open Storage Sense policy.");
            }

            key.SetValue("01", settings.Enabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("04", settings.RecycleDays, RegistryValueKind.DWord);
            key.SetValue("08", settings.DownloadsDays, RegistryValueKind.DWord);
            return OperationResult.Success("Storage Sense policy saved.");
        }
        catch (Exception ex)
        {
            return OperationResult.Fail(ex.Message);
        }
    }

    public async Task<OperationResult> RunCleanupAsync()
    {
        var result = await HiddenProcess.RunAsync(
            "schtasks.exe",
            "/Run /TN \"\\Microsoft\\Windows\\DiskFootprint\\StorageSense\"",
            timeoutMs: 60_000);
        if (result.ExitCode == 0)
        {
            return OperationResult.Success("Storage Sense cleanup started.");
        }

        var fallback = await HiddenProcess.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Start-ScheduledTask -TaskName 'StorageSense' -TaskPath '\\Microsoft\\Windows\\DiskFootprint\\' -ErrorAction SilentlyContinue\"",
            timeoutMs: 60_000);
        return fallback.ExitCode == 0
            ? OperationResult.Success("Storage Sense cleanup started.")
            : OperationResult.Fail("Could not start Storage Sense. Check that the task exists on this PC.");
    }
}

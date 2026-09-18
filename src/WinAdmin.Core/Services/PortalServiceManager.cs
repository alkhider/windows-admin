using System.ServiceProcess;
using WinAdmin.Core.Infrastructure;

namespace WinAdmin.Core.Services;

public static class PortalServiceManager
{
    public const string ServiceName = "WinAdminPortal";
    public const string Url = "http://127.0.0.1:5077";

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinAdmin", "portal");

    public static string InstalledExe => Path.Combine(InstallDirectory, "WinAdmin.Web.exe");

    public static bool IsInstalled()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            _ = service.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsRunning()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            return service.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> TryStartAsync()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            if (service.Status == ServiceControllerStatus.Running)
            {
                return true;
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            return service.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            var result = await HiddenProcess.RunAsync("sc.exe", $"start {ServiceName}");
            return result.ExitCode == 0 || IsRunning();
        }
    }

    public static async Task<bool> TryRegisterCurrentHostAsync()
    {
        var source = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(source, "WinAdmin.Web.exe")))
        {
            return false;
        }

        return await TryInstallAsync(source, startNow: false);
    }

    public static async Task<bool> TryInstallAsync(string sourceDirectory, bool startNow)
    {
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(sourceDirectory, file);
                var destination = Path.Combine(InstallDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                try
                {
                    File.Copy(file, destination, overwrite: true);
                }
                catch
                {
                    // file may be in use
                }
            }

            if (!IsInstalled())
            {
                var create = await HiddenProcess.RunAsync(
                    "sc.exe",
                    $"create {ServiceName} binPath= {InstalledExe} start= auto DisplayName= \"Windows Admin Portal\"");
                if (create.ExitCode != 0 && !IsInstalled())
                {
                    return false;
                }
            }

            await HiddenProcess.RunAsync("sc.exe", $"description {ServiceName} \"Windows Admin local portal. Runs as the system so tasks do not ask for permission.\"");
            await HiddenProcess.RunAsync("sc.exe", $"config {ServiceName} obj= LocalSystem start= auto");
            await HiddenProcess.RunAsync("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/3000/restart/5000/restart/5000");
            await HiddenProcess.RunAsync(
                "sc.exe",
                $"sdset {ServiceName} D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPLOCRRC;;;IU)(A;;RPWP;;;AU)");

            if (startNow)
            {
                return await TryStartAsync();
            }

            return IsInstalled();
        }
        catch
        {
            return false;
        }
    }
}

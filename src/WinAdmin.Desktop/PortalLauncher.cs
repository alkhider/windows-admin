using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using WinAdmin.Core.Services;

namespace WinAdmin.Desktop;

internal static class PortalLauncher
{
    public const string Url = PortalServiceManager.Url;
    private static Process? _owned;

    public static async Task<string> EnsureStartedAsync()
    {
        var exe = FindWebExecutable();
        var sidecar = SidecarExe();
        var preferSidecar = File.Exists(sidecar);

        if (preferSidecar)
        {
            if (!IsHostedBy(sidecar) && await IsReadyAsync())
            {
                StopWebHosts();
            }

            if (!await IsReadyAsync())
            {
                _owned = StartHidden(sidecar);
                if (!await WaitUntilReadyAsync(TimeSpan.FromSeconds(30)))
                {
                    throw new InvalidOperationException("Timed out waiting for http://127.0.0.1:5077");
                }
            }

            return Url;
        }

        if (await IsReadyAsync())
        {
            return Url;
        }

        if (PortalServiceManager.IsInstalled())
        {
            await PortalServiceManager.TryStartAsync();
            if (await WaitUntilReadyAsync(TimeSpan.FromSeconds(20)))
            {
                return Url;
            }
        }

        await PortalServiceManager.TryInstallAsync(Path.GetDirectoryName(exe)!, startNow: false);
        if (await PortalServiceManager.TryStartAsync() && await WaitUntilReadyAsync(TimeSpan.FromSeconds(20)))
        {
            return Url;
        }

        _owned = StartHidden(exe);
        if (await WaitUntilReadyAsync(TimeSpan.FromSeconds(30)))
        {
            return Url;
        }

        throw new InvalidOperationException("Timed out waiting for http://127.0.0.1:5077. Close other Windows Admin windows and try again.");
    }

    public static void StopOwnedHost()
    {
        try
        {
            if (_owned is { HasExited: false })
            {
                _owned.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static Process StartHidden(string exe)
    {
        var start = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ErrorDialog = false
        };
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["WINADMIN_URLS"] = Url;
        start.Environment["__COMPAT_LAYER"] = "RunAsInvoker";

        return Process.Start(start)
               ?? throw new InvalidOperationException("The web portal process did not start.");
    }

    private static void StopWebHosts()
    {
        foreach (var process in Process.GetProcessesByName("WinAdmin.Web"))
        {
            try
            {
                if (_owned is not null && process.Id == _owned.Id)
                {
                    continue;
                }

                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static bool IsHostedBy(string exe)
    {
        var expected = Path.GetFullPath(exe);
        foreach (var process in Process.GetProcessesByName("WinAdmin.Web"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path)
                    && string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // access denied on MainModule
            }
        }

        return false;
    }

    private static async Task<bool> WaitUntilReadyAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await IsReadyAsync())
            {
                return true;
            }

            await Task.Delay(400);
        }

        return false;
    }

    private static async Task<bool> IsReadyAsync()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", 5077);
            var completed = await Task.WhenAny(connect, Task.Delay(400));
            return completed == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string SidecarExe() =>
        Path.Combine(AppContext.BaseDirectory, "portal", "WinAdmin.Web.exe");

    private static string FindWebExecutable()
    {
        var candidates = new[]
        {
            SidecarExe(),
            Path.Combine(AppContext.BaseDirectory, "WinAdmin.Web.exe"),
            PortalServiceManager.InstalledExe,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WinAdmin.Web", "bin", "Debug", "net9.0-windows", "WinAdmin.Web.exe")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "WinAdmin.Web", "bin", "Release", "net9.0-windows", "WinAdmin.Web.exe"))
        };

        return candidates.FirstOrDefault(File.Exists)
               ?? throw new FileNotFoundException("WinAdmin.Web.exe was not found. Publish with scripts\\publish-exe.ps1 first.");
    }
}

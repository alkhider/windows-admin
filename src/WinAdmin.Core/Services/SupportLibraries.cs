using System.Diagnostics;
using System.ServiceProcess;
using Microsoft.Win32;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public static class SupportLibraries
{
    private const string FxMajor = "9.";
    private const string WebView2Guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    public static IReadOnlyList<SupportLibrary> Check(string? webView2Version = null)
    {
        var runtimes = ListDotnetRuntimes();
        var desktop = FindFramework("Microsoft.WindowsDesktop.App", runtimes, runningNow: Environment.Version.Major >= 9);
        var aspnet = FindFramework("Microsoft.AspNetCore.App", runtimes, runningNow: false);
        var webview = FindWebView2(webView2Version);
        var vc = FindVcRedist();
        var winget = FindWinget();
        var search = FindWindowsSearch();

        return
        [
            Lib("dotnet-desktop", ".NET 9 Desktop Runtime", "Runs the Windows Admin window.", true,
                desktop.Found, desktop.Detail, "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe"),
            Lib("dotnet-aspnet", ".NET 9 ASP.NET Core Runtime", "Runs the local portal (WinAdmin.Web).", true,
                aspnet.Found, aspnet.Detail, "https://aka.ms/dotnet/9.0/aspnetcore-runtime-win-x64.exe"),
            Lib("webview2", "Microsoft Edge WebView2", "Draws the app window. Current Windows 11 usually has this.", true,
                webview.Found, webview.Detail, "https://go.microsoft.com/fwlink/p/?LinkId=2124703"),
            Lib("vcredist", "Visual C++ Redistributable (x64)", "Native support library used by .NET and WebView2.", false,
                vc.Found, vc.Detail, "https://aka.ms/vs/17/release/vc_redist.x64.exe"),
            Lib("winget", "winget (App Installer)", "Needed to install apps from the Applications page.", false,
                winget.Found, winget.Detail, "https://aka.ms/getwinget"),
            Lib("search", "Windows Search index", "Needed for Search files and Explorer index search.", false,
                search.Found, search.Detail, "ms-settings:cortana-windowssearch")
        ];
    }

    private static SupportLibrary Lib(string id, string name, string why, bool required, bool installed, string detail, string url) =>
        new()
        {
            Id = id,
            Name = name,
            Why = why,
            Required = required,
            Installed = installed,
            Detail = detail,
            DownloadUrl = url
        };

    private static (bool Found, string Detail) FindFramework(string name, IReadOnlyList<RuntimeRow> runtimes, bool runningNow)
    {
        var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in runtimes.Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && IsMajor9(r.Version)))
        {
            versions.Add(row.Version);
        }

        foreach (var shared in DotnetSharedRoots())
        {
            var dir = Path.Combine(shared, name);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            try
            {
                foreach (var folder in Directory.EnumerateDirectories(dir))
                {
                    var version = Path.GetFileName(folder);
                    if (IsMajor9(version) && File.Exists(Path.Combine(folder, name + ".dll")))
                    {
                        versions.Add(version);
                    }
                }
            }
            catch
            {
                // skip unreadable shared folders
            }
        }

        foreach (var version in RegistryFrameworks(name))
        {
            if (IsMajor9(version))
            {
                versions.Add(version);
            }
        }

        if (versions.Count > 0)
        {
            var best = versions
                .Select(v =>
                {
                    Version.TryParse(NormalizeVersion(v), out var parsed);
                    return (Label: v, Parsed: parsed ?? new Version(0, 0));
                })
                .OrderByDescending(t => t.Parsed)
                .First().Label;
            return (true, best);
        }

        if (runningNow)
        {
            return (true, Environment.Version.ToString(3));
        }

        if (HasBundledFramework(name))
        {
            return (true, "included with this app");
        }

        return (false, "not in Program Files\\dotnet");
    }

    private static (bool Found, string Detail) FindWebView2(string? hostVersion)
    {
        if (!string.IsNullOrWhiteSpace(hostVersion) && hostVersion != "0.0.0.0")
        {
            return (true, hostVersion.Trim());
        }

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var path in new[]
                         {
                             $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WebView2Guid}",
                             $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WebView2Guid}",
                             $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\ClientState\{WebView2Guid}",
                             $@"SOFTWARE\Microsoft\EdgeUpdate\ClientState\{WebView2Guid}"
                         })
                {
                    var pv = ReadPv(hive, view, path);
                    if (!string.IsNullOrWhiteSpace(pv))
                    {
                        return (true, pv);
                    }
                }
            }
        }

        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "EdgeWebView", "Application"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "EdgeWebView", "Application"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "EdgeWebView", "Application")
                 })
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var exe = Path.Combine(dir, "msedgewebview2.exe");
                    if (File.Exists(exe))
                    {
                        return (true, Path.GetFileName(dir));
                    }
                }
            }
            catch
            {
                // skip
            }
        }

        return (false, "runtime not registered");
    }

    private static (bool Found, string Detail) FindVcRedist()
    {
        var system = Environment.SystemDirectory;
        var main = Path.Combine(system, "vcruntime140.dll");
        var extra = Path.Combine(system, "vcruntime140_1.dll");
        if (File.Exists(main))
        {
            return (true, File.Exists(extra) ? "vcruntime140" : "vcruntime140 (legacy)");
        }

        return (false, "vcruntime140.dll missing");
    }

    private static (bool Found, string Detail) FindWinget()
    {
        var localApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps");
        var candidates = new[]
        {
            Path.Combine(localApps, "winget.exe"),
            Path.Combine(localApps, "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "winget.exe")
        };
        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return (true, "App Installer");
            }
        }

        var onPath = FindOnPath("winget.exe");
        if (!string.IsNullOrWhiteSpace(onPath))
        {
            return (true, "on PATH");
        }

        try
        {
            var where = Path.Combine(Environment.SystemDirectory, "where.exe");
            if (File.Exists(where))
            {
                var output = RunCapture(where, "winget.exe", 4000);
                var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(p => p.EndsWith("winget.exe", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(line) && (File.Exists(line) || line.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)))
                {
                    return (true, "App Installer");
                }
            }
        }
        catch
        {
            // skip
        }

        return (false, "App Installer not found");
    }

    private static (bool Found, string Detail) FindWindowsSearch()
    {
        var provider = false;
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cls = hklm.OpenSubKey(@"SOFTWARE\Classes\Search.CollatorDSO");
            provider = cls is not null;
            using var setup = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows Search");
            if (ToInt(setup?.GetValue("SetupCompletedSuccessfully")) == 1)
            {
                provider = true;
            }
        }
        catch
        {
            // skip
        }

        try
        {
            if (Type.GetTypeFromProgID("Search.CollatorDSO") is not null ||
                Type.GetTypeFromProgID("Search.CollatorDSO.1") is not null)
            {
                provider = true;
            }
        }
        catch
        {
            // skip
        }

        try
        {
            using var service = new ServiceController("WSearch");
            var status = service.Status.ToString().ToLowerInvariant();
            if (provider || service.Status != ServiceControllerStatus.Stopped)
            {
                return (true, "service " + status);
            }

            return (true, "installed · service " + status);
        }
        catch
        {
            // service missing
        }

        return provider
            ? (true, "provider registered")
            : (false, "Windows Search not registered");
    }

    private static bool HasBundledFramework(string name)
    {
        var baseDir = AppContext.BaseDirectory;
        var portal = Path.Combine(baseDir, "portal");
        if (name.Equals("Microsoft.WindowsDesktop.App", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(Path.Combine(baseDir, "Microsoft.WindowsDesktop.App.dll"))
                   || File.Exists(Path.Combine(baseDir, "coreclr.dll"));
        }

        if (name.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(Path.Combine(portal, "Microsoft.AspNetCore.dll"))
                   || File.Exists(Path.Combine(portal, "Microsoft.AspNetCore.App.dll"))
                   || File.Exists(Path.Combine(baseDir, "Microsoft.AspNetCore.dll"));
        }

        return false;
    }

    private static bool IsMajor9(string version) =>
        version.StartsWith(FxMajor, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string version)
    {
        var core = version.Split('-')[0];
        var parts = core.Split('.');
        return parts.Length >= 3 ? core : core + ".0";
    }

    private static int ToInt(object? value) =>
        value is null ? 0 : Convert.ToInt32(value);

    private static IEnumerable<string> DotnetSharedRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                roots.Add(Path.GetFullPath(path));
            }
            catch
            {
                roots.Add(path);
            }
        }

        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared"));
        foreach (var key in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                Add(Path.Combine(value, "shared"));
            }
        }

        Add(Path.Combine(AppContext.BaseDirectory, "shared"));
        Add(Path.Combine(AppContext.BaseDirectory, "dotnet", "shared"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RuntimeRow> ListDotnetRuntimes()
    {
        var rows = new List<RuntimeRow>();
        foreach (var exe in DotnetHosts())
        {
            var output = RunCapture(exe, "--list-runtimes", 5000);
            foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    rows.Add(new RuntimeRow(parts[0], parts[1]));
                }
            }

            if (rows.Count > 0)
            {
                break;
            }
        }

        return rows;
    }

    private static IEnumerable<string> DotnetHosts()
    {
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(pf))
        {
            yield return pf;
        }

        foreach (var key in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64" })
        {
            var root = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            var exe = Path.Combine(root, "dotnet.exe");
            if (File.Exists(exe))
            {
                yield return exe;
            }
        }
    }

    private static IEnumerable<string> RegistryFrameworks(string name)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            IEnumerable<string> Read(string path)
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(path);
                if (key is null)
                {
                    return [];
                }

                return key.GetValueNames();
            }

            foreach (var version in Read($@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\{name}"))
            {
                yield return version;
            }

            foreach (var version in Read($@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\{name}"))
            {
                yield return version;
            }
        }
    }

    private static string? ReadPv(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path);
            var pv = key?.GetValue("pv")?.ToString();
            return string.IsNullOrWhiteSpace(pv) || pv == "0.0.0.0" ? null : pv;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var folder in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // skip
            }
        }

        return null;
    }

    private static string RunCapture(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return "";
            }

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return "";
            }

            return process.StandardOutput.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    private sealed record RuntimeRow(string Name, string Version);
}

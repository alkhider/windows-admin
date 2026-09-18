using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class AppService
{
    public static readonly IReadOnlyList<CatalogApp> SuggestedApps =
    [
        new() { Name = "Google Chrome", Id = "Google.Chrome", Source = "winget" },
        new() { Name = "Mozilla Firefox", Id = "Mozilla.Firefox", Source = "winget" },
        new() { Name = "7-Zip", Id = "7zip.7zip", Source = "winget" },
        new() { Name = "VLC media player", Id = "VideoLAN.VLC", Source = "winget" },
        new() { Name = "Notepad++", Id = "Notepad++.Notepad++", Source = "winget" },
        new() { Name = "Visual Studio Code", Id = "Microsoft.VisualStudioCode", Source = "winget" },
        new() { Name = "Git", Id = "Git.Git", Source = "winget" },
        new() { Name = "PowerToys", Id = "Microsoft.PowerToys", Source = "winget" }
    ];

    public IReadOnlyList<InstalledApp> GetInstalled()
    {
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
        ReadUninstallKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        ReadUninstallKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        ReadUninstallKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", apps);
        return apps.Values.OrderBy(a => a.Name).ToList();
    }

    public async Task<IReadOnlyList<CatalogApp>> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SuggestedApps;
        }

        var winget = ResolveWinget();
        var result = await HiddenProcess.RunAsync(
            winget,
            $"search \"{Sanitize(query)}\" --disable-interactivity --accept-source-agreements --silent",
            cancellationToken);

        return ParseCatalog(result.Output);
    }

    public async Task<OperationResult> InstallAsync(string packageId, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new JobProgress { Percent = 10, Message = $"Installing {packageId} silently…" });
        var winget = ResolveWinget();
        var result = await HiddenProcess.RunAsync(
            winget,
            $"install --id \"{Sanitize(packageId)}\" --silent --accept-package-agreements --accept-source-agreements --disable-interactivity --force",
            cancellationToken,
            timeoutMs: 600_000);

        progress?.Report(new JobProgress { Percent = 100, Message = "Finished." });
        return Succeeded(result)
            ? OperationResult.Success($"{packageId} was installed.")
            : OperationResult.Fail(Describe(result, "Install failed."));
    }

    public async Task<OperationResult> UninstallAsync(InstalledApp app, IProgress<JobProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new JobProgress { Percent = 8, Message = $"Removing {app.Name} silently…" });

        if (app.IsMsi && !string.IsNullOrWhiteSpace(app.ProductCode))
        {
            progress?.Report(new JobProgress { Percent = 25, Message = "Running silent MSI uninstall…" });
            var msi = await HiddenProcess.RunAsync(
                MsiexecPath(),
                $"/x {app.ProductCode} /qn /norestart REBOOT=ReallySuppress",
                cancellationToken,
                timeoutMs: 600_000);
            if (Succeeded(msi))
            {
                return OperationResult.Success($"{app.Name} was removed.");
            }
        }

        if (!string.IsNullOrWhiteSpace(app.UninstallString))
        {
            progress?.Report(new JobProgress { Percent = 45, Message = "Running silent uninstaller…" });
            var (fileName, arguments) = SplitCommand(app.UninstallString);
            arguments = EnsureSilentArguments(fileName, arguments, app);
            var local = await HiddenProcess.RunAsync(fileName, arguments, cancellationToken, timeoutMs: 600_000);
            if (Succeeded(local))
            {
                return OperationResult.Success($"{app.Name} was removed.");
            }
        }

        progress?.Report(new JobProgress { Percent = 70, Message = "Trying winget silent uninstall…" });
        var winget = await HiddenProcess.RunAsync(
            ResolveWinget(),
            $"uninstall --name \"{Sanitize(app.Name)}\" --exact --silent --disable-interactivity --force --accept-source-agreements",
            cancellationToken,
            timeoutMs: 600_000);
        if (Succeeded(winget))
        {
            return OperationResult.Success($"{app.Name} was removed.");
        }

        progress?.Report(new JobProgress { Percent = 100, Message = "Could not remove the app silently." });
        return OperationResult.Fail($"Could not remove {app.Name} silently. No permission prompt was shown.");
    }

    private static void ReadUninstallKey(RegistryKey root, string path, IDictionary<string, InstalledApp> apps)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null)
            {
                return;
            }

            foreach (var subName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subName);
                if (sub is null)
                {
                    continue;
                }

                var name = sub.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (sub.GetValue("SystemComponent") is int system && system == 1)
                {
                    continue;
                }

                var releaseType = sub.GetValue("ReleaseType") as string;
                if (!string.IsNullOrWhiteSpace(releaseType) &&
                    releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var quiet = sub.GetValue("QuietUninstallString") as string;
                var uninstall = quiet ?? sub.GetValue("UninstallString") as string ?? "";
                var productCode = Guid.TryParse(subName.Trim('{', '}'), out _)
                    ? (subName.StartsWith('{') ? subName : "{" + subName + "}")
                    : "";
                var isMsi = Convert.ToInt32(sub.GetValue("WindowsInstaller") ?? 0) == 1 || !string.IsNullOrWhiteSpace(productCode);

                apps[name] = new InstalledApp
                {
                    Name = name,
                    Version = sub.GetValue("DisplayVersion") as string ?? "",
                    Publisher = sub.GetValue("Publisher") as string ?? "",
                    UninstallString = uninstall,
                    ProductCode = productCode,
                    InstallLocation = sub.GetValue("InstallLocation") as string ?? "",
                    QuietUninstall = !string.IsNullOrWhiteSpace(quiet),
                    IsMsi = isMsi
                };
            }
        }
        catch
        {
            // ignored
        }
    }

    private static IReadOnlyList<CatalogApp> ParseCatalog(string output)
    {
        var lines = output.Replace("\r", "").Split('\n')
            .Select(l => l.TrimEnd())
            .Where(l => l.Length > 0)
            .ToList();
        var header = lines.FirstOrDefault(l => l.StartsWith("Name", StringComparison.OrdinalIgnoreCase) && l.Contains("Id"));
        if (header is null)
        {
            return [];
        }

        var idIndex = header.IndexOf("Id", StringComparison.Ordinal);
        var versionIndex = header.IndexOf("Version", StringComparison.Ordinal);
        var sourceIndex = header.IndexOf("Source", StringComparison.Ordinal);
        if (idIndex < 0 || versionIndex < 0)
        {
            return [];
        }

        var apps = new List<CatalogApp>();
        foreach (var line in lines.Skip(lines.IndexOf(header) + 1))
        {
            if (line.All(c => c is '-' or ' '))
            {
                continue;
            }

            if (line.Length < versionIndex)
            {
                continue;
            }

            string Slice(int start, int end) =>
                line[Math.Min(start, line.Length)..Math.Min(end < 0 ? line.Length : end, line.Length)].Trim();

            apps.Add(new CatalogApp
            {
                Name = Slice(0, idIndex),
                Id = Slice(idIndex, versionIndex),
                Version = Slice(versionIndex, sourceIndex),
                Source = sourceIndex < 0 ? "" : Slice(sourceIndex, -1)
            });
        }

        return apps.Where(a => !string.IsNullOrWhiteSpace(a.Id)).Take(40).ToList();
    }

    private static string EnsureSilentArguments(string fileName, string arguments, InstalledApp app)
    {
        arguments ??= "";
        var blob = $"{fileName} {arguments}";

        if (fileName.Contains("msiexec", StringComparison.OrdinalIgnoreCase) || app.IsMsi)
        {
            arguments = Regex.Replace(arguments, @"/I\s*", "/X", RegexOptions.IgnoreCase);
            if (!Regex.IsMatch(arguments, @"/x", RegexOptions.IgnoreCase) && !string.IsNullOrWhiteSpace(app.ProductCode))
            {
                arguments = $"/x {app.ProductCode}";
            }

            if (!Regex.IsMatch(arguments, @"/q", RegexOptions.IgnoreCase))
            {
                arguments += " /qn";
            }

            if (!arguments.Contains("norestart", StringComparison.OrdinalIgnoreCase))
            {
                arguments += " /norestart REBOOT=ReallySuppress";
            }

            return arguments.Trim();
        }

        if (blob.Contains("adobe", StringComparison.OrdinalIgnoreCase))
        {
            if (!blob.Contains("mode=silent", StringComparison.OrdinalIgnoreCase))
            {
                arguments += " --mode=silent";
            }

            if (!blob.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            {
                arguments += " --uninstall";
            }

            return arguments.Trim();
        }

        if (Regex.IsMatch(arguments, @"(/S\b|/silent|/quiet|/qn|--silent|/VERYSILENT)", RegexOptions.IgnoreCase))
        {
            return arguments.Trim();
        }

        if (fileName.Contains("unins", StringComparison.OrdinalIgnoreCase))
        {
            return (arguments + " /VERYSILENT /SUPPRESSMSGBOXES /NORESTART").Trim();
        }

        return (arguments + " /S /silent /quiet").Trim();
    }

    private static string ResolveWinget()
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(local))
        {
            return local;
        }

        try
        {
            var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var found = Directory.GetFiles(windowsApps, "winget.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(found))
            {
                return found;
            }
        }
        catch
        {
            // SYSTEM may not enumerate WindowsApps
        }

        return "winget.exe";
    }

    private static string MsiexecPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");

    private static string Sanitize(string value) => value.Replace("\"", "");

    private static bool Succeeded((int ExitCode, string Output, string Error) result) =>
        result.ExitCode is 0 or 1641 or 3010;

    private static string Describe((int ExitCode, string Output, string Error) result, string fallback)
    {
        var detail = string.Join("\n", new[] { result.Output, result.Error }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
        return string.IsNullOrWhiteSpace(detail) ? $"{fallback} Exit code {result.ExitCode}." : detail;
    }

    internal static (string FileName, string Arguments) SplitCommand(string command)
    {
        command = command.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            return ("", "");
        }

        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            if (end > 1)
            {
                return (command[1..end], command[(end + 1)..].Trim());
            }
        }

        var match = Regex.Match(command, @"^(?<file>.+?\.(?:exe|msi|bat|cmd|com))(?<args>.*)$", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups["file"].Value.Trim().Trim('"'), match.Groups["args"].Value.Trim());
        }

        var space = command.IndexOf(' ');
        return space < 0 ? (command, "") : (command[..space], command[(space + 1)..].Trim());
    }
}

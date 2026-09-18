using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class ScheduledTaskService
{
    public async Task<IReadOnlyList<ScheduledTaskItem>> ListAsync()
    {
        var result = await HiddenProcess.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-ScheduledTask | Select-Object -First 150 TaskName, TaskPath, State, @{N='Trigger';E={($_.Triggers | Select-Object -First 1).CimClass.CimClassName}} | ConvertTo-Csv -NoTypeInformation\"",
            timeoutMs: 90_000);
        if (result.ExitCode != 0)
        {
            return [];
        }

        var list = new List<ScheduledTaskItem>();
        foreach (var line in result.Output.Replace("\r", "").Split('\n').Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var parts = ParseCsvLine(line);
            if (parts.Count < 3)
            {
                continue;
            }

            var taskPath = parts[1].Trim('"');
            var full = string.IsNullOrWhiteSpace(taskPath) ? parts[0].Trim('"') : $"{taskPath.TrimEnd('\\')}\\{parts[0].Trim('"')}";
            list.Add(new ScheduledTaskItem
            {
                Name = parts[0].Trim('"'),
                Path = full,
                State = parts.Count > 2 ? parts[2].Trim('"') : "",
                Trigger = parts.Count > 3 ? parts[3].Trim('"') : ""
            });
        }

        return list.OrderBy(t => t.Name).ToList();
    }

    public async Task<OperationResult> RunAsync(string taskPath)
    {
        var result = await HiddenProcess.RunAsync("schtasks.exe", $"/Run /TN \"{taskPath.Replace("\"", "")}\"");
        return result.ExitCode == 0
            ? OperationResult.Success($"Task {taskPath} started.")
            : OperationResult.Fail(result.Error.Trim().Length > 0 ? result.Error.Trim() : "Could not run task.");
    }

    public async Task<OperationResult> EnableAsync(string taskPath, bool enable)
    {
        var flag = enable ? "/Enable" : "/Disable";
        var result = await HiddenProcess.RunAsync("schtasks.exe", $"{flag} /TN \"{taskPath.Replace("\"", "")}\"");
        return result.ExitCode == 0
            ? OperationResult.Success($"Task {taskPath} {(enable ? "enabled" : "disabled")}.")
            : OperationResult.Fail("Could not change task state.");
    }

    private static List<string> ParseCsvLine(string line)
    {
        var parts = new List<string>();
        var current = "";
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current += c;
            }
            else if (c == ',' && !inQuotes)
            {
                parts.Add(current);
                current = "";
            }
            else
            {
                current += c;
            }
        }

        parts.Add(current);
        return parts;
    }
}

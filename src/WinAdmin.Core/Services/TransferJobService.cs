using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class TransferJobService
{
    public async Task<IReadOnlyList<TransferJobItem>> ListBitsJobsAsync()
    {
        var result = await HiddenProcess.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-BitsTransfer | Select-Object DisplayName, JobState, BytesTransferred | ConvertTo-Csv -NoTypeInformation\"");
        if (result.ExitCode != 0)
        {
            return [];
        }

        return ParseCsv(result.Output, (cols) => new TransferJobItem
        {
            Name = cols.ElementAtOrDefault(0) ?? "",
            State = cols.ElementAtOrDefault(1) ?? "",
            BytesTransferred = long.TryParse(cols.ElementAtOrDefault(2), out var b) ? b : 0
        });
    }

    public async Task<IReadOnlyList<TransferJobItem>> ListDeliveryOptimizationAsync()
    {
        var result = await HiddenProcess.RunAsync(
            "powershell.exe",
            "-NoProfile -NonInteractive -Command \"Get-DeliveryOptimizationStatus | Select-Object DownloadMode, DownloadModeName | ConvertTo-Csv -NoTypeInformation\"");
        if (result.ExitCode != 0)
        {
            return [];
        }

        return ParseCsv(result.Output, cols => new TransferJobItem
        {
            Name = "Delivery Optimization",
            State = cols.ElementAtOrDefault(1) ?? cols.ElementAtOrDefault(0) ?? "",
            BytesTransferred = 0
        });
    }

    private static List<TransferJobItem> ParseCsv(string output, Func<string[], TransferJobItem> map)
    {
        var lines = output.Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Skip(1).ToList();
        var list = new List<TransferJobItem>();
        foreach (var line in lines)
        {
            var cols = line.Split(',').Select(c => c.Trim('"')).ToArray();
            list.Add(map(cols));
        }

        return list;
    }
}

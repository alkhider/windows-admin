using WinAdmin.Core.Infrastructure;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class WindowsUpdateService
{
    public Task<IReadOnlyList<WindowsUpdateItem>> GetAvailableAsync() =>
        StaRunner.RunAsync(ReadAvailable);

    public Task<IReadOnlyList<UpdateHistoryItem>> GetHistoryAsync() =>
        StaRunner.RunAsync(ReadHistory);

    public Task<OperationResult> InstallAsync(IReadOnlyCollection<string> updateIds, IProgress<JobProgress>? progress = null) =>
        StaRunner.RunAsync(() => Install(updateIds, progress));

    public Task<OperationResult> InstallAllAsync(IProgress<JobProgress>? progress = null) =>
        StaRunner.RunAsync(() => Install(null, progress));

    private static IReadOnlyList<WindowsUpdateItem> ReadAvailable()
    {
        dynamic session = Create("Microsoft.Update.Session");
        session.ClientApplicationID = "WinAdmin";
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
        var list = new List<WindowsUpdateItem>();
        int count = result.Updates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic update = result.Updates.Item(i);
            list.Add(MapUpdate(update));
        }

        return list;
    }

    private static IReadOnlyList<UpdateHistoryItem> ReadHistory()
    {
        dynamic session = Create("Microsoft.Update.Session");
        session.ClientApplicationID = "WinAdmin";
        dynamic searcher = session.CreateUpdateSearcher();
        int total = 0;
        try
        {
            total = searcher.GetTotalHistoryCount();
        }
        catch
        {
            return [];
        }

        var take = Math.Min(total, 40);
        if (take <= 0)
        {
            return [];
        }

        dynamic history = searcher.QueryHistory(0, take);
        var list = new List<UpdateHistoryItem>();
        int count = history.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic item = history.Item(i);
            DateTime date = DateTime.Now;
            try { date = (DateTime)item.Date; } catch { /* ignore */ }
            list.Add(new UpdateHistoryItem
            {
                Date = date,
                Title = (string?)item.Title ?? "Windows Update",
                Result = MapResultCode(item.ResultCode)
            });
        }

        return list;
    }

    private static OperationResult Install(IReadOnlyCollection<string>? updateIds, IProgress<JobProgress>? progress)
    {
        progress?.Report(new JobProgress { Percent = 5, Message = "Searching for updates…" });
        dynamic session = Create("Microsoft.Update.Session");
        session.ClientApplicationID = "WinAdmin";
        dynamic searcher = session.CreateUpdateSearcher();
        dynamic searchResult = searcher.Search("IsInstalled=0 and IsHidden=0");
        dynamic collection = Create("Microsoft.Update.UpdateColl");

        int count = searchResult.Updates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic update = searchResult.Updates.Item(i);
            string id = (string)update.Identity.UpdateID;
            if (updateIds is not null && !updateIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (!Convert.ToBoolean(update.EulaAccepted))
                {
                    update.AcceptEula();
                }
            }
            catch
            {
                // Some updates do not expose EULA state.
            }

            collection.Add(update);
        }

        int selected = collection.Count;
        if (selected == 0)
        {
            return OperationResult.Success("No matching updates were found.");
        }

        progress?.Report(new JobProgress { Percent = 25, Message = $"Downloading {selected} update(s)…" });
        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = collection;
        downloader.Download();

        progress?.Report(new JobProgress { Percent = 70, Message = "Installing updates…" });
        dynamic installer = session.CreateUpdateInstaller();
        installer.AllowSourcePrompts = false;
        installer.Updates = collection;
        dynamic result = installer.Install();

        bool reboot = false;
        try { reboot = (bool)result.RebootRequired; } catch { /* ignore */ }
        int resultCode = 0;
        try { resultCode = (int)result.ResultCode; } catch { /* ignore */ }

        progress?.Report(new JobProgress { Percent = 100, Message = reboot ? "Installed. Restart required." : "Installed." });

        return resultCode is 2 or 3
            ? OperationResult.Success($"Installed {selected} update(s).", reboot)
            : OperationResult.Fail($"Windows Update finished with result code {resultCode}.");
    }

    private static WindowsUpdateItem MapUpdate(dynamic update)
    {
        string id = "";
        string title = "";
        string description = "";
        string kb = "";
        string severity = "";
        long size = 0;
        bool downloaded = false;
        bool reboot = false;

        try { id = (string)update.Identity.UpdateID; } catch { /* ignore */ }
        try { title = (string)update.Title; } catch { /* ignore */ }
        try { description = (string)update.Description; } catch { /* ignore */ }
        try { severity = (string)update.MsrcSeverity; } catch { /* ignore */ }
        try { size = Convert.ToInt64(update.MaxDownloadSize); } catch { /* ignore */ }
        try { downloaded = (bool)update.IsDownloaded; } catch { /* ignore */ }
        try { reboot = (bool)update.RebootRequired; } catch { /* ignore */ }
        try
        {
            int kbCount = update.KBArticleIDs.Count;
            if (kbCount > 0)
            {
                kb = "KB" + (string)update.KBArticleIDs.Item(0);
            }
        }
        catch
        {
            // ignore
        }

        return new WindowsUpdateItem
        {
            Id = id,
            Title = title,
            Description = description ?? "",
            Kb = kb,
            Severity = string.IsNullOrWhiteSpace(severity) ? "Optional" : severity,
            SizeBytes = size,
            IsDownloaded = downloaded,
            RebootRequired = reboot
        };
    }

    private static string MapResultCode(dynamic code)
    {
        try
        {
            return ((int)code) switch
            {
                2 => "Succeeded",
                3 => "Succeeded with errors",
                4 => "Failed",
                5 => "Aborted",
                _ => "Unknown"
            };
        }
        catch
        {
            return "Unknown";
        }
    }

    private static dynamic Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"COM class '{progId}' is not available on this PC.");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Could not create '{progId}'.");
    }
}

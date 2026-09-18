using System.Diagnostics.Eventing.Reader;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class EventLogService
{
    public IReadOnlyList<EventItem> Query(string logName, int level, int hours = 72, int max = 80)
    {
        var items = new List<EventItem>();
        var ms = hours * 3600 * 1000;
        var levelFilter = level switch
        {
            1 => "*[System[(Level=1) and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]",
            2 => "*[System[(Level=2) and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]",
            3 => "*[System[(Level=3) and TimeCreated[timediff(@SystemTime) <= " + ms + "]]]",
            _ => "*[System[TimeCreated[timediff(@SystemTime) <= " + ms + "]]]"
        };

        try
        {
            var query = new EventLogQuery(logName, PathType.LogName, levelFilter);
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && items.Count < max; record = reader.ReadEvent())
            {
                using (record)
                {
                    items.Add(new EventItem
                    {
                        Time = record.TimeCreated ?? DateTime.Now,
                        Source = record.ProviderName ?? "",
                        Level = MapLevel(record.Level),
                        Message = Truncate(record.FormatDescription() ?? "", 300)
                    });
                }
            }
        }
        catch
        {
            // access
        }

        return items.OrderByDescending(i => i.Time).ToList();
    }

    public EventItem? GetLastCritical(string logName = "System")
    {
        return Query(logName, 1, 168, 1).FirstOrDefault()
               ?? Query(logName, 2, 168, 1).FirstOrDefault();
    }

    private static string MapLevel(byte? level) => level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        _ => "Log"
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

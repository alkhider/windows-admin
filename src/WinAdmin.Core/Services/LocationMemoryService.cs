using System.Text.Json;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class LocationMemoryService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinAdmin",
        "ml");

    private string VisitsPath => Path.Combine(_dir, "locations.jsonl");
    private string PinsPath => Path.Combine(_dir, "location-pins.json");

    public void Record(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        lock (_gate)
        {
            Directory.CreateDirectory(_dir);
            var visit = new FolderVisit { At = DateTime.UtcNow, Path = Path.GetFullPath(path) };
            File.AppendAllText(VisitsPath, JsonSerializer.Serialize(visit) + Environment.NewLine);
        }
    }

    public IReadOnlyList<FrequentLocation> GetSuggestions(int take = 10)
    {
        var visits = ReadVisits();
        var pins = ReadPins();
        var now = DateTime.UtcNow;
        var ranked = visits
            .GroupBy(v => v.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var last = g.Max(v => v.At);
                var hours = Math.Max(0.25, (now - last).TotalHours);
                var score = g.Count() / (1 + hours / 24d);
                return new FrequentLocation
                {
                    Path = g.Key,
                    Name = FolderName(g.Key),
                    Visits = g.Count(),
                    LastVisit = last,
                    Score = score,
                    Pinned = pins.Contains(g.Key)
                };
            })
            .Where(l => Directory.Exists(l.Path))
            .ToList();

        foreach (var pin in pins.Where(p => ranked.All(r => !r.Path.Equals(p, StringComparison.OrdinalIgnoreCase))))
        {
            if (!Directory.Exists(pin))
            {
                continue;
            }

            ranked.Add(new FrequentLocation
            {
                Path = pin,
                Name = FolderName(pin),
                Visits = 0,
                LastVisit = DateTime.MinValue,
                Score = 1000,
                Pinned = true
            });
        }

        return ranked
            .OrderByDescending(l => l.Pinned)
            .ThenByDescending(l => l.Score)
            .Take(take)
            .ToList();
    }

    public void Pin(string path, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        path = Path.GetFullPath(path);
        lock (_gate)
        {
            var pins = ReadPins().ToList();
            if (pinned)
            {
                if (!pins.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    pins.Add(path);
                }
            }
            else
            {
                pins = pins.Where(p => !p.Equals(path, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            Directory.CreateDirectory(_dir);
            File.WriteAllText(PinsPath, JsonSerializer.Serialize(pins, Json));
        }
    }

    private List<FolderVisit> ReadVisits()
    {
        lock (_gate)
        {
            if (!File.Exists(VisitsPath))
            {
                return [];
            }

            var list = new List<FolderVisit>();
            foreach (var line in File.ReadAllLines(VisitsPath).TakeLast(4000))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var item = JsonSerializer.Deserialize<FolderVisit>(line, Json);
                    if (item is not null)
                    {
                        list.Add(item);
                    }
                }
                catch
                {
                    // skip
                }
            }

            return list;
        }
    }

    private HashSet<string> ReadPins()
    {
        lock (_gate)
        {
            if (!File.Exists(PinsPath))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                var pins = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(PinsPath), Json) ?? [];
                return new HashSet<string>(pins, StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private static string FolderName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }
}

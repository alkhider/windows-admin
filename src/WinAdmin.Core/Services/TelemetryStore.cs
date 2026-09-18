using System.Text.Json;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class TelemetryStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();

    public string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinAdmin",
        "ml");

    public string TelemetryPath => Path.Combine(DirectoryPath, "telemetry.jsonl");
    public string FeedbackPath => Path.Combine(DirectoryPath, "feedback.jsonl");
    public string ModelPath => Path.Combine(DirectoryPath, "ranker.zip");

    public void AppendSample(TelemetrySample sample)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(TelemetryPath, JsonSerializer.Serialize(sample, Json) + Environment.NewLine);
            Prune(TelemetryPath, TimeSpan.FromDays(7));
        }
    }

    public void AppendFeedback(RecommendationFeedback feedback)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(FeedbackPath, JsonSerializer.Serialize(feedback, Json) + Environment.NewLine);
        }
    }

    public IReadOnlyList<TelemetrySample> ReadSamples(int max = 2000)
    {
        lock (_gate)
        {
            return ReadLines<TelemetrySample>(TelemetryPath, max);
        }
    }

    public IReadOnlyList<RecommendationFeedback> ReadFeedback(int max = 2000)
    {
        lock (_gate)
        {
            return ReadLines<RecommendationFeedback>(FeedbackPath, max);
        }
    }

    public DateTime? LastSampleUtc()
    {
        var samples = ReadSamples(8);
        return samples.Count == 0 ? null : samples[^1].At;
    }

    private static List<T> ReadLines<T>(string path, int max)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var lines = File.ReadAllLines(path);
        var list = new List<T>();
        foreach (var line in lines.TakeLast(max))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var item = JsonSerializer.Deserialize<T>(line, Json);
                if (item is not null)
                {
                    list.Add(item);
                }
            }
            catch
            {
                // skip bad row
            }
        }

        return list;
    }

    private static void Prune(string path, TimeSpan keep)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - keep;
        var kept = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("At", out var at)
                    && at.TryGetDateTime(out var when)
                    && when < cutoff)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            kept.Add(line);
        }

        File.WriteAllLines(path, kept);
    }
}

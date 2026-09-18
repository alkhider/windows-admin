using Microsoft.ML;
using Microsoft.ML.Data;
using WinAdmin.Core.Models;

namespace WinAdmin.Core.Services;

public sealed class RecommendationService(
    HealthService health,
    LiveMetricsService metrics,
    WindowsUpdateService updates,
    TelemetryStore store)
{
    private readonly object _gate = new();
    private readonly MLContext _ml = new(seed: 1);
    private ITransformer? _ranker;
    private DateTime _lastTrain = DateTime.MinValue;
    private DateTime _lastUpdateScan = DateTime.MinValue;
    private int _updateCount;
    private DateTime _lastSample = DateTime.MinValue;

    public IReadOnlyList<RecommendationItem> GetRecommendations(int take = 8)
    {
        CaptureSample();
        TryTrain(force: false);

        var report = health.GetReport(_updateCount);
        var live = metrics.Sample();
        var samples = store.ReadSamples();
        var feedback = store.ReadFeedback();
        var candidates = BuildCandidates(report, live, samples);
        Rank(candidates, feedback);
        return HideRecentlyDismissed(candidates, feedback)
            .OrderByDescending(c => c.Confidence)
            .Take(take)
            .ToList();
    }

    public void CaptureSample()
    {
        if ((DateTime.UtcNow - _lastSample).TotalSeconds < 30)
        {
            return;
        }

        _lastSample = DateTime.UtcNow;
        try
        {
            var report = health.GetReport(_updateCount);
            var live = metrics.Sample();
            var hottest = report.Drives.OrderByDescending(d => d.UsedPercent).FirstOrDefault();
            store.AppendSample(new TelemetrySample
            {
                At = DateTime.UtcNow,
                CpuPercent = live.CpuPercent,
                MemoryPercent = live.MemoryPercent,
                DiskBusyPercent = live.DiskPercent,
                DiskUsedPercent = hottest?.UsedPercent ?? 0,
                GpuPercent = live.GpuPercent,
                TopProcessCpu = live.TopProcessCpu,
                TopProcessName = live.TopProcessName ?? "",
                ErrorCount = report.RecentErrors.Count,
                PendingReboot = report.PendingReboot,
                DefenderOff = report.Defender.Available && !report.Defender.RealTimeProtection,
                UpdateCount = _updateCount
            });
        }
        catch
        {
            // never break the portal
        }

        _ = RefreshUpdatesAsync();
    }

    public void RecordFeedback(RecommendationItem item, bool accepted, HealthReport? report = null, LiveMetrics? live = null)
    {
        report ??= health.GetReport(_updateCount);
        live ??= metrics.Sample();
        var hottest = report.Drives.OrderByDescending(d => d.UsedPercent).FirstOrDefault();
        store.AppendFeedback(new RecommendationFeedback
        {
            At = DateTime.UtcNow,
            Kind = item.Kind,
            Accepted = accepted,
            Disk = hottest?.UsedPercent ?? 0,
            Memory = live.MemoryPercent,
            Cpu = live.CpuPercent,
            Gpu = live.GpuPercent,
            Reboot = report.PendingReboot ? 1 : 0,
            DefenderOff = report.Defender.Available && !report.Defender.RealTimeProtection ? 1 : 0,
            Errors = report.RecentErrors.Count,
            Updates = _updateCount,
            TopCpu = (float)live.TopProcessCpu
        });
        TryTrain(force: true);
    }

    public async Task RefreshUpdatesAsync()
    {
        if ((DateTime.UtcNow - _lastUpdateScan).TotalMinutes < 15)
        {
            return;
        }

        _lastUpdateScan = DateTime.UtcNow;
        try
        {
            var list = await updates.GetAvailableAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _updateCount = list.Count;
        }
        catch
        {
            // WU scan is optional for ranking
        }
    }

    private List<RecommendationItem> BuildCandidates(HealthReport report, LiveMetrics live, IReadOnlyList<TelemetrySample> samples)
    {
        var list = new List<RecommendationItem>();
        var hottest = report.Drives.OrderByDescending(d => d.UsedPercent).FirstOrDefault();
        if (hottest is not null && hottest.UsedPercent >= 85)
        {
            list.Add(new RecommendationItem
            {
                Id = "disk-full",
                Kind = "disk-full",
                Title = $"Free space on {hottest.Name.TrimEnd('\\')}",
                Why = $"{hottest.Name} is {hottest.UsedPercent}% full ({StorageService.FormatBytes(hottest.FreeBytes)} free). Storage Sense can reclaim files without a Disk Cleanup popup.",
                ActionLabel = "Run Storage Sense",
                Action = "storage-sense",
                NavigateTo = "storage",
                NeedsConfirm = true,
                RuleScore = hottest.UsedPercent >= 95 ? 0.95 : hottest.UsedPercent >= 90 ? 0.82 : 0.68
            });
        }

        if (report.PendingReboot)
        {
            list.Add(new RecommendationItem
            {
                Id = "reboot",
                Kind = "reboot",
                Title = "Finish a pending restart",
                Why = "Windows is waiting on a restart to complete updates or servicing. Restart in 60 seconds from this portal, or cancel from System tasks.",
                ActionLabel = "Restart in 60 seconds",
                Action = "reboot",
                NavigateTo = "tasks",
                NeedsConfirm = true,
                RuleScore = 0.88
            });
        }

        if (report.Defender.Available && !report.Defender.RealTimeProtection)
        {
            list.Add(new RecommendationItem
            {
                Id = "defender",
                Kind = "defender",
                Title = "Turn Defender real-time protection back on",
                Why = "Microsoft Defender real-time protection is off. Start the Defender service from System tasks.",
                ActionLabel = "Open system tasks",
                Action = "navigate",
                NavigateTo = "tasks",
                RuleScore = 0.9
            });
        }

        if (live.MemoryPercent >= 85 || report.Memory.UsedPercent >= 85)
        {
            var mem = Math.Max(live.MemoryPercent, report.Memory.UsedPercent);
            list.Add(new RecommendationItem
            {
                Id = "memory",
                Kind = "memory",
                Title = "Memory is under pressure",
                Why = $"Memory is {mem}% used. Check the heaviest processes and end only what you recognize.",
                ActionLabel = "Open processes",
                Action = "navigate",
                NavigateTo = "processes",
                RuleScore = mem >= 92 ? 0.86 : 0.7
            });
        }

        if (live.TopProcessCpu >= 25 && !string.IsNullOrWhiteSpace(live.TopProcessName)
            && !live.TopProcessName.Equals("Idle", StringComparison.OrdinalIgnoreCase)
            && !live.TopProcessName.Equals("System Idle Process", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new RecommendationItem
            {
                Id = "high-cpu-process",
                Kind = "high-cpu-process",
                Title = $"{live.TopProcessName} is using the CPU",
                Why = $"{live.TopProcessName} is at {live.TopProcessCpu:0.0}% across all processors. Confirm before ending the task.",
                ActionLabel = "Open processes",
                Action = "navigate",
                NavigateTo = "processes?q=" + Uri.EscapeDataString(live.TopProcessName),
                RuleScore = Math.Clamp(0.45 + live.TopProcessCpu / 100d, 0.5, 0.92)
            });
        }

        if (report.RecentErrors.Count >= 8)
        {
            list.Add(new RecommendationItem
            {
                Id = "error-burst",
                Kind = "error-burst",
                Title = "Review recent error events",
                Why = $"{report.RecentErrors.Count} error events were recorded recently. Filter System and Application logs in Events.",
                ActionLabel = "Open events",
                Action = "navigate",
                NavigateTo = "events",
                RuleScore = Math.Clamp(0.4 + report.RecentErrors.Count / 40d, 0.5, 0.8)
            });
        }

        if (_updateCount > 0)
        {
            list.Add(new RecommendationItem
            {
                Id = "updates",
                Kind = "updates",
                Title = "Install waiting Windows updates",
                Why = $"{_updateCount} update(s) are available. Scan and install from Windows Update without opening a console.",
                ActionLabel = "Open Windows Update",
                Action = "navigate",
                NavigateTo = "updates",
                RuleScore = Math.Clamp(0.55 + _updateCount * 0.06, 0.55, 0.9)
            });
        }

        foreach (var spike in DetectSpikes(samples))
        {
            list.Add(spike);
        }

        if (list.Count == 0)
        {
            list.Add(new RecommendationItem
            {
                Id = "healthy",
                Kind = "healthy",
                Title = "No urgent actions",
                Why = "Local ranking did not find a high-priority issue. Open Performance to watch live gauges.",
                ActionLabel = "Open performance",
                Action = "navigate",
                NavigateTo = "performance",
                RuleScore = 0.2
            });
        }

        return list;
    }

    private List<RecommendationItem> DetectSpikes(IReadOnlyList<TelemetrySample> samples)
    {
        var list = new List<RecommendationItem>();
        if (samples.Count < 24)
        {
            return list;
        }

        try
        {
            if (IsSpike(samples.Select(s => (float)s.CpuPercent).ToArray()))
            {
                list.Add(new RecommendationItem
                {
                    Id = "cpu-anomaly",
                    Kind = "cpu-anomaly",
                    Title = "CPU spike detected",
                    Why = "ML.NET time-series anomaly detection saw a CPU spike versus this PC’s recent baseline.",
                    ActionLabel = "Open performance",
                    Action = "navigate",
                    NavigateTo = "performance",
                    RuleScore = 0.74,
                    Source = "Anomaly"
                });
            }

            if (IsSpike(samples.Select(s => (float)s.MemoryPercent).ToArray()))
            {
                list.Add(new RecommendationItem
                {
                    Id = "memory-anomaly",
                    Kind = "memory-anomaly",
                    Title = "Memory spike detected",
                    Why = "Memory use jumped versus the local baseline. Check processes before the machine starts swapping.",
                    ActionLabel = "Open processes",
                    Action = "navigate",
                    NavigateTo = "processes",
                    RuleScore = 0.72,
                    Source = "Anomaly"
                });
            }
        }
        catch
        {
            // time-series needs a warm history
        }

        return list;
    }

    private bool IsSpike(float[] values)
    {
        if (values.Length < 24)
        {
            return false;
        }

        var history = Math.Clamp(values.Length / 4, 8, 30);
        var data = _ml.Data.LoadFromEnumerable(values.Select(v => new TsRow { Value = v }));
        var pipeline = _ml.Transforms.DetectIidSpike(
            outputColumnName: nameof(SpikeRow.Prediction),
            inputColumnName: nameof(TsRow.Value),
            confidence: 95.0,
            pvalueHistoryLength: history);
        var model = pipeline.Fit(data);
        var viewed = model.Transform(data);
        var rows = _ml.Data.CreateEnumerable<SpikeRow>(viewed, reuseRowObject: false).ToList();
        var last = rows.LastOrDefault();
        return last?.Prediction is { Length: > 0 } && last.Prediction[0] == 1;
    }

    private void Rank(List<RecommendationItem> items, IReadOnlyList<RecommendationFeedback> feedback)
    {
        lock (_gate)
        {
            foreach (var item in items)
            {
                var ml = Predict(item, feedback);
                item.Confidence = ml is null
                    ? item.RuleScore
                    : Math.Clamp(0.45 * item.RuleScore + 0.55 * ml.Value, 0.05, 0.99);
                if (ml is not null && item.Source != "Anomaly")
                {
                    item.Source = "Learned";
                }
            }
        }
    }

    private double? Predict(RecommendationItem item, IReadOnlyList<RecommendationFeedback> feedback)
    {
        if (_ranker is null)
        {
            return KindBias(item.Kind, feedback);
        }

        try
        {
            var engine = _ml.Model.CreatePredictionEngine<RankRow, RankPrediction>(_ranker);
            var row = ToRankRow(new RecommendationFeedback
            {
                Kind = item.Kind,
                Accepted = true,
                Disk = item.Kind == "disk-full" ? 90 : 50,
                Cpu = item.Kind is "high-cpu-process" or "cpu-anomaly" ? 70 : 20
            }, item.Kind);
            var pred = engine.Predict(row);
            return Math.Clamp(pred.Probability, 0.05, 0.98);
        }
        catch
        {
            return KindBias(item.Kind, feedback);
        }
    }

    private static double? KindBias(string kind, IReadOnlyList<RecommendationFeedback> feedback)
    {
        var rows = feedback.Where(f => f.Kind == kind).ToList();
        if (rows.Count < 3)
        {
            return null;
        }

        return rows.Count(f => f.Accepted) / (double)rows.Count;
    }

    private static IEnumerable<RecommendationItem> HideRecentlyDismissed(
        IEnumerable<RecommendationItem> items,
        IReadOnlyList<RecommendationFeedback> feedback)
    {
        var hidden = feedback
            .Where(f => !f.Accepted && f.At > DateTime.UtcNow.AddHours(-12))
            .Select(f => f.Kind)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item.Kind == "healthy" || item.RuleScore >= 0.93)
            {
                yield return item;
                continue;
            }

            if (!hidden.Contains(item.Kind))
            {
                yield return item;
            }
        }
    }

    private void TryLoadModel()
    {
        if (_ranker is not null || !File.Exists(store.ModelPath))
        {
            return;
        }

        try
        {
            _ranker = _ml.Model.Load(store.ModelPath, out _);
        }
        catch
        {
            _ranker = null;
        }
    }

    private void TryTrain(bool force)
    {
        lock (_gate)
        {
            if (!force && (DateTime.UtcNow - _lastTrain).TotalMinutes < 15)
            {
                TryLoadModel();
                return;
            }

            TryLoadModel();
            var feedback = store.ReadFeedback();
            if (feedback.Count < 8)
            {
                return;
            }

            try
            {
                var rows = feedback.Select(f => ToRankRow(f, f.Kind)).ToList();
                var data = _ml.Data.LoadFromEnumerable(rows);
                var pipeline = _ml.Transforms.Concatenate("Features",
                        nameof(RankRow.Disk), nameof(RankRow.Memory), nameof(RankRow.Cpu), nameof(RankRow.Gpu),
                        nameof(RankRow.Reboot), nameof(RankRow.DefenderOff), nameof(RankRow.Errors),
                        nameof(RankRow.Updates), nameof(RankRow.TopCpu),
                        nameof(RankRow.KindDisk), nameof(RankRow.KindReboot), nameof(RankRow.KindDefender),
                        nameof(RankRow.KindProcess), nameof(RankRow.KindEvents), nameof(RankRow.KindUpdates),
                        nameof(RankRow.KindAnomaly))
                    .Append(_ml.BinaryClassification.Trainers.SdcaLogisticRegression(
                        labelColumnName: nameof(RankRow.Accepted),
                        featureColumnName: "Features"));
                _ranker = pipeline.Fit(data);
                Directory.CreateDirectory(store.DirectoryPath);
                _ml.Model.Save(_ranker, data.Schema, store.ModelPath);
                _lastTrain = DateTime.UtcNow;
            }
            catch
            {
                _ranker = null;
            }
        }
    }

    private static RankRow ToRankRow(RecommendationFeedback f, string kind) => new()
    {
        Accepted = f.Accepted,
        Disk = f.Disk,
        Memory = f.Memory,
        Cpu = f.Cpu,
        Gpu = f.Gpu,
        Reboot = f.Reboot,
        DefenderOff = f.DefenderOff,
        Errors = f.Errors,
        Updates = f.Updates,
        TopCpu = f.TopCpu,
        KindDisk = kind == "disk-full" ? 1 : 0,
        KindReboot = kind == "reboot" ? 1 : 0,
        KindDefender = kind == "defender" ? 1 : 0,
        KindProcess = kind is "high-cpu-process" or "memory" ? 1 : 0,
        KindEvents = kind == "error-burst" ? 1 : 0,
        KindUpdates = kind == "updates" ? 1 : 0,
        KindAnomaly = kind.Contains("anomaly", StringComparison.OrdinalIgnoreCase) ? 1 : 0
    };

    private sealed class TsRow
    {
        public float Value { get; init; }
    }

    private sealed class SpikeRow
    {
        [VectorType(3)]
        public double[] Prediction { get; set; } = [];
    }

    private sealed class RankRow
    {
        public bool Accepted { get; init; }
        public float Disk { get; init; }
        public float Memory { get; init; }
        public float Cpu { get; init; }
        public float Gpu { get; init; }
        public float Reboot { get; init; }
        public float DefenderOff { get; init; }
        public float Errors { get; init; }
        public float Updates { get; init; }
        public float TopCpu { get; init; }
        public float KindDisk { get; init; }
        public float KindReboot { get; init; }
        public float KindDefender { get; init; }
        public float KindProcess { get; init; }
        public float KindEvents { get; init; }
        public float KindUpdates { get; init; }
        public float KindAnomaly { get; init; }
    }

    private sealed class RankPrediction
    {
        [ColumnName("PredictedLabel")]
        public bool PredictedLabel { get; set; }

        public float Probability { get; set; }
        public float Score { get; set; }
    }
}

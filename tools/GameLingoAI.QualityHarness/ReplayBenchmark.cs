using GameLingoAI.Core.Models;

namespace GameLingoAI.QualityHarness;

public enum BenchmarkStatus { Passed, Breached, Incompatible }

public sealed record BenchmarkAssessment(string Baseline, BenchmarkStatus Status, IReadOnlyList<string> Failures)
{
    public int ExitCode => Status switch { BenchmarkStatus.Passed => 0, BenchmarkStatus.Breached => 3, _ => 2 };
}

/// <summary>Versioned synthetic-workload regression gate; never a native OCR/overlay or release-performance claim.</summary>
public static class ReplayBenchmark
{
    public const string BaselineId = "synthetic-40-v1-20261004";
    public const string CorpusHash = "E8E51AB08F1E3EEFD62B02021CD851A40A20E1E21717DD550DB9B791406A959C";
    private static readonly double[] ReferenceP95 = [2.2828, 1.8423, .0104, .2494, 119.6668, .0289];
    private static readonly double[] SchedulerFloors = [10, 10, 5, 10, 200, 5];
    private static readonly long[] RequiredCounts = [40, 40, 40, 40, 4, 5];
    private static readonly BenchmarkAssessment Incompatible = new(BaselineId, BenchmarkStatus.Incompatible,
        Array.AsReadOnly(new[] { "CorpusMismatch" }));

    public static BenchmarkAssessment Evaluate(ReplayRunReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Frames != 40 || report.CorpusVersion != ReplayCorpus.Version || report.CorpusSha256 != CorpusHash)
        { return Incompatible; }
        var failures = new List<string>();
        var stages = report.Pipeline.Stages.Take(7).ToArray();
        if (stages.Length != 6 || stages.Select(stage => stage.Stage).Distinct().Count() != 6 ||
            stages.Any(stage => !Enum.IsDefined(stage.Stage)))
        { failures.Add("StageSetInvalid"); }
        else
        {
            foreach (var stage in stages)
            {
                var index = (int)stage.Stage;
                if (stage.Count != RequiredCounts[index] || !Finite(stage.P50Milliseconds) ||
                    !Finite(stage.P95Milliseconds) || stage.P50Milliseconds > stage.P95Milliseconds)
                { failures.Add("InvalidMetrics:" + stage.Stage); }
                else if (stage.P95Milliseconds > Math.Max(ReferenceP95[index] * 4, SchedulerFloors[index]))
                { failures.Add("P95Regression:" + stage.Stage); }
            }
        }
        if (report.ReleasedFrames != 40 || report.Pipeline.DroppedFrames != 0 || report.Pipeline.DroppedText != 0 ||
            report.Pipeline.DroppedResults != 0 || report.Pipeline.StaleResults != 0 || report.Pipeline.Errors != 0 || report.Pipeline.CacheHits != 1)
        { failures.Add("PipelineInvariant"); }
        if (report.EndToEnd.TotalCount != 5 || report.EndToEnd.WindowCount != 5 ||
            report.EndToEnd.P50Milliseconds is not { } p50 || report.EndToEnd.P95Milliseconds is not { } p95 ||
            report.EndToEnd.MaximumMilliseconds is not { } maximum ||
            !Finite(p50) || !Finite(p95) || !Finite(maximum) || p50 > p95 || p95 > maximum)
        { failures.Add("EndToEndInvalid"); }
        else if (p95 > 125.5566 * 4)
        { failures.Add("EndToEndRegression"); }
        if (!Finite(report.WallMilliseconds) || report.WallMilliseconds < 3900 || report.WallMilliseconds > 4430.8636 * 2)
        { failures.Add("WallDurationRegression"); }
        if (report.AllocatedBytes <= 0 || report.AllocatedBytesPerFrame > 990207.4 * 1.5)
        { failures.Add("AllocationRegression"); }
        if (!Finite(report.CpuMilliseconds) || report.CpuMilliseconds > Math.Max(78.125 * 4, 500))
        { failures.Add("CpuRegression"); }
        if (report.PrivateBytesBefore <= 0 || report.PrivateBytesAfter <= 0)
        { failures.Add("ResourceSampleMissing"); }
        return new(BaselineId, failures.Count == 0 ? BenchmarkStatus.Passed : BenchmarkStatus.Breached, failures.AsReadOnly());
    }

    private static bool Finite(double value) => double.IsFinite(value) && value >= 0;
}

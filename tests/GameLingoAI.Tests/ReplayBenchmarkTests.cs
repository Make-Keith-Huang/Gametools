using GameLingoAI.Core.Models;
using GameLingoAI.QualityHarness;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class ReplayBenchmarkTests
{
    [Fact]
    public void ReferenceWorkloadPassesButDifferentCorpusCannotPass()
    {
        Assert.Equal(0, ReplayBenchmark.Evaluate(Reference()).ExitCode);
        Assert.Equal(2, ReplayBenchmark.Evaluate(Reference() with { CorpusSha256 = new string('0', 64) }).ExitCode);
        Assert.Equal(2, ReplayBenchmark.Evaluate(Reference() with { Frames = 8 }).ExitCode);
    }

    [Fact]
    public void LatencyAllocationCpuDropsAndMissingDataFailClosed()
    {
        var baseline = Reference();
        ReplayRunReport[] invalid =
        [
            baseline with { AllocatedBytes = 100_000_000 },
            baseline with { CpuMilliseconds = double.NaN },
            baseline with { CpuMilliseconds = 501 },
            baseline with { WallMilliseconds = 9000 },
            baseline with { WallMilliseconds = 1 },
            baseline with { PrivateBytesAfter = 0 },
            baseline with { ReleasedFrames = 39 },
            baseline with { Pipeline = baseline.Pipeline with { DroppedText = 1 } },
            baseline with { Pipeline = baseline.Pipeline with { Stages = [] } },
            baseline with { EndToEnd = baseline.EndToEnd with { P95Milliseconds = 1000 } },
            baseline with { EndToEnd = baseline.EndToEnd with { P95Milliseconds = null } },
            baseline with { EndToEnd = baseline.EndToEnd with { MaximumMilliseconds = double.NaN } }
        ];
        foreach (var report in invalid)
        {
            var assessment = ReplayBenchmark.Evaluate(report);
            Assert.Equal(3, assessment.ExitCode);
            Assert.NotEmpty(assessment.Failures);
        }
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1, 10_000 })
        {
            var stages = baseline.Pipeline.Stages.ToArray();
            stages[0] = stages[0] with { P95Milliseconds = value };
            Assert.Equal(3, ReplayBenchmark.Evaluate(baseline with { Pipeline = baseline.Pipeline with { Stages = stages } }).ExitCode);
        }
    }

    private static ReplayRunReport Reference() => new(ReplayCorpus.Version, ReplayBenchmark.CorpusHash, 40,
        new(null, null, 0, 0, 0, 0, 1, 0,
        [new(PipelineStage.Capture, 40, 1.1949, 2.2828), new(PipelineStage.Preprocessing, 40, .4617, 1.8423),
         new(PipelineStage.Ocr, 40, .0046, .0104), new(PipelineStage.Stabilization, 40, .0865, .2494),
         new(PipelineStage.Translation, 4, 105.3406, 119.6668), new(PipelineStage.Presentation, 5, .0039, .0289)]),
        new(5, 5, 107.5665, 125.5566, 125.5566), 4430.8636, 39608296, 78.125, 8273920, 22532096, 40);
}

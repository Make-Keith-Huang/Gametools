using GameLingoAI.QualityHarness;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class QualityMetricTests
{
    [Fact]
    public void PercentilesAreNearestRankAndUseOnlyBoundedRecentSamples()
    {
        var window = new LatencyWindow(20);
        Assert.Null(window.Snapshot().P95Milliseconds);
        for (var value = 1; value <= 100; value++)
        { window.Record(value); }
        var sample = window.Snapshot();
        Assert.Equal(100, sample.TotalCount);
        Assert.Equal(20, sample.WindowCount);
        Assert.Equal(90, sample.P50Milliseconds);
        Assert.Equal(99, sample.P95Milliseconds);
        Assert.Equal(100, sample.MaximumMilliseconds);
        window.Record(0);
        Assert.Equal(100, sample.TotalCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Record(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Record(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => window.Record(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LatencyWindow(65_537));
    }

    [Fact]
    public void ConcurrentSamplingHasExactCountsAndFiniteOrderedPercentiles()
    {
        var window = new LatencyWindow(128);
        Parallel.For(0, 10_000, value =>
        {
            window.Record(value % 100);
            var snapshot = window.Snapshot();
            Assert.InRange(snapshot.WindowCount, 1, 128);
            Assert.True(snapshot.P50Milliseconds <= snapshot.P95Milliseconds);
        });
        Assert.Equal(10_000, window.Snapshot().TotalCount);
        Assert.Equal(128, window.Snapshot().WindowCount);
    }

    [Fact]
    public void WarmupDoesNotCountAndExactLimitIsABreachEvenAfterRecovery()
    {
        var gate = new MemoryGrowthGate(TimeSpan.FromSeconds(10));
        gate.Observe(TimeSpan.Zero, 500_000_000);
        gate.Observe(TimeSpan.FromSeconds(10), 100_000_000);
        gate.Observe(TimeSpan.FromSeconds(11), 149_999_999);
        Assert.Equal(GrowthStatus.Passed, gate.Assess(TimeSpan.FromSeconds(11)).Status);
        gate.Observe(TimeSpan.FromSeconds(12), 150_000_000);
        gate.Observe(TimeSpan.FromSeconds(13), 90_000_000);
        var result = gate.Assess(TimeSpan.FromSeconds(13));
        Assert.Equal(GrowthStatus.Breached, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal(100_000_000, result.BaselineBytes);
        Assert.Equal(50_000_000, result.PeakGrowthBytes);
        Assert.Equal(4, result.PostWarmupSamples);
    }

    [Fact]
    public void MissingCountersShortRunsAndOneSampleCannotPass()
    {
        var gate = new MemoryGrowthGate(TimeSpan.Zero);
        Assert.Equal(2, gate.Assess(TimeSpan.FromHours(1)).ExitCode);
        gate.Observe(TimeSpan.Zero, 10);
        Assert.Equal(GrowthStatus.InsufficientSamples, gate.Assess(TimeSpan.FromSeconds(1)).Status);
        gate.Observe(TimeSpan.FromSeconds(1), 20);
        Assert.Equal(GrowthStatus.InsufficientSamples, gate.Assess(TimeSpan.FromHours(1)).Status);
        gate.Observe(TimeSpan.FromHours(1), null);
        gate.Observe(TimeSpan.FromHours(2), 20);
        Assert.Equal(GrowthStatus.Unavailable, gate.Assess(TimeSpan.FromHours(1)).Status);
        Assert.Equal(2, gate.Assess(TimeSpan.FromHours(1)).ExitCode);
    }

    [Fact]
    public void GrowthGateRejectsInvalidAndOutOfOrderMeasurements()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryGrowthGate(TimeSpan.FromSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryGrowthGate(TimeSpan.Zero, 0));
        var gate = new MemoryGrowthGate(TimeSpan.FromSeconds(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Assess(TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.Observe(TimeSpan.Zero, -1));
        gate.Observe(TimeSpan.FromSeconds(1), 10);
        Assert.Throws<ArgumentException>(() => gate.Observe(TimeSpan.FromSeconds(1), 10));
        Assert.Throws<ArgumentException>(() => gate.Observe(TimeSpan.Zero, 10));
    }
}

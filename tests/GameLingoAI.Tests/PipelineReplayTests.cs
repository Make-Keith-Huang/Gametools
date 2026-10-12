using GameLingoAI.Core.Models;
using GameLingoAI.QualityHarness;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class PipelineReplayTests
{
    [Fact]
    public async Task RealPipelineReplayRepeatsInputAndDrainsFrames()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first = await PipelineReplay.RunAsync(40, timeout.Token);
        var second = await PipelineReplay.RunAsync(40, timeout.Token);
        Assert.Equal(first.CorpusSha256, second.CorpusSha256);
        foreach (var result in new[] { first, second })
        {
            Assert.Equal(40, result.ReleasedFrames);
            Assert.Equal(0, result.Pipeline.Errors);
            Assert.Equal(0, result.Pipeline.DroppedFrames);
            Assert.True(result.Pipeline.CacheHits > 0);
            Assert.Equal(40, result.Pipeline.Stages.Single(stage => stage.Stage == PipelineStage.Stabilization).Count);
            Assert.True(result.EndToEnd.TotalCount > 0);
            Assert.True(result.EndToEnd.P95Milliseconds >= result.EndToEnd.P50Milliseconds);
            Assert.True(result.AllocatedBytes > 0);
            Assert.True(result.WallMilliseconds > 0);
        }
        Assert.Equal(first.EndToEnd.TotalCount, second.EndToEnd.TotalCount);
    }

    [Fact]
    public async Task ReplayHonorsCancellationAndNonTransientFailure()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PipelineReplay.RunAsync(8, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => PipelineReplay.RunAsync(9, CancellationToken.None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PipelineReplay.RunAsync(8, timeout.Token,
            [new(TimeSpan.Zero, SimulatedOutcome.AuthenticationRejected)]));
        Assert.Equal(8, (await PipelineReplay.RunAsync(8, timeout.Token)).ReleasedFrames);
    }
}

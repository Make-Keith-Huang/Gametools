using System.Security.Cryptography;
using GameLingoAI.Core.Models;
using GameLingoAI.QualityHarness;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class ReplayTests
{
    [Fact]
    public void CorpusRepeatsMetadataAndPixelsWithoutRetainingBuffers()
    {
        var first = new ReplayCorpus(40);
        var second = new ReplayCorpus(40);
        var hashes = new List<string>();
        for (var index = 0; index < first.Count; index++)
        {
            using var left = first.CreateFrame(index, CancellationToken.None);
            using var right = second.CreateFrame(index, CancellationToken.None);
            Assert.Equal(left.Metadata, right.Metadata);
            Assert.Equal(DateTimeOffset.UnixEpoch.AddMilliseconds(index * 100), left.Metadata.CapturedAtUtc);
            Assert.Equal(index, left.Metadata.Sequence.Value);
            Assert.True(left.BgraPixels.Span.SequenceEqual(right.BgraPixels.Span));
            hashes.Add(Convert.ToHexString(SHA256.HashData(left.BgraPixels.Span)));
            var retainedView = left.BgraPixels;
            left.Dispose();
            left.Dispose();
            Assert.True(retainedView.Span.IndexOfAnyExcept((byte)0) < 0);
            Assert.Contains((byte)255, right.BgraPixels.ToArray());
        }
        Assert.Single(hashes.Take(8).Distinct());
        Assert.NotEqual(hashes[0], hashes[8]);
        Assert.NotEqual(hashes[16], hashes[24]);
        Assert.Equal(40, Enumerable.Range(0, 40).Select(index => first.Metadata(index).CorrelationId).Distinct().Count());
    }

    [Fact]
    public void CorpusRejectsInvalidRangesAndCancellation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayCorpus(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayCorpus(100_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplayCorpus(interval: TimeSpan.Zero));
        var corpus = new ReplayCorpus(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => corpus.Metadata(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => corpus.Metadata(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => corpus.CreateFrame(0, cancellation.Token));
    }

    [Fact]
    public async Task ProviderScriptRepeatsLatencyFaultsAndRecovery()
    {
        var steps = Enum.GetValues<SimulatedOutcome>().Select((outcome, index) =>
            new ProviderStep(TimeSpan.FromMilliseconds(100 + index * 280), outcome)).ToArray();
        var expected = steps.ToArray();
        var delays = new List<TimeSpan>();
        var provider = new ScriptedTranslationProvider(steps, (duration, token) =>
        {
            token.ThrowIfCancellationRequested();
            delays.Add(duration);
            return Task.CompletedTask;
        });
        steps[0] = new ProviderStep(TimeSpan.Zero, SimulatedOutcome.Disconnect);
        var request = Request();
        for (var index = 0; index < 12; index++)
        {
            var step = expected[index % expected.Length];
            var result = await provider.TranslateAsync(request, CancellationToken.None);
            Assert.Equal(step.Latency, delays[index]);
            Assert.Equal(step.Outcome == SimulatedOutcome.Success, result.IsSuccess);
            if (result.IsSuccess)
            {
                Assert.Equal(request.Metadata, result.Value!.Metadata);
                Assert.Equal(step.Latency, result.Value.Duration);
            }
            else
            {
                Assert.Equal(request.Metadata.CorrelationId, result.Failure!.CorrelationId);
                Assert.DoesNotContain(request.Text, result.Failure.Message, StringComparison.Ordinal);
                Assert.Equal(step.Outcome != SimulatedOutcome.AuthenticationRejected, result.Failure.IsTransient);
                Assert.Equal(step.Outcome switch
                {
                    SimulatedOutcome.RateLimited => FailureCode.RateLimited,
                    SimulatedOutcome.PacketLoss => FailureCode.Timeout,
                    SimulatedOutcome.AuthenticationRejected => FailureCode.AuthenticationRejected,
                    _ => FailureCode.ProviderUnavailable
                }, result.Failure.Code);
            }
        }
        Assert.Equal(12, provider.Calls);
    }

    [Fact]
    public async Task ProviderCancellationHasNoSleepOrLateSuccess()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedTranslationProvider([new(TimeSpan.FromMilliseconds(1500), SimulatedOutcome.Success)],
            async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        using var cancellation = new CancellationTokenSource();
        var pending = provider.TranslateAsync(Request(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync(Request(), cancellation.Token));
        Assert.Equal(1, provider.Calls);
        release.TrySetResult();
        Assert.True((await provider.TranslateAsync(Request(), CancellationToken.None)).IsSuccess);
    }

    [Fact]
    public void ProviderScriptRejectsUnboundedOrInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptedTranslationProvider([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptedTranslationProvider(new ProviderStep[1025]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptedTranslationProvider([new(TimeSpan.FromSeconds(31), SimulatedOutcome.Success)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScriptedTranslationProvider([new(TimeSpan.Zero, (SimulatedOutcome)999)]));
    }

    private static TranslationRequest Request() => new(new ReplayCorpus(1).Metadata(0), "en", "ja", "SECRET synthetic input",
        null, null, "fixture-v1");
}

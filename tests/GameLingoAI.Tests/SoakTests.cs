using System.Diagnostics;
using System.Text.Json;
using GameLingoAI.Core.Models;
using GameLingoAI.QualityHarness;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class SoakTests
{
    private static readonly SoakOptions Short = new(false, TimeSpan.FromMilliseconds(100), TimeSpan.Zero, TimeSpan.FromMilliseconds(10));

    [Fact]
    public async Task WallClockIdleSamplesAndActiveSessionDrains()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var idle = await SoakRunner.RunAsync(Short, timeout.Token, _ => Task.FromResult(Counters(100)));
        Assert.Equal(GrowthStatus.Passed, idle.Growth.Status);
        Assert.True(idle.Growth.ObservedDuration >= Short.Duration);
        Assert.True(idle.SampleCount >= 2);
        Assert.Equal(idle.SampleCount, idle.RecentSamples.Count);
        Assert.Equal(0, idle.Workload.Frames);
        Assert.Null(idle.Workload.FrameDropRate);
        var active = await PipelineReplay.RunForAsync(true, TimeSpan.FromSeconds(1), timeout.Token);
        Assert.True(active.Frames > 0);
        Assert.Equal(active.Frames, active.ReleasedFrames);
        Assert.Equal(0, active.Pipeline.Errors);
    }

    [Fact]
    public async Task SampledBreachMissingCounterAndEarlyWorkloadCannotPass()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var samples = 0;
        var breach = await SoakRunner.RunAsync(Short, timeout.Token,
            _ => Task.FromResult(Counters(++samples == 1 ? 100 : 50_000_100)));
        Assert.Equal(3, breach.Growth.ExitCode);
        var missing = await SoakRunner.RunAsync(Short, timeout.Token, _ => Task.FromResult(Counters(null)));
        Assert.Equal(GrowthStatus.Unavailable, missing.Growth.Status);
        var early = await SoakRunner.RunAsync(Short, timeout.Token, _ => Task.FromResult(Counters(100)),
            (_, _, _) => Task.FromResult(EmptyReport()));
        Assert.Equal(2, early.Growth.ExitCode);
    }

    [Fact]
    public async Task CancellationAndSamplerFailureDrainOwnedWorkload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        async Task<ReplayRunReport> Work(bool active, TimeSpan duration, CancellationToken token)
        {
            entered.TrySetResult();
            try
            { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
            return EmptyReport();
        }
        var run = SoakRunner.RunAsync(Short, cancellation.Token, _ => Task.FromResult(Counters(100)), Work);
        await entered.Task.WaitAsync(timeout.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(stopped);
        stopped = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => SoakRunner.RunAsync(Short, timeout.Token,
            _ => throw new InvalidOperationException("Synthetic sampler failure"), Work));
        Assert.True(stopped);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SoakRunner.RunAsync(Short, cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => (Short with { Warmup = Short.Duration }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (Short with { SampleInterval = TimeSpan.Zero }).Validate());
        SoakOptions.ActiveHour.Validate();
        SoakOptions.IdleEightHours.Validate();
        SoakOptions.ShortCi.Validate();
    }

    [Theory]
    [InlineData("steady", 0, "Passed")]
    [InlineData("breach", 3, "Breached")]
    [InlineData("missing", 2, "Unavailable")]
    [InlineData("short", 2, "InsufficientSamples")]
    public async Task CliGrowthFixturesSaveHonestReportsAndReturnRealExitCodes(string scenario, int expected, string status)
    {
        var directory = Directory.CreateTempSubdirectory("gamelingo-quality-");
        try
        {
            var destination = Path.Combine(directory.FullName, "probe.json");
            var result = await RunCliAsync("growth-check", scenario, destination);
            Assert.Equal(expected, result);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(destination));
            Assert.Equal("virtual-growth-check", document.RootElement.GetProperty("Mode").GetString());
            Assert.False(document.RootElement.GetProperty("WallClockMeasured").GetBoolean());
            Assert.False(document.RootElement.GetProperty("ReleaseGateEvaluated").GetBoolean());
            Assert.Equal(status, document.RootElement.GetProperty("Assessment").GetProperty("Status").GetString());
            var original = await File.ReadAllBytesAsync(destination);
            Assert.Equal(2, await RunCliAsync("growth-check", scenario, destination));
            Assert.Equal(original, await File.ReadAllBytesAsync(destination));
            Assert.Single(directory.GetFiles());
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task CliBadInputAndFatalProviderDoNotCommitReports()
    {
        var directory = Directory.CreateTempSubdirectory("gamelingo-quality-");
        try
        {
            var destination = Path.Combine(directory.FullName, "fault.json");
            Assert.Equal(2, await RunCliAsync("replay", "9", destination));
            Assert.Equal(2, await RunCliAsync("soak", "unknown", destination));
            Assert.Equal(1, await RunCliAsync("fault-replay", "auth", destination));
            Assert.Empty(directory.GetFiles());
            Assert.Throws<ArgumentException>(() => FaultProfiles.Select("unknown"));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task<int> RunCliAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.ChangeExtension(typeof(SoakRunner).Assembly.Location, ".exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        { process.StartInfo.ArgumentList.Add(argument); }
        Assert.True(process.Start());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.DoesNotContain("Exception", await output + await error, StringComparison.Ordinal);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static ResourceMetrics Counters(long? value) => new(value, value, 0, 0, 0, 0);
    private static ReplayRunReport EmptyReport() => new(ReplayCorpus.Version, "", 0,
        new(null, null, 0, 0, 0, 0, 0, 0, []), new(0, 0, null, null, null), 0, 0, 0, 0, 0, 0);
}

using System.Diagnostics;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;

namespace GameLingoAI.QualityHarness;

public sealed record SoakOptions(bool Active, TimeSpan Duration, TimeSpan Warmup, TimeSpan SampleInterval)
{
    public static SoakOptions ActiveHour { get; } = new(true, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    public static SoakOptions IdleEightHours { get; } = new(false, TimeSpan.FromHours(8), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10));
    public static SoakOptions ShortCi { get; } = new(true, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1));

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Duration, TimeSpan.FromMilliseconds(10));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Duration, Active ? TimeSpan.FromHours(1) : TimeSpan.FromHours(8));
        ArgumentOutOfRangeException.ThrowIfLessThan(Warmup, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(Warmup, Duration);
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleInterval, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SampleInterval, (Duration - Warmup) / 2);
    }
}

public sealed record SoakSample(TimeSpan Elapsed, ResourceMetrics Resources);
public sealed record SoakReport(SoakOptions Options, GrowthAssessment Growth, long SampleCount,
    IReadOnlyList<SoakSample> RecentSamples, ReplayRunReport Workload);

/// <summary>Wall-clock sampling with bounded report storage and fail-closed peak growth across all samples.</summary>
public static class SoakRunner
{
    public const int MaximumRetainedSamples = 1024;

    public static async Task<SoakReport> RunAsync(SoakOptions options, CancellationToken token,
        Func<CancellationToken, Task<ResourceMetrics>>? sample = null,
        Func<bool, TimeSpan, CancellationToken, Task<ReplayRunReport>>? workload = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        token.ThrowIfCancellationRequested();
        sample ??= CurrentProcessMetrics.SampleAsync;
        workload ??= PipelineReplay.RunForAsync;
        var samples = new Queue<SoakSample>(MaximumRetainedSamples);
        var gate = new MemoryGrowthGate(options.Warmup);
        var watch = Stopwatch.StartNew();
        long count = 0;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        // A broken workload or sampler cannot leave an unattended multi-hour process running forever.
        lifetime.CancelAfter(options.Duration + TimeSpan.FromSeconds(30));
        var running = workload(options.Active, options.Duration, lifetime.Token);
        try
        {
            while (true)
            {
                var resource = await sample(lifetime.Token).ConfigureAwait(false);
                var elapsed = watch.Elapsed;
                gate.Observe(elapsed, resource.PrivateBytes);
                if (samples.Count == MaximumRetainedSamples)
                { samples.Dequeue(); }
                samples.Enqueue(new(elapsed, resource));
                count++;
                if (running.IsCompleted)
                { break; }
                var delay = Task.Delay(options.SampleInterval, lifetime.Token);
                await Task.WhenAny(running, delay).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
            }
            var result = await running.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return new(options, gate.Assess(options.Duration), count, Array.AsReadOnly(samples.ToArray()), result);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            // Observe and drain the workload even when the sampler or caller fails.
            try
            { await running.ConfigureAwait(false); }
            catch (Exception) when (lifetime.IsCancellationRequested) { }
        }
    }
}

namespace GameLingoAI.QualityHarness;

public enum GrowthStatus { Passed, Breached, InsufficientSamples, Unavailable }

public sealed record GrowthAssessment(GrowthStatus Status, long? BaselineBytes, long? PeakGrowthBytes,
    long LimitBytes, long PostWarmupSamples, TimeSpan ObservedDuration)
{
    /// <summary>For the harness CLI: 0 passed, 3 threshold breach, 2 incomplete/unverifiable run.</summary>
    public int ExitCode => Status switch { GrowthStatus.Passed => 0, GrowthStatus.Breached => 3, _ => 2 };
}

/// <summary>Constant-space peak private-byte growth after warm-up; never forgives an earlier breach after recovery.</summary>
public sealed class MemoryGrowthGate
{
    private readonly TimeSpan _warmup;
    private readonly long _limit;
    private TimeSpan? _last;
    private long? _baseline;
    private long _peakGrowth;
    private long _samples;
    private bool _unavailable;

    public MemoryGrowthGate(TimeSpan warmup, long limitBytes = 50_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(warmup, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limitBytes);
        _warmup = warmup;
        _limit = limitBytes;
    }

    /// <summary>One ordered sampling loop owns this gate. Null indicates an unavailable counter, not zero memory.</summary>
    public void Observe(TimeSpan elapsed, long? privateBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        if (_last is { } previous && elapsed <= previous)
        { throw new ArgumentException("Resource samples must have strictly increasing elapsed times.", nameof(elapsed)); }
        if (privateBytes < 0)
        { throw new ArgumentOutOfRangeException(nameof(privateBytes)); }
        _last = elapsed;
        if (elapsed < _warmup)
        { return; }
        if (privateBytes is not { } bytes)
        {
            _unavailable = true;
            return;
        }
        _baseline ??= bytes;
        _peakGrowth = Math.Max(_peakGrowth, bytes - _baseline.Value);
        _samples++;
    }

    public GrowthAssessment Assess(TimeSpan requiredDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(requiredDuration, _warmup);
        var status = _baseline is not null && _peakGrowth >= _limit ? GrowthStatus.Breached
            : _unavailable ? GrowthStatus.Unavailable
            : _samples < 2 || _last < requiredDuration || _last is null ? GrowthStatus.InsufficientSamples
            : GrowthStatus.Passed;
        return new GrowthAssessment(status, _baseline, _baseline is null ? null : _peakGrowth,
            _limit, _samples, _last ?? TimeSpan.Zero);
    }
}

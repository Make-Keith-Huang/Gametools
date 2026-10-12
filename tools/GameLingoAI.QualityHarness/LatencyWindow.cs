namespace GameLingoAI.QualityHarness;

public sealed record LatencySummary(long TotalCount, int WindowCount, double? P50Milliseconds,
    double? P95Milliseconds, double? MaximumMilliseconds);

/// <summary>Thread-safe bounded last-N window. Percentiles use nearest rank; absent data is null, never a false zero.</summary>
public sealed class LatencyWindow
{
    private readonly object _sync = new();
    private readonly double[] _samples;
    private long _count;

    public LatencyWindow(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 65_536);
        _samples = new double[capacity];
    }

    public void Record(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds < 0)
        { throw new ArgumentOutOfRangeException(nameof(milliseconds)); }
        lock (_sync)
        {
            _samples[_count % _samples.Length] = milliseconds;
            _count++;
        }
    }

    public LatencySummary Snapshot()
    {
        lock (_sync)
        {
            var values = _samples.AsSpan(0, (int)Math.Min(_count, _samples.Length)).ToArray();
            Array.Sort(values);
            double? Percentile(double fraction) => values.Length == 0 ? null : values[(int)Math.Ceiling(values.Length * fraction) - 1];
            return new LatencySummary(_count, values.Length, Percentile(.5), Percentile(.95),
                values.Length == 0 ? null : values[^1]);
        }
    }
}

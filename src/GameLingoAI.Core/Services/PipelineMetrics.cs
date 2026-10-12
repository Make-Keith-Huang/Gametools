using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

/// <summary>Fixed-size latency reservoirs and content-free counters; no text, pixels, endpoint or exception storage.</summary>
internal sealed class PipelineMetrics(SessionId sessionId)
{
    private readonly object _sync = new();
    private readonly double[][] _samples = Enumerable.Range(0, 6).Select(_ => new double[256]).ToArray();
    private readonly long[] _counts = new long[6];
    private CorrelationId? _correlation;
    public long DroppedFrames;
    public long DroppedText;
    public long DroppedResults;
    public long StaleResults;
    public long CacheHits;
    public long Errors;

    public void Record(PipelineStage stage, TimeSpan elapsed, PipelineMetadata metadata)
    {
        lock (_sync)
        {
            var index = (int)stage;
            _samples[index][_counts[index]++ % 256] = Math.Max(0, elapsed.TotalMilliseconds);
            _correlation = metadata.CorrelationId;
        }
    }

    public SessionMetrics Snapshot()
    {
        lock (_sync)
        {
            var stages = Enumerable.Range(0, 6).Select(index =>
            {
                var values = _samples[index].Take((int)Math.Min(_counts[index], 256)).Order().ToArray();
                double Percentile(double p) => values.Length == 0 ? 0 : values[(int)Math.Ceiling(values.Length * p) - 1];
                return new StageMeasurement((PipelineStage)index, _counts[index], Percentile(.5), Percentile(.95));
            }).ToArray();
            return new SessionMetrics(sessionId, _correlation, Interlocked.Read(ref DroppedFrames),
                Interlocked.Read(ref DroppedText), Interlocked.Read(ref DroppedResults),
                Interlocked.Read(ref StaleResults), Interlocked.Read(ref CacheHits), Interlocked.Read(ref Errors), Array.AsReadOnly(stages));
        }
    }
}

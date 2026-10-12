using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

public sealed class TemporalTextStabilizer : ITextStabilizer
{
    private readonly object _sync = new();
    private readonly IClock _clock;
    private readonly StabilizationOptions _options;
    private readonly List<Candidate> _candidates = new();
    private ScopeKey? _scope;
    private SessionId? _sessionId;
    private SequenceNumber? _lastSequence;
    private NormalizedSubtitle? _emitted;
    private int _emptyObservations;

    public TemporalTextStabilizer(
        IClock clock,
        StabilizationOptions options)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var settings = new AppSettings { Stabilization = options };
        var errors = AppSettingsValidator.Validate(settings).Errors
            .Where(error => error.Field.StartsWith("stabilization.", StringComparison.Ordinal))
            .ToArray();
        if (errors.Length > 0)
        {
            throw new ArgumentException(string.Join("; ", errors.Select(error => error.Message)), nameof(options));
        }

    }

    public int CandidateCount
    {
        get
        {
            lock (_sync)
            {
                return _candidates.Count;
            }
        }
    }

    public ValueTask<StableTextEvent?> ObserveAsync(
        OcrResult result,
        StabilizationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sessionId == result.Metadata.SessionId && _lastSequence is not null &&
                result.Metadata.Sequence <= _lastSequence.Value)
            {
                return ValueTask.FromResult<StableTextEvent?>(null);
            }

            var scope = ScopeKey.Create(context);
            if (_sessionId != result.Metadata.SessionId || _scope != scope)
            {
                ClearState();
                _sessionId = result.Metadata.SessionId;
                _scope = scope;
            }

            _lastSequence = result.Metadata.Sequence;
            var now = _clock.UtcNow;
            PruneCandidates(now);
            var normalized = OcrTextNormalizer.Normalize(result, context, _options.MaximumSpansPerObservation);
            if (normalized is null || normalized.AverageConfidence < _options.MinimumAverageConfidence)
            {
                return ValueTask.FromResult(ObserveEmpty(result.Metadata));
            }

            _emptyObservations = 0;
            if (_emitted is not null && IsDuplicate(_emitted, normalized))
            {
                _candidates.Clear();
                return ValueTask.FromResult<StableTextEvent?>(null);
            }

            var candidate = FindCandidate(normalized);
            if (candidate is null)
            {
                candidate = new Candidate(normalized, now);
                _candidates.Add(candidate);
                TrimCandidates();
            }
            else
            {
                candidate.Observe(normalized, now, _options.SimilarityThreshold);
            }

            var required = RequiredObservations(candidate);
            if (candidate.ObservationCount < required || candidate.AverageConfidence < _options.MinimumAverageConfidence)
            {
                return ValueTask.FromResult<StableTextEvent?>(null);
            }

            var kind = _emitted is null ? StableTextEventKind.New : StableTextEventKind.Changed;
            _emitted = candidate.Value;
            var textEvent = new StableTextEvent(
                result.Metadata,
                kind,
                candidate.Value.Text,
                candidate.Value.OrderedSpans,
                candidate.ObservationCount);
            _candidates.Clear();
            return ValueTask.FromResult<StableTextEvent?>(textEvent);
        }
    }

    public ValueTask ResetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ClearState();
            _sessionId = null;
            _scope = null;
        }

        return ValueTask.CompletedTask;
    }

    private StableTextEvent? ObserveEmpty(PipelineMetadata metadata)
    {
        _candidates.Clear();
        if (_emitted is null)
        {
            return null;
        }

        _emptyObservations++;
        if (_emptyObservations < _options.DisappearanceObservations)
        {
            return null;
        }

        var observations = _emptyObservations;
        _emitted = null;
        _emptyObservations = 0;
        return new StableTextEvent(
            metadata,
            StableTextEventKind.Disappeared,
            text: null,
            Array.Empty<OcrTextSpan>(),
            observations);
    }

    private Candidate? FindCandidate(NormalizedSubtitle value) => _candidates
        .Select(candidate => new
        {
            Candidate = candidate,
            Similarity = TextSimilarity(candidate.Value.Text, value.Text),
            Overlap = SpatialOverlap(candidate.Value.Bounds, value.Bounds)
        })
        .Where(match => match.Overlap >= _options.SpatialOverlapThreshold &&
            (match.Similarity >= _options.SimilarityThreshold || IsIncremental(match.Candidate.Value.Text, value.Text)))
        .OrderByDescending(match => match.Similarity + match.Overlap)
        .Select(match => match.Candidate)
        .FirstOrDefault();

    private int RequiredObservations(Candidate candidate)
    {
        if (_emitted is null || candidate.ObservationCount != 1 ||
            candidate.AverageConfidence < _options.ImmediateChangeConfidence)
        {
            return _options.RequiredObservations;
        }

        var similarity = TextSimilarity(_emitted.Text, candidate.Value.Text);
        var overlap = SpatialOverlap(_emitted.Bounds, candidate.Value.Bounds);
        return similarity < 0.5 && overlap >= _options.SpatialOverlapThreshold
            ? 1
            : _options.RequiredObservations;
    }

    private bool IsDuplicate(NormalizedSubtitle current, NormalizedSubtitle observed)
    {
        if (string.Equals(current.Text, observed.Text, StringComparison.Ordinal))
        {
            return SpatialOverlap(current.Bounds, observed.Bounds) >= _options.SpatialOverlapThreshold;
        }

        return !IsIncremental(current.Text, observed.Text) &&
            TextSimilarity(current.Text, observed.Text) >= _options.SimilarityThreshold &&
            SpatialOverlap(current.Bounds, observed.Bounds) >= _options.SpatialOverlapThreshold;
    }

    private void PruneCandidates(DateTimeOffset now)
    {
        var window = TimeSpan.FromMilliseconds(_options.ObservationWindowMilliseconds);
        _candidates.RemoveAll(candidate => now >= candidate.LastObserved && now - candidate.LastObserved > window);
    }

    private void TrimCandidates()
    {
        while (_candidates.Count > _options.MaximumCandidates)
        {
            var oldest = _candidates.MinBy(candidate => candidate.LastObserved)!;
            _candidates.Remove(oldest);
        }
    }

    private void ClearState()
    {
        _candidates.Clear();
        _emitted = null;
        _emptyObservations = 0;
        _lastSequence = null;
    }

    private static bool IsIncremental(string left, string right) =>
        left.Length != right.Length &&
        (left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal));

    internal static double TextSimilarity(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitution = previous[rightIndex - 1] + (left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1);
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    substitution);
            }

            (previous, current) = (current, previous);
        }

        return 1 - (previous[right.Length] / (double)Math.Max(left.Length, right.Length));
    }

    private static double SpatialOverlap(NormalizedRegion left, NormalizedRegion right)
    {
        var intersectionWidth = Math.Max(0, Math.Min(left.X + left.Width, right.X + right.Width) - Math.Max(left.X, right.X));
        var intersectionHeight = Math.Max(0, Math.Min(left.Y + left.Height, right.Y + right.Height) - Math.Max(left.Y, right.Y));
        var intersection = intersectionWidth * intersectionHeight;
        var union = (left.Width * left.Height) + (right.Width * right.Height) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    private sealed class Candidate
    {
        private double _confidenceTotal;

        public Candidate(NormalizedSubtitle value, DateTimeOffset observedAt)
        {
            Value = value;
            LastObserved = observedAt;
            ObservationCount = 1;
            _confidenceTotal = value.AverageConfidence;
        }

        public NormalizedSubtitle Value { get; private set; }

        public DateTimeOffset LastObserved { get; private set; }

        public int ObservationCount { get; private set; }

        public double AverageConfidence => _confidenceTotal / ObservationCount;

        public void Observe(NormalizedSubtitle value, DateTimeOffset observedAt, double similarityThreshold)
        {
            var similarity = TextSimilarity(Value.Text, value.Text);
            if (IsIncremental(Value.Text, value.Text) || similarity < similarityThreshold)
            {
                Value = value;
                ObservationCount = 1;
                _confidenceTotal = value.AverageConfidence;
            }
            else
            {
                ObservationCount++;
                _confidenceTotal += value.AverageConfidence;
                if (value.AverageConfidence >= Value.AverageConfidence)
                {
                    Value = value;
                }
            }

            LastObserved = observedAt;
        }
    }

    private sealed record ScopeKey(
        ProfileId ProfileId,
        string Language,
        string RegionOrder,
        string Replacements)
    {
        public static ScopeKey Create(StabilizationContext context) => new(
            context.ProfileId,
            context.Language.ToUpperInvariant(),
            string.Join('|', context.RegionOrder),
            string.Join('|', context.ArtifactReplacements.Select(rule =>
                $"{rule.Artifact.Length}:{rule.Artifact}{rule.Replacement.Length}:{rule.Replacement}")));
    }
}

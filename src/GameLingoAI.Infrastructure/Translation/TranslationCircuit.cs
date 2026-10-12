using GameLingoAI.Core.Abstractions;

namespace GameLingoAI.Infrastructure.Translation;

/// <summary>Per-adapter circuit; old in-flight results cannot close a newer outage.</summary>
internal sealed class TranslationCircuit(IClock clock, TranslationHttpPolicy policy)
{
    private readonly object _gate = new();
    private int _generation;
    private int _failures;
    private DateTimeOffset? _retryAt;
    private bool _probeInFlight;

    public int? TryEnter()
    {
        lock (_gate)
        {
            if (_retryAt is { } retryAt)
            {
                if (clock.UtcNow < retryAt || _probeInFlight)
                { return null; }
                _probeInFlight = true;
            }
            return _generation;
        }
    }

    public bool IsCurrent(int generation)
    {
        lock (_gate)
        { return generation == _generation; }
    }

    // Null means cancellation/exception, not evidence that the provider failed.
    public void Complete(int generation, bool? transientFailure)
    {
        lock (_gate)
        {
            if (generation != _generation)
            { return; }
            if (transientFailure is null)
            {
                _probeInFlight = false;
                return;
            }
            if (transientFailure == false)
            {
                _failures = 0;
                if (_retryAt is not null)
                { _generation++; }
                _retryAt = null;
                _probeInFlight = false;
                return;
            }
            _failures++;
            if (_retryAt is not null || _failures >= policy.CircuitFailureThreshold)
            {
                _generation++;
                _retryAt = clock.UtcNow.AddMilliseconds(policy.CircuitBreakMilliseconds);
                _probeInFlight = false;
            }
        }
    }
}

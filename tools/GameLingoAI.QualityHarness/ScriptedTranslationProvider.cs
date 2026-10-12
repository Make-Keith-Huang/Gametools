using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.QualityHarness;

public enum SimulatedOutcome { Success, RateLimited, ServerError, Disconnect, PacketLoss, AuthenticationRejected }

public sealed record ProviderStep(TimeSpan Latency, SimulatedOutcome Outcome);

/// <summary>Finite, copied script, repeated cyclically. Injected delay supports virtual-time tests without real sleeps.</summary>
public sealed class ScriptedTranslationProvider : ITranslationProvider
{
    private readonly ProviderStep[] _steps;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private long _calls;

    public ScriptedTranslationProvider(IReadOnlyList<ProviderStep> steps,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentOutOfRangeException.ThrowIfLessThan(steps.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(steps.Count, 1024);
        _steps = steps.ToArray();
        foreach (var step in _steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentOutOfRangeException.ThrowIfLessThan(step.Latency, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(step.Latency, TimeSpan.FromSeconds(30));
            if (!Enum.IsDefined(step.Outcome))
            { throw new ArgumentOutOfRangeException(nameof(steps)); }
        }
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public string Id => "synthetic-script";
    public long Calls => Interlocked.Read(ref _calls);

    public async Task<OperationResult<TranslationResult>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var ordinal = Interlocked.Increment(ref _calls) - 1;
        var step = _steps[(int)(ordinal % _steps.Length)];
        await _delay(step.Latency, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (step.Outcome == SimulatedOutcome.Success)
        {
            return OperationResult.Success(new TranslationResult(request.Metadata, request.Text, "Synthetic translation",
                Id, "fixture-v1", step.Latency, false, true));
        }
        var code = step.Outcome switch
        {
            SimulatedOutcome.RateLimited => FailureCode.RateLimited,
            SimulatedOutcome.PacketLoss => FailureCode.Timeout,
            SimulatedOutcome.AuthenticationRejected => FailureCode.AuthenticationRejected,
            _ => FailureCode.ProviderUnavailable
        };
        return OperationResult.Failed<TranslationResult>(new PipelineFailure(code, "Synthetic provider scenario failed.",
            request.Metadata.CorrelationId, step.Outcome != SimulatedOutcome.AuthenticationRejected));
    }
}

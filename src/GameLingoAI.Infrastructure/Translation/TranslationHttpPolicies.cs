using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Translation;

public sealed record TranslationHttpPolicy
{
    public int MaximumConcurrentRequests { get; init; } = 2;

    public int MinimumRequestIntervalMilliseconds { get; init; } = 200;

    public int TimeoutMilliseconds { get; init; } = 30_000;

    public int MaximumRetries { get; init; } = 2;

    public int CircuitFailureThreshold { get; init; } = 3;

    public int CircuitBreakMilliseconds { get; init; } = 10_000;

    public int MaximumResponseBytes { get; init; } = 64 * 1024;

    public int MaximumOutputCharacters { get; init; } = 4_000;

    public int MaximumOutputLines { get; init; } = 8;

    public void Validate()
    {
        if (MaximumConcurrentRequests is < 1 or > 16 ||
            MinimumRequestIntervalMilliseconds is < 0 or > 60_000 ||
            TimeoutMilliseconds is < 100 or > 120_000 ||
            MaximumRetries is < 0 or > 5 ||
            CircuitFailureThreshold is < 1 or > 100 ||
            CircuitBreakMilliseconds is < 100 or > 60_000 ||
            MaximumResponseBytes is < 256 or > 1_048_576 ||
            MaximumOutputCharacters is < 1 or > 16_000 ||
            MaximumOutputLines is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(TranslationHttpPolicy),
                "Translation HTTP policy exceeds its safe bounds.");
        }
    }
}

public interface ITranslationDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemTranslationDelay : ITranslationDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IRetryJitter
{
    /// <summary>Returns a value in [0, 1] for bounded retry backoff.</summary>
    double NextUnitInterval();
}

public sealed class RandomRetryJitter : IRetryJitter
{
    public double NextUnitInterval() => Random.Shared.NextDouble();
}

/// <summary>Only text-domain messages cross this boundary; protocol DTOs stay in Infrastructure.</summary>
public interface ITranslationHttpProtocol
{
    string Id { get; }

    Uri BaseUri { get; }

    TranslationProviderCapabilities Capabilities { get; }

    IReadOnlySet<string> AcceptedResponseMediaTypes { get; }

    HttpRequestMessage CreateRequest(TranslationRequest request);

    HttpRequestMessage? CreateHealthRequest();

    TranslationHttpPayload ParseResponse(ReadOnlyMemory<byte> response);
}

public sealed record TranslationHttpPayload(string Text, TranslationUsage? Usage = null);

public sealed class ConfigurationRemoteTextConsent : IRemoteTextConsent
{
    private readonly IConfigurationStore _configuration;

    public ConfigurationRemoteTextConsent(IConfigurationStore configuration) =>
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public async ValueTask<bool> IsGrantedAsync(CancellationToken cancellationToken)
    {
        var settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
        return settings.IsSuccess && settings.Value?.Privacy.RemoteTextProcessingConsent == true;
    }
}

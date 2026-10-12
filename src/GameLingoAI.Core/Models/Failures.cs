namespace GameLingoAI.Core.Models;

public enum FailureCode
{
    Validation,
    Cancelled,
    TargetUnavailable,
    ModelMissing,
    ModelCorrupt,
    UnsupportedPlatform,
    UnsupportedLanguage,
    ProviderUnavailable,
    AuthenticationRejected,
    RateLimited,
    Timeout,
    ResponseTooLarge,
    ResponseMalformed,
    ConsentRequired,
    PersistenceUnavailable,
    Unexpected
}

/// <summary>A safe failure description. It must never contain secrets or raw captured pixels.</summary>
public sealed record PipelineFailure
{
    public PipelineFailure(
        FailureCode code,
        string message,
        CorrelationId correlationId,
        bool isTransient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        Code = code;
        Message = message;
        CorrelationId = correlationId;
        IsTransient = isTransient;
    }

    public FailureCode Code { get; }

    public string Message { get; }

    public CorrelationId CorrelationId { get; }

    public bool IsTransient { get; }
}

public readonly record struct OperationResult<T>
{
    internal OperationResult(T? value, PipelineFailure? failure)
    {
        Value = value;
        Failure = failure;
    }

    public T? Value { get; }

    public PipelineFailure? Failure { get; }

    public bool IsSuccess => Failure is null;
}

public static class OperationResult
{
    public static OperationResult<T> Success<T>(T value) =>
        new(value ?? throw new ArgumentNullException(nameof(value)), null);

    public static OperationResult<T> Failed<T>(PipelineFailure failure) =>
        new(default, failure ?? throw new ArgumentNullException(nameof(failure)));
}

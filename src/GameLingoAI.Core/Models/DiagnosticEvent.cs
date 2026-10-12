namespace GameLingoAI.Core.Models;

public enum DiagnosticEventKind
{
    Unspecified,
    ApplicationStarted,
    SessionStarting,
    SessionRunning,
    SessionPaused,
    SessionStopped,
    StageFailed,
    StageCompleted,
    CommandFailed,
    StorageUnavailable,
    SupportBundleExported
}

/// <summary>Content-free diagnostic context. Never add text, URLs, configurations or exceptions here.</summary>
public sealed record DiagnosticEvent(DiagnosticEventKind Kind, SessionId? SessionId = null,
    CorrelationId? CorrelationId = null, PipelineStage? Stage = null, FailureCode? ErrorCategory = null);

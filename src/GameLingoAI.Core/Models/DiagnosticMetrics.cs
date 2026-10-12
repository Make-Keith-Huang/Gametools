namespace GameLingoAI.Core.Models;

/// <summary>Only aggregate counters for this application process; null means unavailable, never zero by assumption.</summary>
public sealed record ResourceMetrics(long? PrivateBytes, long? WorkingSetBytes, long ManagedBytes,
    long TotalAllocatedBytes, double? TotalCpuMilliseconds, int? HandleCount);

public sealed record DiagnosticMetrics(DateTimeOffset CapturedAtUtc, SessionMetrics Session,
    ResourceMetrics Resources, long DroppedLogEvents, long LoggingFailures);

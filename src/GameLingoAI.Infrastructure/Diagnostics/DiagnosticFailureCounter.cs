using Serilog.Core;
using Serilog.Events;

namespace GameLingoAI.Infrastructure.Diagnostics;

/// <summary>Counts notifications only; never retains or renders failure payloads.</summary>
internal sealed class DiagnosticFailureCounter : ILoggingFailureListener
{
    private long _count;
    public long Count => Interlocked.Read(ref _count);

    public void OnLoggingFailed(object sender, LoggingFailureKind kind, string message,
        IReadOnlyCollection<LogEvent>? events, Exception? exception) => Interlocked.Increment(ref _count);
}

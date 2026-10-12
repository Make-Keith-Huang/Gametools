using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;

namespace GameLingoAI.Infrastructure.Diagnostics;

/// <summary>Creates private Serilog destinations; callers receive only the content-free writer.</summary>
public static class DiagnosticWriters
{
    public const long DefaultFileSizeBytes = 1024 * 1024;

    public static BufferedDiagnosticWriter CreateProduction(string? directory = null,
        int retainedFileCount = 7, long fileSizeBytes = DefaultFileSizeBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFileCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(retainedFileCount, 31);
        ArgumentOutOfRangeException.ThrowIfLessThan(fileSizeBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fileSizeBytes, 16 * DefaultFileSizeBytes);
        directory ??= Configuration.AppDataPaths.Logs;
        var path = Path.Combine(Path.GetFullPath(directory), "diagnostic-.jsonl");
        var failures = new DiagnosticFailureCounter();
        return new BufferedDiagnosticWriter(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return new LoggerSink(new LoggerConfiguration().WriteTo.Fallible(sinks => sinks.File(new JsonFormatter(), path,
                rollingInterval: RollingInterval.Day, fileSizeLimitBytes: fileSizeBytes,
                rollOnFileSizeLimit: true, retainedFileCountLimit: retainedFileCount,
                buffered: false, shared: false), failures).CreateLogger());
        }, failures);
    }

    public static BufferedDiagnosticWriter CreateDevelopment()
    {
        var failures = new DiagnosticFailureCounter();
        return new BufferedDiagnosticWriter(() => new LoggerSink(new LoggerConfiguration()
            .WriteTo.Fallible(sinks => sinks.Console(new JsonFormatter()), failures)
            .WriteTo.Fallible(sinks => sinks.Debug(new JsonFormatter()), failures)
            .CreateLogger()), failures);
    }

    private sealed class LoggerSink(Logger logger) : ILogEventSink, IDisposable
    {
        public void Emit(LogEvent logEvent) => logger.Write(logEvent);
        public void Dispose() => logger.Dispose();
    }
}

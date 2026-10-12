using GameLingoAI.Core.Models;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace GameLingoAI.Infrastructure.Diagnostics;

/// <summary>
/// A terminal privacy boundary before ANY sink: rebuild an allowlisted event, never render or traverse input content.
/// Dropping all free text also removes unknown/encoded secrets, headers, URLs and exception data without keeping secrets in memory.
/// </summary>
public sealed class RedactingDiagnosticSink(ILogEventSink destination) : ILogEventSink
{
    private static readonly MessageTemplate Template = new MessageTemplateParser().Parse("Diagnostic {Kind}");
    private long _failures;

    public long Failures => Interlocked.Read(ref _failures);

    public void Emit(LogEvent logEvent)
    {
        try
        { destination.Emit(Sanitize(logEvent)); }
        catch (Exception)
        {
            // Do not send sink failures to another logger: that can recurse or disclose a private path.
            Interlocked.Increment(ref _failures);
        }
    }

    public static LogEvent Sanitize(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        var kind = ReadEnum<DiagnosticEventKind>(logEvent, "Kind") ?? DiagnosticEventKind.Unspecified;
        var properties = new List<LogEventProperty> { new("Kind", new ScalarValue(kind.ToString())) };
        CopyGuid(logEvent, properties, "SessionId");
        CopyGuid(logEvent, properties, "CorrelationId");
        CopyEnum<PipelineStage>(logEvent, properties, "Stage");
        CopyEnum<FailureCode>(logEvent, properties, "ErrorCategory");
        var level = Enum.IsDefined(logEvent.Level) ? logEvent.Level : LogEventLevel.Warning;
        // Intentionally omit exception, message template, arbitrary properties, trace/span IDs and destructured data.
        return new LogEvent(logEvent.Timestamp, level, null, Template, properties);
    }

    private static T? ReadEnum<T>(LogEvent entry, string name) where T : struct, Enum =>
        entry.Properties.TryGetValue(name, out var property) && property is ScalarValue { Value: T value } && Enum.IsDefined(value)
            ? value : null;

    private static void CopyEnum<T>(LogEvent entry, List<LogEventProperty> output, string name) where T : struct, Enum
    {
        if (ReadEnum<T>(entry, name) is { } value)
        { output.Add(new(name, new ScalarValue(value.ToString()))); }
    }

    private static void CopyGuid(LogEvent entry, List<LogEventProperty> output, string name)
    {
        if (entry.Properties.TryGetValue(name, out var property) && property is ScalarValue { Value: Guid value } && value != Guid.Empty)
        { output.Add(new(name, new ScalarValue(value))); }
    }
}

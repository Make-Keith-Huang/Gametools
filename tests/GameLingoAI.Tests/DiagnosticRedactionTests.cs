using System.Globalization;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Json;
using Serilog.Parsing;

internal static class DiagnosticRedactionTests
{
    public static Task RunAsync()
    {
        var capture = new CaptureSink();
        var sink = new RedactingDiagnosticSink(capture);
        var session = Guid.NewGuid();
        var correlation = Guid.NewGuid();
        var random = new Random(22001);
        var adversarialEvents = new List<LogEvent>();
        for (var index = 0; index < 500; index++)
        {
            var secret = "private-" + random.NextInt64().ToString(CultureInfo.InvariantCulture) + "-字幕";
            var exception = new InvalidOperationException("Bearer " + secret, new IOException(secret));
            exception.Data[secret] = new DangerousValue();
            var entry = new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Error, exception,
                new MessageTemplateParser().Parse(secret + " {@Provider} {Authorization}"),
                [new("Kind", new ScalarValue(DiagnosticEventKind.StageFailed)),
                 new("SessionId", new ScalarValue(session)), new("CorrelationId", new ScalarValue(correlation)),
                 new("Stage", new ScalarValue(PipelineStage.Translation)), new("ErrorCategory", new ScalarValue(FailureCode.ProviderUnavailable)),
                 new("Authorization", new ScalarValue("Bearer " + secret)),
                 new("Provider", new StructureValue([new("Url", new ScalarValue("https://user:" + secret + "@example.invalid/?key=" + Uri.EscapeDataString(secret)))])),
                 new("RecognizedText", new ScalarValue(secret)), new("TranslatedText", new ScalarValue(secret)),
                 new(secret, new ScalarValue(new DangerousValue()))]);
            sink.Emit(entry);
            adversarialEvents.Add(entry);
            var safe = capture.Last!;
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            new JsonFormatter().Format(safe, output);
            var json = output.ToString();
            Assert(!json.Contains(secret, StringComparison.Ordinal) && !json.Contains("https", StringComparison.Ordinal) &&
                !json.Contains("Bearer", StringComparison.Ordinal) && safe.Exception is null && safe.Properties.Count == 5,
                "Sensitive data escaped the central diagnostic projection.");
            Assert(json.Contains(session.ToString(), StringComparison.Ordinal) && json.Contains(correlation.ToString(), StringComparison.Ordinal) &&
                json.Contains("StageFailed", StringComparison.Ordinal) && json.Contains("Translation", StringComparison.Ordinal),
                "Safe correlation/stage context was lost.");
        }
        var hostile = new LogEvent(DateTimeOffset.UnixEpoch, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("raw secret"),
            [new("Kind", new ScalarValue("StageFailed-secret")), new("Stage", new ScalarValue((PipelineStage)int.MaxValue)),
             new("SessionId", new ScalarValue("secret-in-identifier")), new("CorrelationId", new ScalarValue(Guid.Empty))]);
        sink.Emit(hostile);
        Assert(capture.Last!.Properties.Count == 1 && capture.Last.RenderMessage(CultureInfo.InvariantCulture).Contains("Unspecified", StringComparison.Ordinal),
            "Stringly-typed or invalid diagnostic metadata was accepted.");
        var failing = new RedactingDiagnosticSink(new ThrowingSink());
        Parallel.For(0, 1_000, _ => failing.Emit(hostile));
        Assert(failing.Failures == 1_000, "Concurrent sink failures escaped or were miscounted.");
        Assert(sink.Failures == 0, "Redaction invoked hostile content or unexpectedly failed.");
        CheckRealDestinations(adversarialEvents, correlation);
        Console.WriteLine("Diagnostic redaction: 500 adversarial events, content/property/exception exclusion, typed correlation and 1,000 concurrent contained sink failures passed.");
        return Task.CompletedTask;
    }

    private static void CheckRealDestinations(List<LogEvent> entries, Guid correlation)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-sink-privacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "events.jsonl");
        var original = Console.Out;
        using var console = new StringWriter(CultureInfo.InvariantCulture);
        var debug = new ObservingDebugFormatter();
        try
        {
            Console.SetOut(console);
            using (var logger = new LoggerConfiguration()
                .WriteTo.Console(new JsonFormatter())
                .WriteTo.Debug(debug)
                .WriteTo.File(new JsonFormatter(), path)
                .CreateLogger())
            {
                var boundary = new RedactingDiagnosticSink(new LoggerDestination(logger));
                foreach (var entry in entries)
                { boundary.Emit(entry); }
                Assert(boundary.Failures == 0, "Real destination redaction failed.");
            }
            var disk = File.ReadAllText(path);
            var consoleText = console.ToString();
            var debugText = string.Concat(debug.Output);
            Assert(debug.Output.Count == entries.Count, "The real Debug sink did not format every event.");
            Assert(consoleText == disk && debugText == disk,
                "Console, File and Debug destination payloads differ.");
            Assert(disk.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == entries.Count &&
                disk.Contains(correlation.ToString(), StringComparison.Ordinal), "Destination events/correlation were lost.");
            Assert(!disk.Contains("private-", StringComparison.Ordinal) &&
                !disk.Contains("Bearer", StringComparison.Ordinal) && !disk.Contains("https", StringComparison.Ordinal) &&
                !disk.Contains("Exception", StringComparison.Ordinal) && !disk.Contains("字幕", StringComparison.Ordinal),
                "Adversarial content reached a real destination.");
        }
        finally
        {
            Console.SetOut(original);
            Directory.Delete(directory, true);
        }
        Console.WriteLine("Sink privacy: 500 hostile events yielded identical safe Console/File/Debug payloads.");
    }

    // The actual Serilog Debug sink supplies its StringWriter here, then passes these bytes to Debug.Write.
    // This observes its public formatter boundary, not a debugger UI or another process's memory.
    private sealed class ObservingDebugFormatter : ITextFormatter
    {
        private readonly JsonFormatter _json = new();
        public List<string> Output { get; } = new();

        public void Format(LogEvent logEvent, TextWriter output)
        {
            _json.Format(logEvent, output);
            if (output is not StringWriter buffer)
            { throw new InvalidOperationException("Unexpected Debug sink output buffer."); }
            Output.Add(buffer.ToString());
        }
    }

    private sealed class LoggerDestination(Logger logger) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => logger.Write(logEvent);
    }

    private static void Assert(bool value, string message)
    { if (!value) { throw new InvalidOperationException(message); } }
    private sealed class CaptureSink : ILogEventSink
    {
        public LogEvent? Last;
        public void Emit(LogEvent logEvent) => Last = logEvent;
    }
    private sealed class ThrowingSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => throw new IOException("SECRET sink path");
    }
    private sealed class DangerousValue
    {
        public override string ToString() => throw new InvalidOperationException("Do not render untrusted properties.");
    }
}

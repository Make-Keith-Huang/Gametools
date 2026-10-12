using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;
using Serilog.Core;
using Serilog.Events;

internal static class BufferedDiagnosticTests
{
    public static async Task RunAsync()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sink = new SlowSink(entered, release);
        var writer = new BufferedDiagnosticWriter(() => sink, 2, 2);
        var entry = new DiagnosticEvent(DiagnosticEventKind.SessionRunning, SessionId.New(), CorrelationId.New());
        try
        {
            Assert(writer.TryWrite(entry), "First event rejected.");
            Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Worker did not enter sink.");
            Assert(writer.TryWrite(entry) && writer.TryWrite(entry), "Available queue capacity rejected.");
            Parallel.For(0, 1000, _ => Assert(!writer.TryWrite(entry), "Blocked sink queue exceeded its bound."));
            Assert(writer.Dropped == 1000, "Concurrent drop accounting failed.");
        }
        finally { release.Set(); await writer.DisposeAsync(); }
        await writer.DisposeAsync();
        Assert(sink.Count == 3 && sink.Disposals == 1, "Drain/disposal ownership failed.");
        Assert(writer.SnapshotRecent().Count == 2, "Recent events exceeded retention.");
        Assert(!writer.TryWrite(entry) && writer.Dropped == 1001, "Post-disposal admission succeeded.");
        Assert(writer.SnapshotRecent().All(json => json.Contains(entry.CorrelationId!.Value.Value.ToString(), StringComparison.Ordinal)),
            "Correlation lost during buffering.");

        var startupFailure = new BufferedDiagnosticWriter(() => throw new IOException("SECRET startup"));
        startupFailure.TryWrite(entry);
        await startupFailure.DisposeAsync();
        Assert(startupFailure.Failures == 1 && startupFailure.SnapshotRecent().Count == 1, "Startup failure lost safe recent diagnostics.");
        var failed = new BufferedDiagnosticWriter(() => new FailingSink());
        failed.TryWrite(entry);
        await failed.DisposeAsync();
        Assert(failed.Failures == 2 && !string.Join("", failed.SnapshotRecent()).Contains("SECRET", StringComparison.Ordinal),
            "Sink or disposal failure escaped containment.");
        Console.WriteLine("Buffered diagnostics: bounded overload, concurrent drops, correlation, drain, repeated disposal and startup/write/disposal failure isolation passed.");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class SlowSink(ManualResetEventSlim entered, ManualResetEventSlim release) : ILogEventSink, IDisposable
    {
        public int Count;
        public int Disposals;
        public void Emit(LogEvent logEvent)
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
            { throw new TimeoutException(); }
            Count++;
        }
        public void Dispose() => Disposals++;
    }

    private sealed class FailingSink : ILogEventSink, IDisposable
    {
        public void Emit(LogEvent logEvent) => throw new IOException("SECRET write");
        public void Dispose() => throw new IOException("SECRET dispose");
    }
}

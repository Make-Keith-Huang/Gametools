using GameLingoAI.App.Services;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;
using Serilog.Core;
using Serilog.Events;

internal static class ApplicationDiagnosticsTests
{
    public static async Task RunAsync()
    {
        await ConfigurationAsync();
        var diagnostics = new ApplicationDiagnostics();
        var sink = new CaptureSink();
        var starts = 0;
        BufferedDiagnosticWriter Factory()
        { starts++; return new(() => sink); }
        Assert(!diagnostics.TryWrite(new(DiagnosticEventKind.CommandFailed)), "Unstarted app admitted logs.");
        diagnostics.Start(Factory);
        diagnostics.Start(Factory);
        var reporter = new ErrorReporter(diagnostics);
        string? userMessage = null;
        reporter.ErrorReported += (_, message) => userMessage = message;
        reporter.Report(new IOException("SECRET app exception"));
        await Task.WhenAll(diagnostics.ShutdownAsync(), diagnostics.ShutdownAsync());
        diagnostics.RequestShutdown();
        diagnostics.Start(Factory);
        Assert(starts == 1 && sink.Count == 2 && sink.Disposals == 1, "Application diagnostics lifetime ownership failed.");
        Assert(!diagnostics.TryWrite(new(DiagnosticEventKind.SessionRunning)), "Closed application admitted events.");
        Assert(diagnostics.SnapshotRecent().Count == 2 && diagnostics.Failures == 0 &&
            !string.Join("", diagnostics.SnapshotRecent()).Contains("SECRET", StringComparison.Ordinal) &&
            userMessage is not null && !userMessage.Contains("SECRET", StringComparison.Ordinal), "Application error leaked content or lost events.");
        var closed = new ApplicationDiagnostics();
        await closed.ShutdownAsync();
        closed.Start(Factory);
        Assert(starts == 1, "Late Loaded started logging after shutdown.");
        var failed = new ApplicationDiagnostics();
        failed.Start(() => throw new IOException("SECRET startup path"));
        await failed.ShutdownAsync();
        Assert(failed.Failures == 1 && failed.SnapshotRecent().Count == 0, "Logger creation failure was not isolated.");
        Console.WriteLine("Application diagnostics: lazy start, single ownership, command-error privacy, drain, repeated/early shutdown and failed creation passed.");
    }

    private static void Assert(bool value, string message)
    { if (!value) { throw new InvalidOperationException(message); } }

    private static async Task ConfigurationAsync()
    {
        foreach (var count in new[] { 1, 12, 30, 0, 31 })
        {
            var owner = new ApplicationDiagnostics();
            var selected = 0;
            await owner.InitializeAsync(new Configuration(count), value =>
            { selected = value; return new(() => new CaptureSink()); }, CancellationToken.None);
            await owner.ShutdownAsync();
            Assert(selected == (count is >= 1 and <= 30 ? count : 7), "Invalid retention selection.");
            Assert(owner.Failures == (count is >= 1 and <= 30 ? 0 : 1), "Invalid retention not reported.");
        }
        var delayed = new Configuration(12) { Delay = true };
        var closed = new ApplicationDiagnostics();
        using var cancellation = new CancellationTokenSource();
        var initialization = closed.InitializeAsync(delayed, _ => throw new InvalidOperationException("Late logger start"), cancellation.Token);
        await delayed.Entered.Task;
        cancellation.Cancel();
        try
        { await initialization; throw new InvalidOperationException("Expected initialization cancellation."); }
        catch (OperationCanceledException) { }
        await closed.ShutdownAsync();
        Assert(closed.SnapshotRecent().Count == 0 && closed.Failures == 0, "Cancelled initialization created diagnostics.");
    }

    private sealed class Configuration(int count) : GameLingoAI.Core.Abstractions.IConfigurationStore
    {
        public bool Delay;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OperationResult<GameLingoAI.Core.Configuration.AppSettings>> LoadAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            if (Delay)
            { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            token.ThrowIfCancellationRequested();
            return OperationResult.Success(new GameLingoAI.Core.Configuration.AppSettings
            { Logging = new() { RetainedFileCount = count, IncludeRecognizedText = true } });
        }
        public Task SaveAsync(GameLingoAI.Core.Configuration.AppSettings settings, CancellationToken token) => throw new NotSupportedException();
        public Task<string> ExportSanitizedAsync(GameLingoAI.Core.Configuration.AppSettings settings, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class CaptureSink : ILogEventSink, IDisposable
    {
        public int Count;
        public int Disposals;
        public void Emit(LogEvent logEvent) => Count++;
        public void Dispose() => Disposals++;
    }
}

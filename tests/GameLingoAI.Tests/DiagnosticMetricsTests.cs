using GameLingoAI.App.Services;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;

internal static class DiagnosticMetricsTests
{
    public static async Task RunAsync()
    {
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => CurrentProcessMetrics.SampleAsync(CancellationToken.None)));
        foreach (var value in snapshots)
        {
            Assert(value.PrivateBytes is null or >= 0 && value.WorkingSetBytes is null or >= 0 &&
                value.ManagedBytes >= 0 && value.TotalAllocatedBytes >= 0 && value.TotalCpuMilliseconds is null or >= 0 &&
                value.HandleCount is null or >= 0, "Resource snapshot contains an invalid counter.");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        { await CurrentProcessMetrics.SampleAsync(cancellation.Token); throw new InvalidOperationException("Cancelled resource sampling ran."); }
        catch (OperationCanceledException) { }
        var session = new SessionMetrics(SessionId.New(), CorrelationId.New(), 1, 2, 3, 4, 5, 6,
            Array.AsReadOnly(new[] { new StageMeasurement(PipelineStage.Ocr, 20, 40, 80) }));
        var diagnostics = new ApplicationDiagnostics();
        var snapshot = await diagnostics.SnapshotMetricsAsync(session, CancellationToken.None);
        Assert(snapshot.Session == session && snapshot.LoggingFailures == 0 && snapshot.DroppedLogEvents == 0 &&
            snapshot.CapturedAtUtc.Offset == TimeSpan.Zero, "Diagnostic metrics lost counters, stages or UTC timestamp.");
        await diagnostics.ShutdownAsync();
        Console.WriteLine("Diagnostic metrics: concurrent own-process sampling, cancellation and aggregate snapshot composition passed.");
    }

    private static void Assert(bool value, string message)
    { if (!value) { throw new InvalidOperationException(message); } }
}

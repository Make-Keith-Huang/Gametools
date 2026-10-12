using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;

internal static class DiagnosticFileTests
{
    public static async Task RunAsync()
    {
        await CheckDevelopmentOutputAsync();
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unrelated = Path.Combine(directory, "unrelated.txt");
            await File.WriteAllTextAsync(unrelated, "preserve");
            var session = SessionId.New();
            var correlation = CorrelationId.New();
            var entry = new DiagnosticEvent(DiagnosticEventKind.StageFailed, session, correlation,
                PipelineStage.Translation, FailureCode.ProviderUnavailable);
            for (var restart = 0; restart < 3; restart++)
            {
                await using (var writer = DiagnosticWriters.CreateProduction(directory, 3, 1024))
                {
                    for (var index = 0; index < 200; index++)
                    { writer.TryWrite(entry); }
                }
                var files = Directory.GetFiles(directory, "diagnostic-*.jsonl");
                Assert(files.Length == 3, "Rolling file count was not retained across restarts.");
                foreach (var file in files)
                {
                    // The file sink checks size before a write: the final bounded event may cross the threshold.
                    Assert(new FileInfo(file).Length <= 2048, "Rolling size exceeded threshold plus one bounded event.");
                    foreach (var line in await File.ReadAllLinesAsync(file))
                    {
                        Assert(line.Length < 1024 && line.Contains(correlation.Value.ToString(), StringComparison.Ordinal),
                            "Unexpected event size or missing correlation.");
                        Assert(!line.Contains("Exception", StringComparison.Ordinal), "Unexpected exception detail in production log.");
                    }
                }
            }
            Assert(await File.ReadAllTextAsync(unrelated) == "preserve", "Retention touched an unrelated file.");
            var failed = DiagnosticWriters.CreateProduction(unrelated);
            failed.TryWrite(entry);
            await failed.DisposeAsync();
            Assert(failed.Failures == 1 && failed.SnapshotRecent().Count == 1,
                "Unavailable log directory escaped failure isolation or disabled in-memory diagnostics.");
            var lockedDirectory = Path.Combine(directory, "locked-retention");
            Directory.CreateDirectory(lockedDirectory);
            var lockedPath = Path.Combine(lockedDirectory, "diagnostic-20000101.jsonl");
            await File.WriteAllTextAsync(lockedPath, "synthetic locked log");
            await using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var retention = DiagnosticWriters.CreateProduction(lockedDirectory, 1, 1024);
                for (var index = 0; index < 100; index++)
                { retention.TryWrite(entry); }
                await retention.DisposeAsync();
                Assert(retention.Failures > 0, "Locked retention deletion was not reported as degraded logging.");
                Assert(retention.SnapshotRecent().Count > 0, "Retention failure disabled recent diagnostics.");
                Assert(Directory.GetFiles(lockedDirectory, "diagnostic-*.jsonl").Length <= 2,
                    "Repeated retention failure caused unbounded rolling files.");
            }
            await using (var recovered = DiagnosticWriters.CreateProduction(lockedDirectory, 1, 1024))
            { recovered.TryWrite(entry); }
            Assert(Directory.GetFiles(lockedDirectory, "diagnostic-*.jsonl").Length == 1,
                "Retention did not recover after releasing the locked file.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("Diagnostic files: size/count rolling, three reopen cycles, correlation, unrelated-file preservation and unavailable directory passed.");
    }

    private static async Task CheckDevelopmentOutputAsync()
    {
        var original = Console.Out;
        using var output = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var correlation = CorrelationId.New();
        try
        {
            Console.SetOut(output);
            var writer = DiagnosticWriters.CreateDevelopment();
            writer.TryWrite(new DiagnosticEvent(DiagnosticEventKind.SessionRunning, CorrelationId: correlation));
            await writer.DisposeAsync();
            Assert(writer.Failures == 0 && output.ToString().Contains(correlation.Value.ToString(), StringComparison.Ordinal),
                "Development Console sink lost safe correlated output.");
            using var failingOutput = new FailingWriter();
            Console.SetOut(failingOutput);
            var failed = DiagnosticWriters.CreateDevelopment();
            failed.TryWrite(new DiagnosticEvent(DiagnosticEventKind.CommandFailed));
            await failed.DisposeAsync();
            Assert(failed.Failures > 0 && failed.SnapshotRecent().Count == 1,
                "Serilog-swallowed Console failure was not counted or lost recent events.");
            Assert(!string.Join("", failed.SnapshotRecent()).Contains("SECRET", StringComparison.Ordinal),
                "Failure listener leaked exception content.");
        }
        finally { Console.SetOut(original); }
    }

    private sealed class FailingWriter : StringWriter
    {
        public override void Write(string? value) => throw new IOException("SECRET console failure");
        public override void Write(char value) => throw new IOException("SECRET console failure");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

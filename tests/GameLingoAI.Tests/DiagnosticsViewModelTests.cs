using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Models;

internal static class DiagnosticsViewModelTests
{
    public static async Task RunAsync()
    {
        var snapshot = new DiagnosticMetrics(DateTimeOffset.UnixEpoch,
            new(null, null, 1, 2, 3, 4, 5, 6, []), new(null, null, 10, 20, null, null), 7, 8);
        var reporter = new ErrorReporter();
        var failures = 0;
        reporter.ErrorReported += (_, _) => failures++;
        var fail = false;
        using var view = new DiagnosticsViewModel(_ => fail ? throw new IOException("SECRET sampling") : Task.FromResult(snapshot),
            () => ["{\"Kind\":\"ApplicationStarted\"}"], reporter);
        await view.RefreshCommand.ExecuteAsync();
        Assert(view.MetricsJson.Contains("WorkingSetBytes", StringComparison.Ordinal) &&
            view.MetricsJson.Contains("null", StringComparison.Ordinal) && view.EventsJson.Contains("ApplicationStarted", StringComparison.Ordinal),
            "Diagnostic page did not expose the safe snapshot.");
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-diagnostic-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            view.ExportPath = Path.Combine(directory, "bundle.zip");
            view.Consent = true;
            Assert(!view.ExportCommand.CanExecute(null), "Export was available without preview.");
            await view.PreviewCommand.ExecuteAsync();
            Assert(!view.Consent && view.PreviewText.Contains("manifest.json", StringComparison.Ordinal), "New preview reused consent or lost manifest.");
            view.Consent = true;
            view.ExportPath = Path.Combine(directory, "reviewed.zip");
            Assert(!view.Consent, "Changed destination reused approval.");
            view.Consent = true;
            Assert(view.ExportCommand.CanExecute(null), "Reviewed bundle could not be exported.");
            await view.ExportCommand.ExecuteAsync();
            Assert(view.ExportSucceeded && File.Exists(view.ExportPath) && !view.Consent, "UI export did not commit or consume consent.");
            using var archive = System.IO.Compression.ZipFile.OpenRead(view.ExportPath);
            foreach (var entry in archive.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                Assert(view.PreviewText.Contains(entry.FullName + Environment.NewLine + await reader.ReadToEndAsync(), StringComparison.Ordinal),
                    "Export differed from displayed preview.");
            }
            await view.RefreshCommand.ExecuteAsync();
            Assert(view.PreviewText.Length == 0 && !view.ExportCommand.CanExecute(null), "Refresh left an approved stale preview.");
        }
        finally { Directory.Delete(directory, recursive: true); }
        var previous = view.MetricsJson;
        fail = true;
        await view.RefreshCommand.ExecuteAsync();
        Assert(failures == 1 && view.MetricsJson == previous && !view.MetricsJson.Contains("SECRET", StringComparison.Ordinal),
            "Failed refresh leaked content or replaced the previous snapshot.");
        var completion = new TaskCompletionSource<DiagnosticMetrics>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var late = new DiagnosticsViewModel(_ => completion.Task, () => [], reporter);
        var refresh = late.RefreshCommand.ExecuteAsync();
        Assert(!late.RefreshCommand.CanExecute(null), "Duplicate refresh allowed.");
        var shutdown = late.ShutdownAsync();
        Assert(!shutdown.IsCompleted, "Shutdown did not drain the in-flight sample.");
        completion.SetResult(snapshot);
        await Task.WhenAll(refresh, shutdown);
        Assert(late.MetricsJson.Length == 0 && !late.RefreshCommand.CanExecute(null), "Late result changed a closed page.");
        Console.WriteLine("Diagnostics UI: aggregate/recent display, unavailable values, safe failure preservation, duplicate guard and late refresh shutdown passed.");
    }

    private static void Assert(bool value, string message)
    { if (!value) { throw new InvalidOperationException(message); } }
}

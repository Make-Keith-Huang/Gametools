using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLingoAI.QualityHarness;

if (args is not [var mode, var count, var destination])
{
    Console.Error.WriteLine("Usage: replay <frames> <new-local.json>; benchmark 40 <new-local.json>; fault-replay <recovery|auth> <new-local.json>; soak <active|idle|ci> <new-local.json>; growth-check <steady|breach|missing|short> <new-local.json>.");
    return 2;
}
var frames = 0;
var valid = mode switch
{
    "replay" or "benchmark" => int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out frames) &&
        frames is >= 8 and <= 100_000 && frames % 8 == 0 && (mode != "benchmark" || frames == 40),
    "soak" => count is "active" or "idle" or "ci",
    "growth-check" => count is "steady" or "breach" or "missing" or "short",
    "fault-replay" => count is "recovery" or "auth",
    _ => false
};
if (!valid) { Console.Error.WriteLine("Invalid mode or workload. Use a documented harness command."); return 2; }

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
string? partial = null;
try
{
    var path = Path.GetFullPath(destination);
    if (new Uri(path).IsUnc || !string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
    { throw new ArgumentException("Select a local JSON destination."); }
    if (File.Exists(path))
    { throw new IOException("Report already exists."); }
    object document;
    int exitCode;
    if (mode == "soak")
    {
        var options = count switch { "active" => SoakOptions.ActiveHour, "idle" => SoakOptions.IdleEightHours, _ => SoakOptions.ShortCi };
        var report = await SoakRunner.RunAsync(options, cancellation.Token);
        exitCode = report.Growth.ExitCode;
        document = new
        {
            SchemaVersion = 1,
            Mode = "synthetic-soak-" + count,
            WallClockMeasured = true,
            NativeApplicationEvaluated = false,
            ReleaseGateEvaluated = false,
            GpuCounterAvailable = false,
            ResourceWindow = SoakRunner.MaximumRetainedSamples,
            Report = report
        };
    }
    else if (mode == "growth-check")
    {
        // Explicitly virtual counter fixtures test process exit/report wiring, never soak performance.
        var gate = new MemoryGrowthGate(TimeSpan.Zero);
        gate.Observe(TimeSpan.Zero, 100_000_000);
        gate.Observe(TimeSpan.FromSeconds(1), count switch { "breach" => 150_000_000, "missing" => null, _ => 100_000_000 });
        var result = gate.Assess(TimeSpan.FromSeconds(count == "short" ? 2 : 1));
        exitCode = result.ExitCode;
        document = new
        {
            SchemaVersion = 1,
            Mode = "virtual-growth-check",
            WallClockMeasured = false,
            NativeApplicationEvaluated = false,
            ReleaseGateEvaluated = false,
            Assessment = result
        };
    }
    else
    {
        IReadOnlyList<ProviderStep>? script = mode == "fault-replay" ? FaultProfiles.Select(count) : null;
        var result = await PipelineReplay.RunAsync(mode == "fault-replay" ? 128 : frames, cancellation.Token, script);
        var assessment = mode == "benchmark" ? ReplayBenchmark.Evaluate(result) : null;
        exitCode = assessment?.ExitCode ?? 0;
        document = new
        {
            SchemaVersion = 1,
            Mode = mode == "benchmark" ? "baselined-synthetic-replay" : mode == "fault-replay" ? "unbaselined-fault-replay" : "unbaselined-pipeline-replay",
            ProviderProfile = mode == "fault-replay" ? count : "success-100ms",
            PerformanceGateEvaluated = assessment is not null,
            Assessment = assessment,
            NativeOcrEvaluated = false,
            NativeOverlayEvaluated = false,
            RemoteProviderEvaluated = false,
            StagePercentileWindow = 256,
            EndToEndPercentileWindow = 4096,
            Runtime = Environment.Version.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            Report = result
        };
    }
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    var candidate = Path.Combine(Path.GetDirectoryName(path)!, ".replay-" + Guid.NewGuid().ToString("N") + ".partial");
    await using (var output = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
    {
        partial = candidate;
        await JsonSerializer.SerializeAsync(output, document, ReportJson.Options, cancellation.Token);
    }
    cancellation.Token.ThrowIfCancellationRequested();
    File.Move(partial, path, overwrite: false);
    Console.WriteLine("Harness completed; local report saved. Exit code: " + exitCode.ToString(CultureInfo.InvariantCulture) + ". Native/release gates are not evaluated.");
    return exitCode;
}

catch (OperationCanceledException)
{
    Console.Error.WriteLine("Harness cancelled or timed out; no completed report was committed.");
    return 130;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("Cannot create report. Choose a writable, unused local .json destination and valid frame count.");
    return 2;
}
catch (Exception)
{
    Console.Error.WriteLine("Harness failed. Run PipelineReplayTests and SoakTests to inspect the synthetic pipeline; no release pass is claimed.");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancel;
    if (partial is not null && File.Exists(partial))
    {
        try
        { File.Delete(partial); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Could not remove the owned partial report; inspect the destination directory."); }
    }
}

file static class ReportJson
{
    public static JsonSerializerOptions Options { get; } = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
}

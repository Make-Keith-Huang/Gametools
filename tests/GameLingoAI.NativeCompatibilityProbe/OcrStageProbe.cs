using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameLingoAI.Infrastructure.Ocr;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;

internal static class OcrStageProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(OcrModelPackage package, string installation, CancellationToken token)
    {
        using var runtime = new SdcbPaddleOcrRuntimeFactory(Math.Clamp(Environment.ProcessorCount / 2, 1, 8), reusePredictors: false).Create(package, installation);
        // Diagnostic-only access to owned managed objects; no fields are modified.
        // Fail closed when pinned package internals change. Never access another process.
        var engine = ReadField<PaddleOcrAll>(runtime, "_engine");
        var detector = ReadField<PaddlePredictor>(engine.Detector, "_p");
        var recognizer = ReadField<PaddlePredictor>(engine.Recognizer, "_p");
        using var image = new Mat(160, 1000, MatType.CV_8UC3, Scalar.White);
        Cv2.PutText(image, "HELLO WORLD 123", new Point(35, 105), HersheyFonts.HersheySimplex,
            2, Scalar.Black, 3, LineTypes.AntiAlias);
        var samples = new List<object>();
        var failures = 0;
        for (var index = 0; index < 8; index++)
        {
            token.ThrowIfCancellationRequested();
            var detectorClone = TimeClone(detector);
            var recognizerClone = TimeClone(recognizer);
            var watch = Stopwatch.StartNew();
            var regions = engine.Detector.Run(image);
            var detection = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var result = engine.Run(image);
            var total = watch.Elapsed.TotalMilliseconds;
            var actual = string.Concat(result.Regions.Select(region => region.Text));
            var correct = string.Concat(actual.Where(character => !char.IsWhiteSpace(character))) == "HELLOWORLD123";
            if (!correct)
            { failures++; }
            samples.Add(new
            {
                index,
                correct,
                regions = regions.Length,
                detectionMilliseconds = detection,
                fullRunMilliseconds = total,
                detectorCloneMilliseconds = detectorClone,
                recognizerCloneMilliseconds = recognizerClone
            });
        }

        var report = new
        {
            utc = DateTimeOffset.UtcNow,
            failures,
            scope = "Owned in-memory authored fixture; independent clone/detection/full calls, not additive stage accounting",
            detectorMaxSize = engine.Detector.MaxSize,
            samples
        };
        var output = Path.GetFullPath(Path.Combine("artifacts", "native-compatibility", "ocr-stages-" + Guid.NewGuid().ToString("N") + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions), token);
        Console.WriteLine($"Stage probe exact checks: {8 - failures}/8; report: {output}");
        return failures == 0 ? 0 : 3;
    }

    private static double TimeClone(PaddlePredictor predictor)
    {
        var watch = Stopwatch.StartNew();
        using (predictor.Clone())
        { }
        return watch.Elapsed.TotalMilliseconds;
    }

    private static T ReadField<T>(object instance, string name) where T : class =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new InvalidOperationException("Pinned diagnostic field unavailable.");
}

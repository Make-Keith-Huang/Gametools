using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Ocr;
using OpenCvSharp;

internal static class OcrPerformanceProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(OcrModelProvisioner provisioner, OcrModelPackage package,
        string installation, CancellationToken token)
    {
        var verification = new List<double>();
        for (var index = 0; index < 5; index++)
        {
            var watch = Stopwatch.StartNew();
            var status = await provisioner.GetStatusAsync(package, token);
            if (status.State != OcrModelState.Ready)
            { throw new InvalidOperationException("Model verification failed."); }
            verification.Add(watch.Elapsed.TotalMilliseconds);
        }

        var samples = new List<object>();
        var failures = 0;
        // No await in this block: construction, all inference calls and disposal share one thread.
        using (var runtime = new SdcbPaddleOcrRuntimeFactory().Create(package, installation))
        using (var image = CreateImage())
        {
            for (var index = 0; index < 12; index++)
            {
                token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                var spans = runtime.Recognize(image, token);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                var correct = CheckText(string.Join(" ", spans.Select(span => span.Text)));
                if (!correct)
                { failures++; }
                samples.Add(new
                {
                    mode = "same-thread-runtime",
                    index,
                    correct,
                    elapsedMilliseconds = elapsed,
                    thread = Environment.CurrentManagedThreadId
                });
            }
        }

        var recording = new RecordingFactory();
        await using (var engine = new PaddleOcrEngine(provisioner, recording, package))
        {
            for (var index = 0; index < 12; index++)
            {
                token.ThrowIfCancellationRequested();
                using var frame = new PreprocessedFrame(new PipelineMetadata(SessionId.New(), new SequenceNumber(index),
                    DateTimeOffset.UtcNow, CorrelationId.New()), [CreateImage()]);
                var watch = Stopwatch.StartNew();
                var result = await engine.RecognizeAsync(frame, new OcrRecognitionOptions(OcrLanguage.English, 0.65), token);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                if (!result.IsSuccess)
                { throw new InvalidOperationException("Production OCR failed."); }
                var correct = CheckText(string.Join(" ", result.Value!.Spans.Select(span => span.Text)));
                if (!correct)
                { failures++; }
                samples.Add(new
                {
                    mode = "production-engine",
                    index,
                    correct,
                    elapsedMilliseconds = elapsed,
                    nativeMilliseconds = recording.LastMilliseconds,
                    thread = recording.LastThread
                });
            }
        }

        foreach (var threads in new[] { 1, 2, 4, 8 })
        {
            using var runtime = new SdcbPaddleOcrRuntimeFactory(threads).Create(package, installation);
            using var image = CreateImage();
            for (var index = 0; index < 6; index++)
            {
                token.ThrowIfCancellationRequested();
                var watch = Stopwatch.StartNew();
                var spans = runtime.Recognize(image, token);
                var elapsed = watch.Elapsed.TotalMilliseconds;
                var correct = CheckText(string.Join(" ", spans.Select(span => span.Text)));
                if (!correct)
                { failures++; }
                samples.Add(new { mode = "cpu-thread-comparison", threads, index, correct, elapsedMilliseconds = elapsed });
            }
        }

        var report = new
        {
            utc = DateTimeOffset.UtcNow,
            failures,
            fixture = "authored HELLO WORLD 123 /1000x160 BGRA",
            scope = "48 OCR calls, first sample cold in each group; not sustained capture or full-app acceptance",
            verificationMilliseconds = verification,
            runtimeCreationMilliseconds = recording.CreationMilliseconds,
            samples
        };
        var output = Path.GetFullPath(Path.Combine("artifacts", "native-compatibility", "ocr-performance-" + Guid.NewGuid().ToString("N") + ".json"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, JsonOptions), token);
        Console.WriteLine($"Synthetic text checks: {48 - failures}/48 passed. Performance evidence: " + output);
        return failures == 0 ? 0 : 3;
    }

    private static PreprocessedRegionImage CreateImage()
    {
        using var mat = new Mat(160, 1000, MatType.CV_8UC4, Scalar.All(255));
        Cv2.PutText(mat, "HELLO WORLD 123", new Point(35, 105), HersheyFonts.HersheySimplex,
            2, Scalar.All(0), 3, LineTypes.AntiAlias);
        var pixels = new byte[checked((int)(mat.Total() * mat.ElemSize()))];
        Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new PreprocessedRegionImage(Guid.NewGuid(), new NormalizedRegion(0, 0, 1, 1),
            mat.Width, mat.Height, OcrPixelFormat.Bgra32, pixels, new PixelLease(pixels));
    }

    private static bool CheckText(string text)
    {
        if (string.Concat(text.Where(character => !char.IsWhiteSpace(character))) != "HELLOWORLD123")
        {
            Console.Error.WriteLine("Authored fixture recognition mismatch: " + text);
            return false;
        }
        return true;
    }

    private sealed class RecordingFactory : IPaddleOcrRuntimeFactory
    {
        public double CreationMilliseconds { get; private set; }
        public double LastMilliseconds { get; set; }
        public int LastThread { get; set; }
        public IPaddleOcrRuntime Create(OcrModelPackage package, string installationPath)
        {
            var watch = Stopwatch.StartNew();
            var runtime = new SdcbPaddleOcrRuntimeFactory().Create(package, installationPath);
            CreationMilliseconds = watch.Elapsed.TotalMilliseconds;
            return new RecordingRuntime(runtime, this);
        }
    }

    private sealed class RecordingRuntime(IPaddleOcrRuntime inner, RecordingFactory owner) : IPaddleOcrRuntime
    {
        public IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image)
        {
            var watch = Stopwatch.StartNew();
            try
            { return inner.Recognize(image); }
            finally
            {
                owner.LastMilliseconds = watch.Elapsed.TotalMilliseconds;
                owner.LastThread = Environment.CurrentManagedThreadId;
            }
        }

        public void Dispose() => inner.Dispose();
    }
}

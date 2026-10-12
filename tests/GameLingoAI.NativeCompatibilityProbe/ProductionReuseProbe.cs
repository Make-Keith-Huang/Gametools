using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Ocr;
using OpenCvSharp;

internal static class ProductionReuseProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(OcrModelPackage package, string installation, CancellationToken token)
    {
        var root = Environment.GetEnvironmentVariable("GAMELINGOAI_PROBE_REPORT_ROOT")
            ?? throw new InvalidOperationException("Explicit report directory required.");
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        using var baseline = new SdcbPaddleOcrRuntimeFactory(threads, reusePredictors: false).Create(package, installation);
        using var candidate = new SdcbPaddleOcrRuntimeFactory().Create(package, installation);
        if (candidate is not ReusablePaddleOcrRuntime)
        { throw new InvalidOperationException("Default factory must select predictor reuse."); }
        var samples = new List<object>();
        var lifecycle = new List<object>();
        var failures = 0;
        bool Check(string id, PreprocessedRegionImage image, string phrase)
        {
            token.ThrowIfCancellationRequested();
            var expected = baseline.Recognize(image, token);
            var watch = Stopwatch.StartNew();
            var actual = candidate.Recognize(image, token);
            var elapsedMs = watch.Elapsed.TotalMilliseconds;
            var equivalent = expected.Count == actual.Count;
            for (var index = 0; index < Math.Min(expected.Count, actual.Count); index++)
            {
                var left = expected[index];
                var right = actual[index];
                equivalent &= left.Text == right.Text && double.IsFinite(right.Confidence) &&
                    Math.Abs(left.Confidence - right.Confidence) <= 1e-6 &&
                    Math.Abs(left.X - right.X) <= 1e-4 && Math.Abs(left.Y - right.Y) <= 1e-4 &&
                    Math.Abs(left.Width - right.Width) <= 1e-4 && Math.Abs(left.Height - right.Height) <= 1e-4;
            }
            var correct = Compact(string.Concat(actual.Select(span => span.Text))) == Compact(phrase);
            if (!equivalent || !correct)
            { failures++; }
            samples.Add(new { id, equivalent, correct, elapsedMs });
            return equivalent && correct;
        }
        foreach (var phrase in new[] { "HELLO WORLD 123", "欢迎来到新的世界", "歡迎來到新的世界", "こんにちは世界" })
        {
            foreach (var style in new[] { "normal", "small", "outlined" })
            {
                token.ThrowIfCancellationRequested();
                using var image = await SyntheticCorpus.RenderAsync(phrase, style);
                Check("bgra-" + samples.Count + "-" + style, image, phrase);
                using var bgra = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC4, image.Pixels.ToArray());
                using var gray = new Mat();
                Cv2.CvtColor(bgra, gray, ColorConversionCodes.BGRA2GRAY);
                using var grayImage = ToImage(gray, OcrPixelFormat.Gray8);
                Check("gray-" + samples.Count + "-" + style, grayImage, phrase);
            }
        }
        foreach (var style in new[] { "normal", "small", "outlined" })
        {
            using var first = await SyntheticCorpus.RenderAsync("HELLO WORLD 123", style);
            using var second = await SyntheticCorpus.RenderAsync("こんにちは世界", style);
            using var upper = Mat.FromPixelData(first.Height, first.Width, MatType.CV_8UC4, first.Pixels.ToArray());
            using var lower = Mat.FromPixelData(second.Height, second.Width, MatType.CV_8UC4, second.Pixels.ToArray());
            for (var order = 0; order < 2; order++)
            {
                using var combined = new Mat();
                Cv2.VConcat(order == 0 ? upper : lower, order == 0 ? lower : upper, combined);
                using var image = ToImage(combined, OcrPixelFormat.Bgra32);
                var phrase = order == 0 ? "HELLO WORLD 123こんにちは世界" : "こんにちは世界HELLO WORLD 123";
                Check($"multiline-{style}-{order}", image, phrase);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(5));
                var watch = Stopwatch.StartNew();
                var rejected = false;
                try
                { _ = candidate.Recognize(image, cancellation.Token); }
                catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token)
                { rejected = true; }
                var elapsedMs = watch.Elapsed.TotalMilliseconds;
                token.ThrowIfCancellationRequested();
                var recovered = Check($"recovery-{style}-{order}", image, phrase);
                var passed = rejected && elapsedMs < 2000 && recovered;
                if (!passed)
                { failures++; }
                lifecycle.Add(new { kind = "timed-cancel-recovery", rejected, elapsedMs, recovered, passed });
            }
        }
        foreach (var format in new[] { OcrPixelFormat.Gray8, OcrPixelFormat.Bgra32 })
        {
            using var blank = new Mat(160, 1000, format == OcrPixelFormat.Gray8 ? MatType.CV_8UC1 : MatType.CV_8UC4, Scalar.All(255));
            using var image = ToImage(blank, format);
            Check("blank-" + format, image, "");
        }
        using var finalImage = await SyntheticCorpus.RenderAsync("HELLO WORLD 123", "normal");
        var preCancelled = false;
        try
        { _ = candidate.Recognize(finalImage, new CancellationToken(true)); }
        catch (OperationCanceledException) { preCancelled = true; }
        candidate.Dispose();
        candidate.Dispose();
        var disposedRejected = false;
        try
        { _ = candidate.Recognize(finalImage, token); }
        catch (ObjectDisposedException) { disposedRejected = true; }
        if (!preCancelled || !disposedRejected)
        { failures++; }
        lifecycle.Add(new { kind = "pre-cancel-double-dispose", preCancelled, disposedRejected, passed = preCancelled && disposedRejected });
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ocr-production-reuse.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { scope = "Actual candidate adapter; timed cancellation may occur during preprocessing, not proof of native in-flight cancellation or full-app stop", failures, samples, lifecycle }, JsonOptions), token);
        Console.WriteLine($"No-reflection production candidate: {samples.Count} corpus checks, {lifecycle.Count} lifecycle groups, {failures} failures; report: {path}");
        return failures == 0 ? 0 : 3;
    }

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static PreprocessedRegionImage ToImage(Mat mat, OcrPixelFormat format)
    {
        var pixels = new byte[checked((int)(mat.Total() * mat.ElemSize()))];
        Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
        return new PreprocessedRegionImage(Guid.NewGuid(), new NormalizedRegion(0, 0, 1, 1), mat.Width, mat.Height, format, pixels, new PixelLease(pixels));
    }
}

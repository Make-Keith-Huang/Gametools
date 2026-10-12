using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using GameLingoAI.Infrastructure.Ocr;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;

internal static partial class OcrReuseProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] Styles = ["normal", "small", "outlined"];
    internal static async Task<int> RunCroppedAsync(OcrModelPackage package, string installation, CancellationToken token, bool reuseDetector = false, int cycles = 2, bool cacheFixtures = false)
    {
        var root = Environment.GetEnvironmentVariable("GAMELINGOAI_PROBE_REPORT_ROOT")
            ?? throw new InvalidOperationException("Explicit report directory required.");
        var phrases = new[] { "HELLO WORLD 123", "欢迎来到新的世界", "歡迎來到新的世界", "こんにちは世界" };
        var samples = new List<object>();
        var resources = new List<object>();
        var fixtures = new Dictionary<(int Phrase, string Style), byte[]>();
        if (cacheFixtures)
        {
            for (var phrase = 0; phrase < phrases.Length; phrase++)
            {
                foreach (var style in Styles)
                {
                    token.ThrowIfCancellationRequested();
                    using var rendered = await SyntheticCorpus.RenderAsync(phrases[phrase], style);
                    fixtures.Add((phrase, style), rendered.Pixels.ToArray());
                }
            }
        }
        using var process = Process.GetCurrentProcess();
        var failures = 0;
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            token.ThrowIfCancellationRequested();
            process.Refresh();
            resources.Add(new { completedCycles = cycle, privateBytes = process.PrivateMemorySize64, handles = process.HandleCount });
            using var runtime = new SdcbPaddleOcrRuntimeFactory(Math.Clamp(Environment.ProcessorCount / 2, 1, 8), reusePredictors: false).Create(package, installation);
            var engine = Field<PaddleOcrAll>(runtime, "_engine");
            using var predictor = Field<PaddlePredictor>(engine.Recognizer, "_p").Clone();
            using var detectorPredictor = Field<PaddlePredictor>(engine.Detector, "_p").Clone();
            for (var phrase = 0; phrase < phrases.Length; phrase++)
            {
                foreach (var style in Styles)
                {
                    token.ThrowIfCancellationRequested();
                    byte[] pixels;
                    if (cacheFixtures)
                    {
                        pixels = fixtures[(phrase, style)];
                    }
                    else
                    {
                        using var rendered = await SyntheticCorpus.RenderAsync(phrases[phrase], style);
                        pixels = rendered.Pixels.ToArray();
                    }
                    using var bgra = Mat.FromPixelData(160, 1000, MatType.CV_8UC4, pixels);
                    using var image = new Mat();
                    Cv2.CvtColor(bgra, image, ColorConversionCodes.BGRA2BGR);
                    var watch = Stopwatch.StartNew();
                    var baselineRegions = engine.Run(image).Regions;
                    var baseline = string.Concat(baselineRegions.Select(region => region.Text));
                    var baselineMs = watch.Elapsed.TotalMilliseconds;
                    watch.Restart();
                    var actualRegions = RecognizeCropSpans(engine, predictor, image,
                        reuseDetector ? Detect(engine.Detector, detectorPredictor, image, token) : engine.Detector.Run(image), token);
                    var actual = string.Concat(actualRegions.Select(region => region.Text));
                    var reusedMs = watch.Elapsed.TotalMilliseconds;
                    var equivalent = actual.ToString() == baseline;
                    var correct = string.Concat(actual.ToString().Where(c => !char.IsWhiteSpace(c))) == string.Concat(phrases[phrase].Where(c => !char.IsWhiteSpace(c)));
                    var geometryMatches = actualRegions.Count == baselineRegions.Length;
                    var confidenceMatches = geometryMatches;
                    for (var region = 0; region < Math.Min(actualRegions.Count, baselineRegions.Length); region++)
                    {
                        var expected = baselineRegions[region];
                        var measured = actualRegions[region];
                        confidenceMatches &= float.IsFinite(measured.Score) && float.IsFinite(expected.Score) && Math.Abs(measured.Score - expected.Score) <= 1e-6f;
                        var expectedPoints = expected.Rect.Points();
                        var measuredPoints = measured.Rect.Points();
                        for (var point = 0; point < expectedPoints.Length; point++)
                        {
                            geometryMatches &= Math.Abs(expectedPoints[point].X - measuredPoints[point].X) <= 1e-4f &&
                                Math.Abs(expectedPoints[point].Y - measuredPoints[point].Y) <= 1e-4f;
                        }
                    }
                    if (!equivalent || !correct || !geometryMatches || !confidenceMatches)
                    { failures++; }
                    samples.Add(new { cycle, phrase, style, equivalent, correct, geometryMatches, confidenceMatches, baselineMs, reusedMs });
                }
            }
            Console.WriteLine($"Completed corpus ownership cycle {cycle + 1}/{cycles}.");
        }
        process.Refresh();
        resources.Add(new { completedCycles = cycles, privateBytes = process.PrivateMemorySize64, handles = process.HandleCount });
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, cacheFixtures ? "ocr-reuse-lifecycle-cached.json" : cycles > 2 ? "ocr-reuse-lifecycle.json" : reuseDetector ? "ocr-reuse-integrated.json" : "ocr-reuse-cropped.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { scope = "Serial predictor/crop authored corpus; pre-creation/post-disposal process snapshots; not capture/translation/overlay or long-run acceptance", reuseDetector, cacheFixtures, failures, samples, resources }, JsonOptions), token);
        Console.WriteLine($"Cropped recognizer reuse: {cycles * 12 - failures}/{cycles * 12} exact/equivalent checks; report: {path}");
        return failures == 0 ? 0 : 3;
    }

    internal static async Task<int> RunSustainedAsync(OcrModelPackage package, string installation, CancellationToken token)
    {
        var root = Environment.GetEnvironmentVariable("GAMELINGOAI_PROBE_REPORT_ROOT")
            ?? throw new InvalidOperationException("Explicit report directory required.");
        var seconds = int.Parse(Environment.GetEnvironmentVariable("GAMELINGOAI_PROBE_SOAK_SECONDS") ?? "120", System.Globalization.CultureInfo.InvariantCulture);
        if (seconds is < 120 or > 3600)
        { throw new InvalidOperationException("Soak duration must be 120..3600 seconds."); }
        var fixtures = new List<(Mat Image, string Expected)>();
        try
        {
            foreach (var phrase in new[] { "HELLO WORLD 123", "欢迎来到新的世界", "歡迎來到新的世界", "こんにちは世界" })
            {
                foreach (var style in Styles)
                {
                    token.ThrowIfCancellationRequested();
                    using var rendered = await SyntheticCorpus.RenderAsync(phrase, style);
                    using var bgra = Mat.FromPixelData(160, 1000, MatType.CV_8UC4, rendered.Pixels.ToArray());
                    var bgr = new Mat();
                    try
                    {
                        Cv2.CvtColor(bgra, bgr, ColorConversionCodes.BGRA2BGR);
                        fixtures.Add((bgr, string.Concat(phrase.Where(c => !char.IsWhiteSpace(c)))));
                    }
                    catch { bgr.Dispose(); throw; }
                }
            }
            using var runtime = new SdcbPaddleOcrRuntimeFactory(Math.Clamp(Environment.ProcessorCount / 2, 1, 8), reusePredictors: false).Create(package, installation);
            var engine = Field<PaddleOcrAll>(runtime, "_engine");
            using var detector = Field<PaddlePredictor>(engine.Detector, "_p").Clone();
            using var recognizer = Field<PaddlePredictor>(engine.Recognizer, "_p").Clone();
            using var process = Process.GetCurrentProcess();
            var resources = new List<object>();
            var watch = Stopwatch.StartNew();
            var count = 0;
            var failures = 0;
            var measuredCount = 0;
            var totalMs = 0.0;
            var maxMs = 0.0;
            long? baselineBytes = null;
            var peakBytes = 0L;
            var nextSnapshot = 0.0;
            var path = Path.Combine(root, seconds == 3600 ? "ocr-reuse-soak-60min.json" : "ocr-reuse-soak-smoke.json");
            Directory.CreateDirectory(root);
            while (watch.Elapsed.TotalSeconds < 30 + seconds)
            {
                token.ThrowIfCancellationRequested();
                var fixture = fixtures[count % fixtures.Count];
                var started = watch.Elapsed.TotalMilliseconds;
                var actual = RecognizeCrops(engine, recognizer, fixture.Image, Detect(engine.Detector, detector, fixture.Image, token), token);
                var elapsed = watch.Elapsed.TotalMilliseconds - started;
                if (string.Concat(actual.Where(c => !char.IsWhiteSpace(c))) != fixture.Expected)
                { failures++; }
                count++;
                if (started >= 30000)
                { measuredCount++; totalMs += elapsed; maxMs = Math.Max(maxMs, elapsed); }
                if (watch.Elapsed.TotalSeconds >= 30 && baselineBytes is null)
                {
                    process.Refresh();
                    baselineBytes = process.PrivateMemorySize64;
                }
                if (watch.Elapsed.TotalSeconds >= nextSnapshot)
                {
                    process.Refresh();
                    if (baselineBytes is not null)
                    { peakBytes = Math.Max(peakBytes, process.PrivateMemorySize64); }
                    resources.Add(new { seconds = watch.Elapsed.TotalSeconds, privateBytes = process.PrivateMemorySize64, handles = process.HandleCount });
                    nextSnapshot = watch.Elapsed.TotalSeconds + 15;
                    Console.WriteLine($"Sustained OCR: {watch.Elapsed.TotalSeconds:F0}/{30 + seconds}s, {count} cases, {failures} failures.");
                }
            }
            process.Refresh();
            peakBytes = Math.Max(peakBytes, process.PrivateMemorySize64);
            var growth = peakBytes - baselineBytes!.Value;
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
            {
                scope = "One serial reused native runtime, fixed fixtures, 30-second warm-up; not full-app acceptance",
                seconds,
                elapsedSeconds = watch.Elapsed.TotalSeconds,
                count,
                failures,
                measuredCount,
                meanMs = totalMs / measuredCount,
                maxMs,
                baselineBytes,
                peakBytes,
                growthBytes = growth,
                memoryWithin50MB = growth < 50_000_000,
                resources
            }, JsonOptions), token);
            Console.WriteLine($"Sustained report: {path}; growth={growth} bytes.");
            return failures == 0 && growth < 50_000_000 ? 0 : 3;
        }
        finally
        {
            foreach (var fixture in fixtures)
            { fixture.Image.Dispose(); }
        }
    }

    private static string RecognizeCrops(PaddleOcrAll engine, PaddlePredictor predictor, Mat image, RotatedRect[] boxes, CancellationToken token, CancellationObservation? observation = null)
        => string.Concat(RecognizeCropSpans(engine, predictor, image, boxes, token, observation).Select(region => region.Text));

    private sealed record ReusedSpan(string Text, float Score, RotatedRect Rect);

    private static List<ReusedSpan> RecognizeCropSpans(PaddleOcrAll engine, PaddlePredictor predictor, Mat image, RotatedRect[] boxes, CancellationToken token, CancellationObservation? observation = null)
    {
        var actual = new List<ReusedSpan>();
        foreach (var box in boxes)
        {
            token.ThrowIfCancellationRequested();
            var rect = (Rect)(typeof(PaddleOcrAll).GetMethod("GetCropedRect", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, [box.BoundingRect(), image.Size()])
                ?? throw new InvalidOperationException("Pinned crop method unavailable."));
            using var crop = image[rect];
            var height = engine.Recognizer.Model.Shape.Height;
            var width = (int)Math.Ceiling((double)crop.Width / crop.Height * height);
            using var resized = RecognizerInvoke<Mat>("ResizePadding", crop, height, width);
            using var normalized = RecognizerInvoke<Mat>("Normalize", resized);
            var data = RecognizerInvoke<float[]>("ExtractMat", new[] { normalized }, 3, height, width);
            var output = Infer(predictor, [1, 3, height, width], data, token, observation);
            var decoded = DecodeResult(output.Shape, output.Values, engine.Recognizer.Model.GetLabelByIndex);
            actual.Add(new ReusedSpan(decoded.Text, decoded.Score, box));
        }
        return actual;
    }

    // Isolated experiment: reuse upstream preprocessing without copying or modifying its implementation.
    // One owned clone, one serial caller; the production runtime and its fields remain untouched.
    public static async Task<int> RunAsync(OcrModelPackage package, string installation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reportRoot = Environment.GetEnvironmentVariable("GAMELINGOAI_PROBE_REPORT_ROOT")
            ?? throw new InvalidOperationException("Explicit report directory required.");
        var samples = new List<object>();
        var recognitionSamples = new List<object>();
        var failures = 0;
        var cancellationChecks = 0;
        var decodedMatches = 0;
        var accuracyFailures = 0;
        var phrases = new[] { "HELLO WORLD 123", "欢迎来到新的世界", "歡迎來到新的世界", "こんにちは世界" };
        for (var cycle = 0; cycle < 2; cycle++)
        {
            using var runtime = new SdcbPaddleOcrRuntimeFactory(Math.Clamp(Environment.ProcessorCount / 2, 1, 8), reusePredictors: false).Create(package, installation);
            var engine = Field<PaddleOcrAll>(runtime, "_engine");
            using var predictor = Field<PaddlePredictor>(engine.Detector, "_p").Clone();
            using var recognitionPredictor = Field<PaddlePredictor>(engine.Recognizer, "_p").Clone();
            for (var index = 0; index < 20; index++)
            {
                token.ThrowIfCancellationRequested();
                using var image = new Mat(160, index % 2 == 0 ? 1000 : 640, MatType.CV_8UC3, Scalar.White);
                if (index < 8 && index % 3 != 0)
                {
                    Cv2.PutText(image, "HELLO WORLD 123", new Point(20, 100), HersheyFonts.HersheySimplex,
                        1.5, Scalar.Black, 2, LineTypes.AntiAlias);
                }
                string? expectedText = null;
                if (index >= 8)
                {
                    expectedText = phrases[(index - 8) / 3];
                    using var rendered = await SyntheticCorpus.RenderAsync(expectedText, Styles[(index - 8) % 3]);
                    var pixels = rendered.Pixels.ToArray();
                    using var source = Mat.FromPixelData(rendered.Height, rendered.Width, MatType.CV_8UC4, pixels);
                    Cv2.CvtColor(source, image, ColorConversionCodes.BGRA2BGR);
                }
                var watch = Stopwatch.StartNew();
                using var baseline = engine.Detector.RunRaw(image, out var baselineSize);
                var baselineMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                using var resized = Invoke<Mat>("MatResize", image, engine.Detector.MaxSize);
                using var padded = Invoke<Mat>("MatPadding32", resized);
                using var normalized = Invoke<Mat>("Normalize", padded);
                using (var input = predictor.GetInputTensor(predictor.InputNames[0]))
                {
                    input.Shape = new[] { 1, 3, normalized.Rows, normalized.Cols };
                    input.SetData(Invoke<float[]>("ExtractMat", normalized));
                }
                token.ThrowIfCancellationRequested();
                if (!predictor.Run())
                { throw new InvalidOperationException("Reused detector failed."); }
                using var output = predictor.GetOutputTensor(predictor.OutputNames[0]);
                var shape = output.Shape;
                var values = output.GetData<float>();
                using var actual = Mat.FromPixelData(shape[2], shape[3], MatType.CV_32FC1, values);
                var reusedMs = watch.Elapsed.TotalMilliseconds;
                var matchingShape = baseline.Size() == actual.Size() && baselineSize == resized.Size();
                var difference = matchingShape ? Cv2.Norm(baseline, actual, NormTypes.INF) : double.PositiveInfinity;
                var correct = matchingShape && double.IsFinite(difference) && difference <= 1e-6;
                if (!correct)
                { failures++; }
                samples.Add(new { cycle, index, correct, baselineMs, reusedMs, maximumDifference = double.IsFinite(difference) ? difference : (double?)null });

                // Both arms receive exactly the same pinned upstream preprocessing.
                // Baseline mirrors the upstream per-call clone; the other arm owns one serial clone.
                var height = engine.Recognizer.Model.Shape.Height;
                var width = (int)Math.Ceiling((double)image.Width / image.Height * height);
                using var recognitionResized = RecognizerInvoke<Mat>("ResizePadding", image, height, width);
                using var recognitionNormalized = RecognizerInvoke<Mat>("Normalize", recognitionResized);
                var inputData = RecognizerInvoke<float[]>("ExtractMat", new[] { recognitionNormalized }, 3, height, width);
                var inputShape = new[] { 1, 3, height, width };
                watch.Restart();
                (int[] Shape, float[] Values) expected;
                using (var fresh = Field<PaddlePredictor>(engine.Recognizer, "_p").Clone())
                {
                    expected = Infer(fresh, inputShape, inputData, token);
                }
                var recognitionBaselineMs = watch.Elapsed.TotalMilliseconds;
                try
                {
                    _ = Infer(recognitionPredictor, inputShape, inputData, new CancellationToken(true));
                    throw new InvalidOperationException("Pre-cancelled inference was accepted.");
                }
                catch (OperationCanceledException)
                {
                    cancellationChecks++;
                }
                watch.Restart();
                var reused = Infer(recognitionPredictor, inputShape, inputData, token);
                var recognitionReusedMs = watch.Elapsed.TotalMilliseconds;
                var recognitionDifference = 0.0;
                var recognitionCorrect = expected.Shape.SequenceEqual(reused.Shape) && expected.Values.Length == reused.Values.Length;
                if (recognitionCorrect)
                {
                    for (var position = 0; position < expected.Values.Length; position++)
                    {
                        var delta = Math.Abs((double)expected.Values[position] - reused.Values[position]);
                        if (!double.IsFinite(delta))
                        { recognitionCorrect = false; break; }
                        recognitionDifference = Math.Max(recognitionDifference, delta);
                    }
                    recognitionCorrect &= recognitionDifference <= 1e-6;
                }
                if (!recognitionCorrect)
                { failures++; }
                var decoded = Decode(reused.Shape, reused.Values, engine.Recognizer.Model.GetLabelByIndex);
                var productionText = engine.Recognizer.Run(image).Text;
                var decodedCorrect = string.Equals(decoded, productionText, StringComparison.Ordinal);
                if (decodedCorrect)
                { decodedMatches++; }
                else
                { failures++; }
                bool? exactAuthoredText = expectedText is null ? null :
                    string.Concat(decoded.Where(c => !char.IsWhiteSpace(c))) == string.Concat(expectedText.Where(c => !char.IsWhiteSpace(c)));
                if (exactAuthoredText == false)
                { accuracyFailures++; }
                recognitionSamples.Add(new { cycle, index, correct = recognitionCorrect, decodedCorrect, exactAuthoredText, baselineMs = recognitionBaselineMs, reusedMs = recognitionReusedMs, maximumDifference = recognitionDifference });
            }
        }
        var path = Path.GetFullPath(Path.Combine(reportRoot, "ocr-reuse.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            scope = "Serial tensor/decoded-text equivalence and pre-cancel recovery, plus direct uncropped recognizer multilingual stress accuracy; not full-pipeline accuracy, in-flight cancellation-duration or long-soak acceptance",
            failures,
            accuracyFailures,
            cancellationChecks,
            decodedMatches,
            samples,
            recognitionSamples
        }, JsonOptions), token);
        Console.WriteLine($"Predictor reuse: {failures} equivalence failures; decoded {decodedMatches}/40; pre-cancel recovery {cancellationChecks}/40; direct multilingual accuracy {24 - accuracyFailures}/24; report: {path}");
        return failures == 0 && accuracyFailures == 0 ? 0 : 3;
    }

    private static T Field<T>(object instance, string name) where T : class =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as T
        ?? throw new InvalidOperationException("Pinned diagnostic field unavailable.");

    // Detector geometry follows PaddleSharp 139bc184a6d86c0c60bb8b8a90fb641b21c0b0e6.
    // Apache-2.0 license retained in packaging/licenses/paddle/paddlesharp-a875839-LICENSE.txt.
    // Diagnostic adaptation only: owned serial predictor replaces the per-call clone.
    private static RotatedRect[] Detect(PaddleOcrDetector detector, PaddlePredictor predictor, Mat image, CancellationToken token, CancellationObservation? observation = null)
    {
        using var resized = Invoke<Mat>("MatResize", image, detector.MaxSize);
        using var padded = Invoke<Mat>("MatPadding32", resized);
        using var normalized = Invoke<Mat>("Normalize", padded);
        var output = Infer(predictor, [1, 3, normalized.Rows, normalized.Cols], Invoke<float[]>("ExtractMat", normalized), token, observation);
        using var prediction = Mat.FromPixelData(output.Shape[2], output.Shape[3], MatType.CV_32FC1, output.Values);
        using var unpadded = prediction[0, resized.Height, 0, resized.Width];
        using var bytes = new Mat();
        unpadded.ConvertTo(bytes, MatType.CV_8UC1, 255);
        using var binary = detector.BoxThreshold is { } threshold ? bytes.Threshold((int)(threshold * 255), 255, ThresholdTypes.Binary) : bytes.Clone();
        var boxes = new List<RotatedRect>();
        var scale = (float)image.Width / resized.Width;
        foreach (var contour in binary.FindContoursAsArray(RetrievalModes.List, ContourApproximationModes.ApproxSimple))
        {
            token.ThrowIfCancellationRequested();
            if (detector.BoxScoreThreahold is { } score && Invoke<float>("GetScore", contour, prediction) <= score)
            { continue; }
            var perimeter = (float)Cv2.ArcLength(contour, true);
            if (perimeter < 1e-6f)
            { continue; }
            var box = Cv2.MinAreaRect(contour);
            var expansion = detector.UnclipRatio * (float)Math.Abs(Cv2.ContourArea(contour)) / perimeter;
            var size = new Size2f((box.Size.Width + 2 * expansion) * scale, (box.Size.Height + 2 * expansion) * scale);
            if (size.Width > detector.MinSize && size.Height > detector.MinSize)
            { boxes.Add(new RotatedRect(box.Center * scale, size, box.Angle)); }
        }
        return boxes.OrderBy(box => box.Center.Y).ThenBy(box => box.Center.X).ToArray();
    }

    private static string Decode(int[] shape, float[] values, Func<int, string> label)
        => DecodeResult(shape, values, label).Text;

    // Match the pinned upstream score: sum retained CTC maxima / UTF-16 text length.
    // Empty output remains NaN like upstream and is rejected by production confidence filtering.
    private static (string Text, float Score) DecodeResult(int[] shape, float[] values, Func<int, string> label)
    {
        if (shape.Length != 3 || shape[0] != 1 || shape[1] <= 0 || shape[2] <= 0 ||
            values.Length != checked(shape[1] * shape[2]))
        { throw new InvalidOperationException("Unexpected recognition tensor shape."); }
        var text = new System.Text.StringBuilder();
        var score = 0f;
        var previous = -1;
        for (var step = 0; step < shape[1]; step++)
        {
            var row = values.AsSpan(step * shape[2], shape[2]);
            var best = 0;
            for (var candidate = 0; candidate < row.Length; candidate++)
            {
                if (!float.IsFinite(row[candidate]))
                { throw new InvalidOperationException("Nonfinite recognition output."); }
                if (row[candidate] > row[best])
                { best = candidate; }
            }
            if (best != 0 && best != previous)
            { text.Append(label(best)); score += row[best]; }
            previous = best;
        }
        return (text.ToString(), score / text.Length);
    }

    private static T Invoke<T>(string name, params object?[] arguments) =>
        (T)(typeof(PaddleOcrDetector).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, arguments)
        ?? throw new InvalidOperationException("Pinned preprocessing method unavailable."));

    private static T RecognizerInvoke<T>(string name, params object?[] arguments) =>
        (T)(typeof(PaddleOcrRecognizer).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, arguments)
        ?? throw new InvalidOperationException("Pinned recognition preprocessing unavailable."));

    private static (int[] Shape, float[] Values) Infer(PaddlePredictor predictor, int[] shape, float[] data, CancellationToken token, CancellationObservation? observation = null)
    {
        token.ThrowIfCancellationRequested();
        using (var input = predictor.GetInputTensor(predictor.InputNames[0]))
        {
            input.Shape = shape;
            input.SetData(data);
        }
        observation?.Begin();
        try
        {
            if (!predictor.Run())
            { throw new InvalidOperationException("Native inference failed."); }
        }
        finally { observation?.End(); }
        token.ThrowIfCancellationRequested();
        using var output = predictor.GetOutputTensor(predictor.OutputNames[0]);
        return (output.Shape, output.GetData<float>());
    }
}

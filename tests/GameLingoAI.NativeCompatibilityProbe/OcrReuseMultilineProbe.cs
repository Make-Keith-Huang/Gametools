using System.Diagnostics;
using System.Text.Json;
using GameLingoAI.Infrastructure.Ocr;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;

internal static partial class OcrReuseProbe
{
    internal static async Task<int> RunMultilineAsync(OcrModelPackage package, string installation, CancellationToken token, bool batched = false)
    {
        var root = Environment.GetEnvironmentVariable("GAMELINGOAI_PROBE_REPORT_ROOT")
            ?? throw new InvalidOperationException("Explicit report directory required.");
        using var runtime = new SdcbPaddleOcrRuntimeFactory(Math.Clamp(Environment.ProcessorCount / 2, 1, 8), reusePredictors: false).Create(package, installation);
        var engine = Field<PaddleOcrAll>(runtime, "_engine");
        using var detector = Field<PaddlePredictor>(engine.Detector, "_p").Clone();
        using var recognizer = Field<PaddlePredictor>(engine.Recognizer, "_p").Clone();
        var samples = new List<object>();
        var boundaries = new List<object>();
        var failures = 0;
        foreach (var style in Styles)
        {
            using var first = await SyntheticCorpus.RenderAsync("HELLO WORLD 123", style);
            using var second = await SyntheticCorpus.RenderAsync("こんにちは世界", style);
            using var upper = Mat.FromPixelData(160, 1000, MatType.CV_8UC4, first.Pixels.ToArray());
            using var lower = Mat.FromPixelData(160, 1000, MatType.CV_8UC4, second.Pixels.ToArray());
            for (var order = 0; order < 2; order++)
            {
                token.ThrowIfCancellationRequested();
                using var combined = new Mat();
                Cv2.VConcat(order == 0 ? upper : lower, order == 0 ? lower : upper, combined);
                using var image = new Mat();
                Cv2.CvtColor(combined, image, ColorConversionCodes.BGRA2BGR);
                var baseline = engine.Run(image).Regions;
                var boxes = Detect(engine.Detector, detector, image, token);
                var actual = batched ? RecognizeBatchedSpans(engine, recognizer, image, boxes, token) : RecognizeCropSpans(engine, recognizer, image, boxes, token);
                var expected = order == 0 ? "HELLOWORLD123こんにちは世界" : "こんにちは世界HELLOWORLD123";
                static string Compact(IEnumerable<string> lines) => string.Concat(string.Concat(lines).Where(c => !char.IsWhiteSpace(c)));
                var baselineCorrect = Compact(baseline.Select(region => region.Text)) == expected;
                var actualCorrect = Compact(actual.Select(region => region.Text)) == expected;
                var geometryMatches = baseline.Length == 2 && actual.Count == baseline.Length;
                var scoreDelta = 0f;
                var textMatches = geometryMatches;
                for (var region = 0; region < Math.Min(actual.Count, baseline.Length); region++)
                {
                    textMatches &= actual[region].Text == baseline[region].Text;
                    var left = baseline[region].Rect.Points();
                    var right = actual[region].Rect.Points();
                    for (var point = 0; point < left.Length; point++)
                    {
                        geometryMatches &= Math.Abs(left[point].X - right[point].X) <= 1e-4f && Math.Abs(left[point].Y - right[point].Y) <= 1e-4f;
                    }
                    scoreDelta = Math.Max(scoreDelta, Math.Abs(baseline[region].Score - actual[region].Score));
                }
                var confidenceMatches = float.IsFinite(scoreDelta) && scoreDelta <= 1e-6f;
                var passed = baselineCorrect && actualCorrect && textMatches && geometryMatches && confidenceMatches;
                if (!passed)
                { failures++; }
                samples.Add(new { style, order, baselineCorrect, actualCorrect, textMatches, geometryMatches, confidenceMatches, scoreDelta = float.IsFinite(scoreDelta) ? scoreDelta : (float?)null, passed });
                if (batched && boxes.Length == 2)
                {
                    var boundary = CheckBatchBoundaries(engine, recognizer, image, boxes, token);
                    boundaries.Add(new { style, order, boundary.Empty, boundary.PreCancelled, boundary.InvalidBox, boundary.SplitMatches, boundary.CancelledInRun, boundary.CancellationMs, boundary.SingleCall, boundary.Recovered, boundary.Passed });
                    if (!boundary.Passed)
                    { failures++; }
                }
            }
        }
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, batched ? "ocr-reuse-multiline-batched.json" : "ocr-reuse-multiline.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { scope = "Two mixed-length authored lines and explicit batched boundary/cancellation checks", batched, failures, samples, boundaries }, JsonOptions), token);
        Console.WriteLine($"Multiline reuse: {failures} failed checks; report: {path}");
        return failures == 0 ? 0 : 3;
    }

    // Preserve the pinned upstream width-sorted batches and common padding width.
    // One owner executes each batch serially; result slots restore detector ordering.
    private static List<ReusedSpan> RecognizeBatchedSpans(PaddleOcrAll engine, PaddlePredictor predictor, Mat image, RotatedRect[] boxes, CancellationToken token, CancellationObservation? observation = null)
    {
        token.ThrowIfCancellationRequested();
        var crops = new List<(int Index, Mat Image)>();
        try
        {
            for (var index = 0; index < boxes.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var rect = boxes[index].BoundingRect();
                rect = Rect.FromLTRB(Math.Clamp(rect.Left, 0, image.Width), Math.Clamp(rect.Top, 0, image.Height),
                    Math.Clamp(rect.Right, 0, image.Width), Math.Clamp(rect.Bottom, 0, image.Height));
                if (rect.Width <= 0 || rect.Height <= 0)
                { throw new ArgumentException("Recognition box must intersect the image.", nameof(boxes)); }
                crops.Add((index, image[rect]));
            }
            var result = new ReusedSpan[boxes.Length];
            foreach (var batch in crops.OrderBy(crop => crop.Image.Width).Chunk(Math.Min(8, Environment.ProcessorCount)))
            {
                token.ThrowIfCancellationRequested();
                var height = engine.Recognizer.Model.Shape.Height;
                var width = (int)Math.Ceiling(batch.Max(crop => (double)crop.Image.Width / crop.Image.Height * height));
                var normalized = new List<Mat>();
                try
                {
                    foreach (var crop in batch)
                    {
                        using var resized = RecognizerInvoke<Mat>("ResizePadding", crop.Image, height, width);
                        normalized.Add(RecognizerInvoke<Mat>("Normalize", resized));
                    }
                    var data = RecognizerInvoke<float[]>("ExtractMat", normalized.ToArray(), 3, height, width);
                    var output = Infer(predictor, [batch.Length, 3, height, width], data, token, observation);
                    if (output.Shape.Length != 3 || output.Shape[0] != batch.Length || output.Shape[1] <= 0 || output.Shape[2] <= 0)
                    { throw new InvalidOperationException("Unexpected batched recognition shape."); }
                    var stride = checked(output.Shape[1] * output.Shape[2]);
                    if (output.Values.Length != checked(batch.Length * stride))
                    { throw new InvalidOperationException("Unexpected batched recognition data."); }
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var decoded = DecodeResult([1, output.Shape[1], output.Shape[2]], output.Values.AsSpan(index * stride, stride).ToArray(), engine.Recognizer.Model.GetLabelByIndex);
                        var original = batch[index].Index;
                        result[original] = new ReusedSpan(decoded.Text, decoded.Score, boxes[original]);
                    }
                }
                finally
                {
                    foreach (var mat in normalized)
                    { mat.Dispose(); }
                }
            }
            return result.ToList();
        }
        finally
        {
            foreach (var crop in crops)
            { crop.Image.Dispose(); }
        }
    }

    private sealed record BatchBoundaryResult(bool Empty, bool PreCancelled, bool InvalidBox, bool SplitMatches,
        bool CancelledInRun, double? CancellationMs, bool SingleCall, bool Recovered)
    {
        internal bool Passed => Empty && PreCancelled && InvalidBox && SplitMatches && CancelledInRun &&
            CancellationMs is >= 0 and < 2000 && SingleCall && Recovered;
    }

    private static BatchBoundaryResult CheckBatchBoundaries(PaddleOcrAll engine, PaddlePredictor predictor, Mat image, RotatedRect[] boxes, CancellationToken token)
    {
        var empty = RecognizeBatchedSpans(engine, predictor, image, [], token).Count == 0;
        var preCancelled = false;
        try
        { _ = RecognizeBatchedSpans(engine, predictor, image, [], new CancellationToken(true)); }
        catch (OperationCanceledException) { preCancelled = true; }
        var invalidBox = false;
        try
        {
            // A valid first crop ensures failure cleanup handles partially acquired resources.
            _ = RecognizeBatchedSpans(engine, predictor, image,
                [boxes[0], new RotatedRect(new Point2f(-10000, -10000), new Size2f(10, 10), 0)], token);
        }
        catch (ArgumentException) { invalidBox = true; }

        var repeated = Enumerable.Range(0, 9).Select(index => boxes[index % boxes.Length]).ToArray();
        var inputs = new List<Mat>();
        try
        {
            foreach (var box in repeated)
            {
                var rect = box.BoundingRect();
                inputs.Add(image[Rect.FromLTRB(Math.Clamp(rect.Left, 0, image.Width), Math.Clamp(rect.Top, 0, image.Height),
                    Math.Clamp(rect.Right, 0, image.Width), Math.Clamp(rect.Bottom, 0, image.Height))]);
            }
            var expected = engine.Recognizer.Run(inputs.ToArray());
            bool Matches(List<ReusedSpan> spans) => spans.Count == expected.Length && spans.Select((span, index) =>
                span.Text == expected[index].Text && float.IsFinite(span.Score) && Math.Abs(span.Score - expected[index].Score) <= 1e-6f).All(match => match);
            var splitMatches = Matches(RecognizeBatchedSpans(engine, predictor, image, repeated, token));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var observation = new CancellationObservation(cancellation);
            var rejected = false;
            try
            { _ = RecognizeBatchedSpans(engine, predictor, image, repeated, cancellation.Token, observation); }
            catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token) { rejected = true; }
            var completed = Stopwatch.GetTimestamp();
            observation.Disarm();
            token.ThrowIfCancellationRequested();
            var inside = rejected && observation.Requested > observation.Started && observation.Requested < observation.Finished;
            var elapsed = observation.Requested == 0 ? (double?)null : Stopwatch.GetElapsedTime(observation.Requested, completed).TotalMilliseconds;
            var recovered = Matches(RecognizeBatchedSpans(engine, predictor, image, repeated, token));
            return new BatchBoundaryResult(empty, preCancelled, invalidBox, splitMatches, inside, elapsed, observation.Calls == 1, recovered);
        }
        finally
        {
            foreach (var input in inputs)
            { input.Dispose(); }
        }
    }
}

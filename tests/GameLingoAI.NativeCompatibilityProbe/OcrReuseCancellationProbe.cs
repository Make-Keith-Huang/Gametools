using System.Diagnostics;
using System.Text.Json;
using GameLingoAI.Infrastructure.Ocr;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;

internal static partial class OcrReuseProbe
{
    internal static async Task<int> RunCancellationAsync(OcrModelPackage package, string installation, CancellationToken token)
    {
        var root = Environment.GetEnvironmentVariable("GAMELINGOAI_PROBE_REPORT_ROOT")
            ?? throw new InvalidOperationException("Explicit report directory required.");
        using var rendered = await SyntheticCorpus.RenderAsync("HELLO WORLD 123", "normal");
        using var bgra = Mat.FromPixelData(160, 1000, MatType.CV_8UC4, rendered.Pixels.ToArray());
        using var image = new Mat();
        Cv2.CvtColor(bgra, image, ColorConversionCodes.BGRA2BGR);
        using var runtime = new SdcbPaddleOcrRuntimeFactory(Math.Clamp(Environment.ProcessorCount / 2, 1, 8), reusePredictors: false).Create(package, installation);
        var engine = Field<PaddleOcrAll>(runtime, "_engine");
        using var detector = Field<PaddlePredictor>(engine.Detector, "_p").Clone();
        using var recognizer = Field<PaddlePredictor>(engine.Recognizer, "_p").Clone();
        string Recognize() => RecognizeCrops(engine, recognizer, image, Detect(engine.Detector, detector, image, token), token);
        static bool Correct(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c))) == "HELLOWORLD123";
        if (!Correct(Recognize()))
        { throw new InvalidOperationException("Cancellation fixture warm-up failed."); }

        var samples = new List<object>();
        var failures = 0;
        for (var index = 0; index < 12; index++)
        {
            token.ThrowIfCancellationRequested();
            var detectorStage = index % 2 == 0;
            // Prepare recognition boxes before arming its cancellation timer.
            var boxes = detectorStage ? [] : Detect(engine.Detector, detector, image, token);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var observation = new CancellationObservation(cancellation);
            var rejected = false;
            try
            {
                if (detectorStage)
                { _ = Detect(engine.Detector, detector, image, cancellation.Token, observation); }
                else
                { _ = RecognizeCrops(engine, recognizer, image, boxes, cancellation.Token, observation); }
            }
            catch (OperationCanceledException error) when (error.CancellationToken == cancellation.Token)
            { rejected = true; }
            var completed = Stopwatch.GetTimestamp();
            // Disarm and join the registration before reading cross-thread timestamps.
            observation.Disarm();
            token.ThrowIfCancellationRequested();
            var insideRun = observation.Requested > observation.Started && observation.Requested < observation.Finished;
            var cancellationMs = observation.Requested == 0 ? (double?)null :
                Stopwatch.GetElapsedTime(observation.Requested, completed).TotalMilliseconds;
            // Never dispose or re-enter a predictor until the synchronous native call has returned.
            var recovered = Correct(Recognize());
            var passed = rejected && insideRun && cancellationMs is >= 0 and < 2000 && recovered;
            if (!passed)
            { failures++; }
            samples.Add(new { index, stage = detectorStage ? "detector" : "recognizer", rejected, insideRun, cancellationMs, recovered, passed });
        }
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "ocr-reuse-cancellation.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            scope = "Warm serial native reuse; cancellation observed between managed Run entry/return timestamps, not native instruction tracing or forced interruption; not full-app stop acceptance",
            failures,
            samples
        }, JsonOptions), token);
        Console.WriteLine($"Native cancellation/recovery: {12 - failures}/12 passed; report: {path}");
        return failures == 0 ? 0 : 3;
    }

    private sealed class CancellationObservation : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly CancellationTokenRegistration _registration;
        internal long Started { get; private set; }
        internal long Finished { get; private set; }
        internal long Requested;
        internal int Calls { get; private set; }

        internal CancellationObservation(CancellationTokenSource source)
        {
            _source = source;
            _registration = source.Token.Register(() => Interlocked.Exchange(ref Requested, Stopwatch.GetTimestamp()));
        }

        internal void Begin()
        {
            Calls++;
            Started = Stopwatch.GetTimestamp();
            _source.CancelAfter(TimeSpan.FromMilliseconds(5));
        }

        internal void End() => Finished = Stopwatch.GetTimestamp();

        internal void Disarm()
        {
            _source.CancelAfter(Timeout.InfiniteTimeSpan);
            _registration.Dispose();
        }

        public void Dispose() => Disarm();
    }
}

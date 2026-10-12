using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Preprocessing;

internal static class PreprocessingTests
{
    private const string ExpectedOutlinedHash = "047576BFE72B394F759793FB148E29E9C1583C44BD8FAFEDD41BE2AC516BB11B";

    public static async Task RunAsync()
    {
        await TestGoldenAndNoOpAsync().ConfigureAwait(false);
        await TestEdgesAndFuzzedDimensionsAsync().ConfigureAwait(false);
        await TestPoolOwnershipOnCancellationFailureAndLimitsAsync().ConfigureAwait(false);
        TestPresetProfileSerialization();
        await RunReferenceBenchmarkAsync().ConfigureAwait(false);
    }

    private static async Task TestGoldenAndNoOpAsync()
    {
        var (frame, region) = LoadOutlinedFixture();
        using (frame)
        {
            var pool = new CountingPixelBufferPool();
            var processor = CreateProcessor(pool);
            using (var noOp = await processor.ProcessAsync(
                       frame,
                       new[] { region },
                       PreprocessingOptions.NoOp,
                       CancellationToken.None).ConfigureAwait(false))
            {
                var image = noOp.Regions.Single();
                Assert(image.PixelFormat == OcrPixelFormat.Bgra32, "No-op preprocessing must preserve BGRA format.");
                Assert(image.Width == frame.Width && image.Height == frame.Height,
                    "No-op preprocessing must preserve full-region dimensions.");
                Assert(image.Pixels.Span.SequenceEqual(frame.BgraPixels.Span),
                    "No-op preprocessing must preserve cropped source pixels exactly.");
            }

            Assert(pool.Outstanding == 0, "No-op output disposal must return its pooled buffer.");

            using (var outlined = await processor.ProcessAsync(
                       frame,
                       new[] { region },
                       PreprocessingOptions.OutlinedSubtitle,
                       CancellationToken.None).ConfigureAwait(false))
            {
                var image = outlined.Regions.Single();
                var hash = Convert.ToHexString(SHA256.HashData(image.Pixels.Span));
                Assert(hash == ExpectedOutlinedHash, $"Outlined golden hash changed: {hash}.");
                Assert(image.PixelFormat == OcrPixelFormat.Gray8, "Outlined preset should produce Gray8.");
                Assert(image.Width == 80 && image.Height == 40, "Outlined preset scale/padding dimensions changed.");
            }

            Assert(pool.Outstanding == 0 && pool.RentCount == pool.ReturnCount,
                "Golden processing must return every rented buffer.");
        }
    }

    private static async Task TestEdgesAndFuzzedDimensionsAsync()
    {
        var random = new Random(8_008);
        var pool = new CountingPixelBufferPool();
        var processor = CreateProcessor(pool);
        for (var iteration = 0; iteration < 300; iteration++)
        {
            var width = random.Next(8, 97);
            var height = random.Next(8, 97);
            using var frame = CreatePatternFrame(width, height);
            var regionWidth = Math.Max(1d / width, random.NextDouble());
            var regionHeight = Math.Max(1d / height, random.NextDouble());
            regionWidth = Math.Min(regionWidth, 1);
            regionHeight = Math.Min(regionHeight, 1);
            var x = random.NextDouble() * (1 - regionWidth);
            var y = random.NextDouble() * (1 - regionHeight);
            var region = new CaptureRegion(
                Guid.NewGuid(),
                "Fuzz",
                new NormalizedRegion(x, y, regionWidth, regionHeight),
                0);
            var options = (iteration % 3) switch
            {
                0 => PreprocessingOptions.NoOp,
                1 => PreprocessingOptions.PlainSubtitle,
                _ => PreprocessingOptions.OutlinedSubtitle
            };
            using var output = await processor.ProcessAsync(
                frame,
                new[] { region },
                options,
                CancellationToken.None).ConfigureAwait(false);
            var image = output.Regions.Single();
            Assert(image.Width > 0 && image.Height > 0, "Fuzzed edge crops must remain positive.");
            Assert(image.Pixels.Length == image.Width * image.Height *
                (image.PixelFormat == OcrPixelFormat.Bgra32 ? 4 : 1),
                "Fuzzed output storage must match dimensions.");
        }

        Assert(pool.Outstanding == 0, "Fuzzed processing must not retain pooled memory.");
    }

    private static async Task TestPoolOwnershipOnCancellationFailureAndLimitsAsync()
    {
        using var frame = CreatePatternFrame(256, 256);
        var region = FullRegion();

        using (var cancellation = new CancellationTokenSource())
        {
            var cancellationPool = new CountingPixelBufferPool { OnRent = cancellation.Cancel };
            var processor = CreateProcessor(cancellationPool);
            await AssertThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(
                frame,
                new[] { region },
                PreprocessingOptions.OutlinedSubtitle,
                cancellation.Token)).ConfigureAwait(false);
            Assert(cancellationPool.Outstanding == 0, "Cancelled processing must return every rented buffer.");
        }

        var diagnosticPool = new CountingPixelBufferPool();
        var failingProcessor = new FramePreprocessor(diagnosticPool, new ThrowingDiagnosticSink());
        await AssertThrowsAsync<InvalidOperationException>(() => failingProcessor.ProcessAsync(
            frame,
            new[] { region },
            PreprocessingOptions.PlainSubtitle with { EnableDiagnosticImages = true },
            CancellationToken.None)).ConfigureAwait(false);
        Assert(diagnosticPool.Outstanding == 0, "Diagnostic failure must dispose completed region outputs.");

        var limitPool = new CountingPixelBufferPool();
        var limited = PreprocessingOptions.PlainSubtitle with
        {
            Scale = 4,
            MaximumOutputWidth = 256,
            MaximumOutputHeight = 256,
            MaximumOutputPixels = 65_536
        };
        await AssertThrowsAsync<InvalidOperationException>(() => CreateProcessor(limitPool).ProcessAsync(
            frame,
            new[] { region },
            limited,
            CancellationToken.None)).ConfigureAwait(false);
        Assert(limitPool.Outstanding == 0, "Resolution-cap failure must return intermediate buffers.");
        Assert(limitPool.AllReturnsRequestedClear, "Sensitive pixel buffers must request clearing on return.");
    }

    private static void TestPresetProfileSerialization()
    {
        var profile = new GameProfile(
            ProfileId.New(),
            "Outlined game",
            "ja",
            "zh-Hans",
            8,
            new[] { FullRegion() },
            false,
            PreprocessingOptions.OutlinedSubtitle);
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var json = JsonSerializer.Serialize(profile, options);
        var roundTrip = JsonSerializer.Deserialize<GameProfile>(json, options);
        Assert(roundTrip?.Preprocessing.Preset == PreprocessingPreset.OutlinedSubtitle,
            "Profile JSON must preserve the preprocessing preset.");
        Assert(roundTrip!.Preprocessing.Threshold == 160 && roundTrip.Preprocessing.Scale == 2,
            "Profile JSON must preserve preprocessing parameters.");
    }

    private static async Task RunReferenceBenchmarkAsync()
    {
        using var frame = CreatePatternFrame(640, 360);
        var region = new CaptureRegion(
            Guid.NewGuid(),
            "Subtitle band",
            new NormalizedRegion(0.05, 0.65, 0.9, 0.25),
            0);
        var processor = CreateProcessor(new SharedPixelBufferPool());
        using (var warmup = await processor.ProcessAsync(
                   frame,
                   new[] { region },
                   PreprocessingOptions.OutlinedSubtitle,
                   CancellationToken.None).ConfigureAwait(false))
        {
        }

        var timings = new List<double>();
        for (var iteration = 0; iteration < 25; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            using var output = await processor.ProcessAsync(
                frame,
                new[] { region },
                PreprocessingOptions.OutlinedSubtitle,
                CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var median = timings[timings.Count / 2];
        var p95 = timings[(int)Math.Ceiling(timings.Count * 0.95) - 1];
        Console.WriteLine(
            $"Preprocessing benchmark: {Environment.ProcessorCount} logical CPU(s), " +
            $"640x360 frame/576x90 crop, outlined 2x preset, p50={median:F2} ms, p95={p95:F2} ms.");
        Assert(median <= 50, $"Preprocessing median {median:F2} ms exceeded the 50 ms stage budget.");
        Assert(p95 <= 150, $"Preprocessing p95 {p95:F2} ms exceeded the 150 ms regression ceiling.");
    }

    private static FramePreprocessor CreateProcessor(IPixelBufferPool pool) =>
        new(pool, new DisabledPreprocessingDiagnosticSink());

    private static (CaptureFrame Frame, CaptureRegion Region) LoadOutlinedFixture()
    {
        var path = FindFixture("outlined-subtitle.txt");
        var rows = File.ReadAllLines(path).Where(line => line.Length > 0).ToArray();
        var width = rows[0].Length;
        Assert(rows.All(row => row.Length == width), "Synthetic fixture rows must have equal width.");
        var pixels = new byte[width * rows.Length * 4];
        for (var y = 0; y < rows.Length; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = rows[y][x] == '#' ? byte.MaxValue : byte.MinValue;
                var offset = ((y * width) + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        return (CreateFrame(width, rows.Length, pixels), FullRegion());
    }

    private static CaptureFrame CreatePatternFrame(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var value = (byte)((index / 4 * 37) % 256);
            pixels[index] = value;
            pixels[index + 1] = (byte)(255 - value);
            pixels[index + 2] = (byte)(value / 2);
            pixels[index + 3] = byte.MaxValue;
        }

        return CreateFrame(width, height, pixels);
    }

    private static CaptureFrame CreateFrame(int width, int height, byte[] pixels) => new(
        new PipelineMetadata(SessionId.New(), new SequenceNumber(0), DateTimeOffset.UnixEpoch, CorrelationId.New()),
        width,
        height,
        pixels);

    private static CaptureRegion FullRegion() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "Full fixture",
        new NormalizedRegion(0, 0, 1, 1),
        0);

    private static string FindFixture(string name)
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "fixtures", "preprocessing", name);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        throw new FileNotFoundException($"Preprocessing fixture '{name}' was not found.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class CountingPixelBufferPool : IPixelBufferPool
    {
        private int _outstanding;

        public Action? OnRent { get; init; }

        public int RentCount { get; private set; }

        public int ReturnCount { get; private set; }

        public int Outstanding => Volatile.Read(ref _outstanding);

        public bool AllReturnsRequestedClear { get; private set; } = true;

        public byte[] Rent(int minimumLength)
        {
            RentCount++;
            Interlocked.Increment(ref _outstanding);
            OnRent?.Invoke();
            return new byte[minimumLength];
        }

        public void ReturnBuffer(byte[] buffer, bool clearBuffer)
        {
            ReturnCount++;
            AllReturnsRequestedClear &= clearBuffer;
            if (clearBuffer)
            {
                Array.Clear(buffer);
            }

            Interlocked.Decrement(ref _outstanding);
        }
    }

    private sealed class ThrowingDiagnosticSink : IPreprocessingDiagnosticSink
    {
        public ValueTask WriteAsync(
            PipelineMetadata metadata,
            PreprocessedRegionImage image,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("synthetic diagnostic failure"));
    }
}

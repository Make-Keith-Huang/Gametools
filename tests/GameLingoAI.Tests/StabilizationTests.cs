using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

internal static class StabilizationTests
{
    private static readonly Guid FirstRegion = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecondRegion = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly ProfileId Profile = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));

    public static async Task RunAsync()
    {
        TestLatinAndCjkNormalization();
        TestOrderingProperty();
        await TestStableChangedDisappearedAndRedisplayAsync().ConfigureAwait(false);
        await TestJitterConfidenceWindowScopeResetAndStaleInputAsync().ConfigureAwait(false);
        await TestMemoryBoundsAndCancellationAsync().ConfigureAwait(false);
    }

    private static void TestLatinAndCjkNormalization()
    {
        var context = Context("en", new[]
        {
            new OcrArtifactReplacement("0CR", "OCR")
        });
        var result = Result(1,
            Span("Next", 0.9, 0.1, 0.3, FirstRegion),
            Span("world", 0.9, 0.3, 0.1, FirstRegion),
            Span("，", 0.9, 0.24, 0.1, FirstRegion),
            Span("Ｈｅｌｌｏ", 0.9, 0.1, 0.1, FirstRegion),
            Span("0CR", 0.9, 0.1, 0.1, SecondRegion));
        var normalized = OcrTextNormalizer.Normalize(result, context, 128)
            ?? throw new InvalidOperationException("Latin normalization returned no text.");
        Assert(normalized.Text == "Hello, world\nNext\nOCR",
            $"Latin order/punctuation normalization changed: '{normalized.Text}'.");
        Assert(normalized.OrderedSpans[0].Text == "Ｈｅｌｌｏ" && normalized.OrderedSpans[^1].Text == "0CR",
            "Source spans must be ordered by configured region and reading position.");

        var cjk = Result(2,
            Span("界", 0.9, 0.5, 0.1, FirstRegion),
            Span("你", 0.9, 0.1, 0.1, FirstRegion),
            Span("，", 0.9, 0.3, 0.1, FirstRegion),
            Span("好", 0.9, 0.2, 0.1, FirstRegion),
            Span("世", 0.9, 0.4, 0.1, FirstRegion));
        var cjkNormalized = OcrTextNormalizer.Normalize(cjk, Context("zh-Hans"), 128)
            ?? throw new InvalidOperationException("CJK normalization returned no text.");
        Assert(cjkNormalized.Text == "你好，世界", "CJK fragments must preserve punctuation and avoid artificial spaces.");
    }

    private static void TestOrderingProperty()
    {
        var canonical = new[]
        {
            Span("one", 0.9, 0.1, 0.1, FirstRegion),
            Span("two", 0.9, 0.3, 0.1, FirstRegion),
            Span("three", 0.9, 0.1, 0.3, FirstRegion),
            Span("four", 0.9, 0.1, 0.1, SecondRegion)
        };
        var random = new Random(10_010);
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var shuffled = canonical.OrderBy(_ => random.Next()).ToArray();
            var normalized = OcrTextNormalizer.Normalize(Result(iteration + 10, shuffled), Context("en"), 128);
            Assert(normalized?.Text == "one two\nthree\nfour", "Span input order must not affect normalized reading order.");
        }
    }

    private static async Task TestStableChangedDisappearedAndRedisplayAsync()
    {
        var clock = new FakeClock();
        var stabilizer = CreateStabilizer(clock);
        var context = Context("en");

        Assert(await ObserveAsync(stabilizer, context, Result(1, Span("Hello", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "One ordinary observation must not emit.");
        clock.Advance(100);
        var created = await ObserveAsync(stabilizer, context, Result(2, Span("Hello", 0.92, 0.101, 0.1, FirstRegion)));
        Assert(created is { Kind: StableTextEventKind.New, Text: "Hello", ObservationCount: 2 },
            "Repeated stable text must emit one New event.");
        clock.Advance(100);
        Assert(await ObserveAsync(stabilizer, context, Result(3, Span("Hello", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Unchanged stable text must be deduplicated.");

        Assert(await ObserveAsync(stabilizer, context, Result(4, Span("Hello world", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Incremental growth must wait for confirmation.");
        clock.Advance(100);
        var growth = await ObserveAsync(stabilizer, context, Result(5, Span("Hello world", 0.9, 0.1, 0.1, FirstRegion)));
        Assert(growth is { Kind: StableTextEventKind.Changed, Text: "Hello world" },
            "Confirmed incremental growth must emit Changed.");

        clock.Advance(50);
        var promptChange = await ObserveAsync(stabilizer, context, Result(6, Span("Goodbye!", 0.99, 0.1, 0.1, FirstRegion)));
        Assert(promptChange is { Kind: StableTextEventKind.Changed, Text: "Goodbye!" },
            "A high-confidence different line must change promptly.");

        Assert(await ObserveAsync(stabilizer, context, Result(7)) is null,
            "A single empty/noisy frame must not hide stable text.");
        var disappeared = await ObserveAsync(stabilizer, context, Result(8));
        Assert(disappeared is { Kind: StableTextEventKind.Disappeared, Text: null },
            "Confirmed absence must emit Disappeared.");
        var disappearedEvent = disappeared ?? throw new InvalidOperationException("Disappearance event was null.");
        Assert(disappearedEvent.Metadata.Sequence == new SequenceNumber(8),
            "Disappearance must carry the triggering pipeline sequence.");

        Assert(await ObserveAsync(stabilizer, context, Result(9, Span("Goodbye!", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Redisplay must be stabilized again after disappearance.");
        var redisplay = await ObserveAsync(stabilizer, context, Result(10, Span("Goodbye!", 0.9, 0.1, 0.1, FirstRegion)));
        Assert(redisplay is { Kind: StableTextEventKind.New }, "Redisplayed text must emit New after disappearance.");
    }

    private static async Task TestJitterConfidenceWindowScopeResetAndStaleInputAsync()
    {
        var clock = new FakeClock();
        var stabilizer = CreateStabilizer(clock);
        var context = Context("en");
        _ = await ObserveAsync(stabilizer, context, Result(1, Span("A rather long subtitle", 0.9, 0.1, 0.1, FirstRegion)));
        clock.Advance(100);
        var created = await ObserveAsync(stabilizer, context, Result(2, Span("A rather long subtitle", 0.9, 0.103, 0.102, FirstRegion)));
        Assert(created?.Kind == StableTextEventKind.New, "Small spatial jitter must still stabilize.");

        var stale = await ObserveAsync(stabilizer, context, Result(1, Span("STALE", 1, 0.1, 0.1, FirstRegion)));
        Assert(stale is null, "A stale sequence must not mutate or emit state.");
        var similarNoise = await ObserveAsync(stabilizer, context, Result(3, Span("A rather long subtit1e", 0.9, 0.1, 0.1, FirstRegion)));
        Assert(similarNoise is null, "A close one-character OCR jitter must not translate again.");

        var lowNoise = await ObserveAsync(stabilizer, context, Result(4, Span("garbage", 0.2, 0.1, 0.1, FirstRegion)));
        Assert(lowNoise is null, "One low-confidence noise frame must be suppressed.");
        Assert(await ObserveAsync(stabilizer, context, Result(5, Span("A rather long subtitle", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Stable text must survive one low-confidence noise frame.");

        await stabilizer.ResetAsync(CancellationToken.None);
        _ = await ObserveAsync(stabilizer, context, Result(10, Span("Windowed", 0.9, 0.1, 0.1, FirstRegion)));
        clock.Advance(700);
        Assert(await ObserveAsync(stabilizer, context, Result(11, Span("Windowed", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Observations outside the bounded window must not combine.");
        clock.Advance(100);
        Assert((await ObserveAsync(stabilizer, context, Result(12, Span("Windowed", 0.9, 0.1, 0.1, FirstRegion))))?.Kind == StableTextEventKind.New,
            "Two fresh observations inside the window must stabilize.");

        var changedScope = Context("ja");
        Assert(await ObserveAsync(stabilizer, changedScope, Result(13, Span("字幕", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Language change must reset prior state.");
        Assert((await ObserveAsync(stabilizer, changedScope, Result(14, Span("字幕", 0.9, 0.1, 0.1, FirstRegion))))?.Kind == StableTextEventKind.New,
            "Changed scope must establish a fresh stable subtitle.");

        var changedRegions = new StabilizationContext(Profile, "ja", new[] { SecondRegion, FirstRegion });
        Assert(await ObserveAsync(stabilizer, changedRegions, Result(15, Span("字幕", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Region-order change must reset state.");

        var changedProfile = new StabilizationContext(ProfileId.New(), "ja", new[] { SecondRegion, FirstRegion });
        Assert(await ObserveAsync(stabilizer, changedProfile, Result(16, Span("字幕", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Profile change must reset state.");
        var nextSession = new SessionId(Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"));
        Assert(await ObserveAsync(
            stabilizer,
            changedProfile,
            Result(nextSession, 1, Span("字幕", 0.9, 0.1, 0.1, FirstRegion))) is null,
            "Session change must reset state even when its sequence restarts.");

        var spatial = CreateStabilizer(clock);
        _ = await ObserveAsync(spatial, context, Result(20, Span("same", 0.9, 0.1, 0.1, FirstRegion)));
        _ = await ObserveAsync(spatial, context, Result(21, Span("same", 0.9, 0.1, 0.1, FirstRegion)));
        Assert(await ObserveAsync(spatial, context, Result(22, Span("same", 0.9, 0.7, 0.7, FirstRegion))) is null,
            "The same text at a disjoint location must require fresh spatial confirmation.");
        var spatialChange = await ObserveAsync(spatial, context, Result(23, Span("same", 0.9, 0.7, 0.7, FirstRegion)));
        Assert(spatialChange?.Kind == StableTextEventKind.Changed,
            "Confirmed relocation must emit a spatial Changed event.");
    }

    private static async Task TestMemoryBoundsAndCancellationAsync()
    {
        var clock = new FakeClock();
        var options = new StabilizationOptions
        {
            RequiredObservations = 10,
            MaximumCandidates = 3,
            MaximumSpansPerObservation = 4
        };
        var stabilizer = new TemporalTextStabilizer(clock, options);
        var context = Context("en");
        for (var index = 0; index < 100; index++)
        {
            var spans = Enumerable.Range(0, 20)
                .Select(span => Span($"candidate-{index}-{span}", 0.8, 0.01 + (span * 0.02), 0.1, FirstRegion))
                .ToArray();
            _ = await ObserveAsync(stabilizer, context, Result(index + 1, spans));
            Assert(stabilizer.CandidateCount <= 3, "Candidate history must stay within its configured hard bound.");
            clock.Advance(1);
        }

        var boundedNormalization = OcrTextNormalizer.Normalize(
            Result(150, Enumerable.Range(0, 20)
                .Select(index => Span($"span-{index}", 0.8, 0.01 + (index * 0.02), 0.1, FirstRegion))
                .ToArray()),
            context,
            maximumSpans: 4);
        Assert(boundedNormalization?.OrderedSpans.Count == 4,
            "Normalizer must enforce the configured per-observation span bound.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await stabilizer.ObserveAsync(Result(200), context, cancelled.Token));
        await AssertThrowsAsync<OperationCanceledException>(async () =>
            await stabilizer.ResetAsync(cancelled.Token));
    }

    private static TemporalTextStabilizer CreateStabilizer(FakeClock clock) => new(
        clock,
        new StabilizationOptions());

    private static StabilizationContext Context(
        string language,
        IReadOnlyList<OcrArtifactReplacement>? replacements = null) =>
        new(Profile, language, new[] { FirstRegion, SecondRegion }, replacements);

    private static async Task<StableTextEvent?> ObserveAsync(
        TemporalTextStabilizer stabilizer,
        StabilizationContext context,
        OcrResult result) =>
        await stabilizer.ObserveAsync(result, context, CancellationToken.None);

    private static OcrResult Result(long sequence, params OcrTextSpan[] spans) => Result(
        new SessionId(Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA")),
        sequence,
        spans);

    private static OcrResult Result(SessionId sessionId, long sequence, params OcrTextSpan[] spans) => new(
        new PipelineMetadata(
            sessionId,
            new SequenceNumber(sequence),
            DateTimeOffset.UnixEpoch.AddMilliseconds(sequence),
            CorrelationId.New()),
        spans);

    private static OcrTextSpan Span(
        string text,
        double confidence,
        double x,
        double y,
        Guid regionId) =>
        new(text, confidence, new NormalizedRegion(x, y, 0.12, 0.08), regionId);

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

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

        public void Advance(int milliseconds) => UtcNow = UtcNow.AddMilliseconds(milliseconds);
    }
}

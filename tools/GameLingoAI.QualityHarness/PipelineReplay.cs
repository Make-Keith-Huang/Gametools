using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using GameLingoAI.Infrastructure.Cache;
using GameLingoAI.Infrastructure.Preprocessing;

namespace GameLingoAI.QualityHarness;

public sealed record ReplayRunReport(string CorpusVersion, string CorpusSha256, int Frames,
    SessionMetrics Pipeline, LatencySummary EndToEnd, double WallMilliseconds, long AllocatedBytes,
    double CpuMilliseconds, long PrivateBytesBefore, long PrivateBytesAfter, int ReleasedFrames)
{
    public double? FrameDropRate => Frames == 0 ? null : Pipeline.DroppedFrames / (double)Frames;
    public double? AllocatedBytesPerFrame => Frames == 0 ? null : AllocatedBytes / (double)Frames;
}

/// <summary>Real controller/preprocessing/stabilization/cache; synthetic capture/OCR/provider and headless presentation.</summary>
public static class PipelineReplay
{
    private static readonly Guid Region = Guid.Parse("23000000-0000-0000-0000-000000000002");

    /// <summary>One continuous synthetic session, or a stopped controller for idle; no restart loop or virtual time.</summary>
    public static async Task<ReplayRunReport> RunForAsync(bool active, TimeSpan duration, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(duration, active ? TimeSpan.FromHours(1) : TimeSpan.FromHours(8));
        token.ThrowIfCancellationRequested();
        var corpus = new ReplayCorpus(100_000);
        using var tracker = new Tracker(corpus.Count);
        var clock = new ReplayClock();
        var provider = new ScriptedTranslationProvider([new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.Success)]);
        await using var controller = new SessionController(new Factory(corpus, tracker, clock, provider),
            new HeadlessPresenter(), diagnostics: tracker);
        using var process = Process.GetCurrentProcess();
        var before = process.PrivateMemorySize64;
        var cpu = process.TotalProcessorTime;
        var allocated = GC.GetTotalAllocatedBytes();
        var watch = Stopwatch.StartNew();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (active)
            { await controller.StartAsync(Request(), token).ConfigureAwait(false); }
            var timer = Task.Delay(duration, lifetime.Token);
            if (active && await Task.WhenAny(timer, tracker.Completion.Task).ConfigureAwait(false) == tracker.Completion.Task)
            {
                await tracker.Completion.Task.ConfigureAwait(false);
                throw new InvalidOperationException("Soak exhausted its continuous workload early.");
            }
            await timer.ConfigureAwait(false);
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await controller.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        if (controller.Failure is not null || tracker.Released != tracker.CapturedCount)
        { throw new InvalidOperationException("Soak pipeline failed or retained generated frames."); }
        process.Refresh();
        return new(ReplayCorpus.Version, tracker.InputHash(), tracker.CapturedCount, controller.Metrics,
            tracker.EndToEnd.Snapshot(), watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes() - allocated,
            (process.TotalProcessorTime - cpu).TotalMilliseconds, before, process.PrivateMemorySize64, tracker.Released);
    }

    public static async Task<ReplayRunReport> RunAsync(int frames, CancellationToken cancellationToken,
        IReadOnlyList<ProviderStep>? providerScript = null)
    {
        // Whole eight-frame scenes ensure the final scene can stabilize before the completion barrier.
        if (frames is < 8 or > 100_000 || frames % 8 != 0)
        { throw new ArgumentOutOfRangeException(nameof(frames)); }
        cancellationToken.ThrowIfCancellationRequested();
        var corpus = new ReplayCorpus(frames);
        using var tracker = new Tracker(frames);
        var clock = new ReplayClock();
        var provider = new ScriptedTranslationProvider(providerScript ?? [new(TimeSpan.FromMilliseconds(100), SimulatedOutcome.Success)]);
        var factory = new Factory(corpus, tracker, clock, provider);
        await using var controller = new SessionController(factory, new HeadlessPresenter(), diagnostics: tracker);
        using var process = Process.GetCurrentProcess();
        var privateBefore = process.PrivateMemorySize64;
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes();
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(frames * 200L + 30_000));
        try
        {
            await controller.StartAsync(Request(), timeout.Token).ConfigureAwait(false);
            await tracker.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally { await controller.StopAsync(CancellationToken.None).ConfigureAwait(false); }
        if (controller.Failure is not null)
        { throw new InvalidOperationException("Replay stopped with a pipeline failure."); }
        watch.Stop();
        process.Refresh();
        var report = new ReplayRunReport(ReplayCorpus.Version, tracker.InputHash(), frames, controller.Metrics,
            tracker.EndToEnd.Snapshot(), watch.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes() - allocatedBefore,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds, privateBefore, process.PrivateMemorySize64,
            tracker.Released);
        if (tracker.Released != frames)
        { throw new InvalidOperationException("Replay did not release every generated frame."); }
        return report;
    }

    private static SessionStartRequest Request()
    {
        // No native handle is opened: the fixture factory consumes this synthetic confirmed descriptor only.
        var target = new CaptureWindow(new WindowIdentity(new WindowId(1), 1, 1), "Synthetic replay", "Fixture",
            new PixelBounds(0, 0, ReplayCorpus.Width, ReplayCorpus.Height), 96, false);
        var selection = new WindowSelectionTracker();
        selection.Select(target);
        selection.Confirm([target]);
        return new SessionStartRequest(selection.ConfirmedSelection!, new GameProfile(
            new ProfileId(Region), "Replay", "en", "ja", 10,
            [new CaptureRegion(Region, "Synthetic region", new NormalizedRegion(.1, .7, .8, .2), 0)], false),
            new AppSettings(), new ProviderOptions { Id = "synthetic-script", Model = "fixture-v1" }, new GlossaryCatalog([]));
    }

    private sealed class Factory(ReplayCorpus corpus, Tracker tracker, ReplayClock clock, ScriptedTranslationProvider provider)
        : ISessionResourcesFactory
    {
        public Task<SessionResources> CreateAsync(SessionStartRequest request, SessionId sessionId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var source = new Source(corpus, sessionId, tracker);
            var ocr = new FixtureOcr();
            var cache = new CachedTranslationProvider(provider, new InMemoryTranslationCacheRepository(clock,
                new TranslationCachePolicy(maximumRows: 32)), clock,
                new TranslationCacheIdentity(provider.Id, "fixture-v1", "replay-v1"), new TranslationCachePolicy(maximumRows: 32));
            return Task.FromResult(new SessionResources(source,
                new FramePreprocessor(new SharedPixelBufferPool(), new DisabledPreprocessingDiagnosticSink()), ocr,
                new ObservedStabilizer(new TemporalTextStabilizer(clock, new StabilizationOptions()), clock, tracker),
                cache, cache, ocr, source));
        }
    }

    private sealed class Source(ReplayCorpus corpus, SessionId session, Tracker tracker) : ICaptureSource
    {
        public async IAsyncEnumerable<CaptureFrame> CaptureAsync([EnumeratorCancellation] CancellationToken token)
        {
            for (var index = 0; index < corpus.Count; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var inner = corpus.CreateFrame(index, token);
                var metadata = new PipelineMetadata(session, inner.Metadata.Sequence, inner.Metadata.CapturedAtUtc, inner.Metadata.CorrelationId);
                tracker.Captured(inner, started);
                yield return new CaptureFrame(metadata, inner.Width, inner.Height, inner.BgraPixels, new Owner(inner, tracker));
            }
            // A live capture EOF means target loss to the controller. Hold until the explicit drain barrier stops it.
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Owner(CaptureFrame frame, Tracker tracker) : IFrameLease
    {
        public void Dispose() { frame.Dispose(); Interlocked.Increment(ref tracker.Released); }
    }

    private sealed class ReplayClock : IClock
    {
        private long _ticks;
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddTicks(Interlocked.Read(ref _ticks));
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _ticks, (value - DateTimeOffset.UnixEpoch).Ticks);
    }

    private sealed class FixtureOcr : IOcrEngine
    {
        private static readonly string[] Texts = ["ALPHA", "BRAVO", "CHARLIE"];
        public OcrEngineCapabilities Capabilities => new(new HashSet<OcrLanguage> { OcrLanguage.English },
            new HashSet<OcrExecutionDevice> { OcrExecutionDevice.Cpu }, true, false);
        public Task<OperationResult<OcrResult>> RecognizeAsync(PreprocessedFrame frame, OcrRecognitionOptions options, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var scene = frame.Metadata.Sequence.Value / 8;
            return Task.FromResult(OperationResult.Success(new OcrResult(frame.Metadata, scene % 4 == 3 ? [] :
                [new OcrTextSpan(Texts[scene % Texts.Length], .99, new NormalizedRegion(.1, .78, .8, .08), Region)])));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ObservedStabilizer(TemporalTextStabilizer inner, ReplayClock clock, Tracker tracker) : ITextStabilizer
    {
        public async ValueTask<StableTextEvent?> ObserveAsync(OcrResult result, StabilizationContext context, CancellationToken token)
        {
            clock.Set(result.Metadata.CapturedAtUtc);
            var stable = await inner.ObserveAsync(result, context, token).ConfigureAwait(false);
            tracker.Observed(result.Metadata.Sequence.Value, stable?.Metadata.Sequence.Value);
            return stable;
        }
        public ValueTask ResetAsync(CancellationToken token) => inner.ResetAsync(token);
    }

    private sealed class HeadlessPresenter : IOverlayPresenter
    {
        public Task ShowAsync(TranslationResult result, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public Task HideAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }

    private sealed class Tracker(int frames) : IDiagnosticEvents, IDisposable
    {
        private readonly object _sync = new();
        private readonly long[] _sequence = Enumerable.Repeat(-1L, 4096).ToArray();
        private readonly long[] _started = new long[4096];
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _observed = -1, _stable = -1, _presented = -1;
        public int Released;
        public int CapturedCount { get; private set; }
        public LatencyWindow EndToEnd { get; } = new();
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Captured(CaptureFrame frame, long started)
        {
            lock (_sync)
            {
                var sequence = frame.Metadata.Sequence.Value;
                var slot = sequence % _sequence.Length;
                _sequence[slot] = sequence;
                _started[slot] = started;
                Span<byte> metadata = stackalloc byte[16];
                BinaryPrimitives.WriteInt64LittleEndian(metadata, sequence);
                BinaryPrimitives.WriteInt64LittleEndian(metadata[8..], frame.Metadata.CapturedAtUtc.UtcTicks);
                _hash.AppendData(metadata);
                _hash.AppendData(frame.BgraPixels.Span);
                CapturedCount++;
            }
        }

        public void Observed(long sequence, long? stable)
        {
            lock (_sync)
            { _observed = sequence; if (stable is { } value) { _stable = value; } CheckComplete(); }
        }

        public bool TryWrite(DiagnosticEvent entry)
        {
            lock (_sync)
            {
                if (entry.Kind == DiagnosticEventKind.StageFailed && entry.Stage is null)
                { Completion.TrySetException(new InvalidOperationException("Replay session faulted: " + entry.ErrorCategory)); }
                if (entry.Kind == DiagnosticEventKind.StageCompleted && entry.Stage == PipelineStage.Presentation && entry.CorrelationId is { } correlation)
                {
                    var sequence = BinaryPrimitives.ReadInt64LittleEndian(correlation.Value.ToByteArray()) - 1;
                    var slot = sequence % _sequence.Length;
                    if (sequence < 0 || _sequence[slot] != sequence)
                    { Completion.TrySetException(new InvalidOperationException("Replay timing window lost a presentation.")); return false; }
                    EndToEnd.Record(Stopwatch.GetElapsedTime(_started[slot]).TotalMilliseconds);
                    _presented = sequence;
                    CheckComplete();
                }
                return true;
            }
        }

        private void CheckComplete()
        {
            if (_observed == frames - 1 && _stable >= 0 && _presented >= _stable)
            { Completion.TrySetResult(); }
        }
        public string InputHash() { lock (_sync) { return Convert.ToHexString(_hash.GetHashAndReset()); } }
        public void Dispose() => _hash.Dispose();
    }
}

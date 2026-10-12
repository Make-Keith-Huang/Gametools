using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

internal static class SessionPipelineTests
{
    internal static readonly Guid Region = Guid.Parse("b1000000-0000-0000-0000-000000000001");
    public static async Task RunAsync()
    {
        await LifecycleAsync();
        await PressureAndStaleAsync();
        await StartupCancellationAsync();
        await FaultsAndRecoveryAsync();
        await TransientRetryAsync();
        await SyntheticThirtyMinuteSoakAsync();
    }

    private static SessionStartRequest Request()
    {
        var window = new CaptureWindow(new WindowIdentity(new WindowId(1), 1, 1), "Synthetic", "Tests",
            new PixelBounds(0, 0, 100, 100), 96, false);
        var tracker = new WindowSelectionTracker();
        tracker.Select(window);
        tracker.Confirm([window]);
        return new SessionStartRequest(tracker.ConfirmedSelection!, new GameProfile(ProfileId.New(), "Test", "en", "ja",
            8, [new CaptureRegion(Region, "Dialogue", new NormalizedRegion(0, 0, 1, 1), 0)], false),
            new AppSettings(), new ProviderOptions { Id = "fake", Model = "model" }, new GlossaryCatalog([]));
    }

    private static async Task LifecycleAsync()
    {
        var factory = new Factory();
        var display = new Display();
        var time = new ManualTime();
        var diagnostics = new RecordingDiagnostics();
        await using var controller = new SessionController(factory, display, time, diagnostics);
        await controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => display.Current?.Sequence == 0);
        Assert(controller.State == SessionState.Running, "First frame must commit startup.");
        var firstId = display.Current!.Metadata.SessionId;
        await controller.PauseAsync(CancellationToken.None);
        Assert(controller.State == SessionState.Paused && display.Current is null, "Pause must drain and hide.");
        Assert(factory.Last!.Source.Active == 0 && factory.Last.Disposals == 0, "Pause keeps adapters but releases enumeration.");
        await controller.ResumeAsync(CancellationToken.None);
        await UntilAsync(() => display.Current?.Sequence == 1);
        Assert(display.Current!.Metadata.SessionId == firstId, "Resume preserves session identity and advances sequence.");
        var watch = Stopwatch.StartNew();
        await controller.StopAsync(CancellationToken.None);
        Assert(watch.Elapsed < TimeSpan.FromSeconds(2), "Stop exceeded two seconds.");
        Assert(controller.State == SessionState.Stopped && display.Current is null, "Stop must hide and finish.");
        factory.Last.AssertDisposed();
        var recorded = diagnostics.Events.ToArray();
        foreach (var kind in new[] { DiagnosticEventKind.SessionStarting, DiagnosticEventKind.SessionRunning,
            DiagnosticEventKind.SessionPaused, DiagnosticEventKind.SessionStopped, DiagnosticEventKind.StageCompleted })
        {
            Assert(recorded.Any(entry => entry.Kind == kind && entry.SessionId == firstId),
                "Session diagnostics lost lifecycle identity or presentation event.");
        }
        Assert(recorded.Any(entry => entry.Stage == PipelineStage.Presentation && entry.CorrelationId is not null),
            "Presentation diagnostic lost correlation.");
        var presented = recorded.First(entry => entry.Stage == PipelineStage.Presentation);
        Assert(recorded.Any(entry => entry.Stage == PipelineStage.Capture && entry.CorrelationId == presented.CorrelationId) &&
            recorded.Any(entry => entry.Stage == PipelineStage.Translation && entry.CorrelationId == presented.CorrelationId),
            "Capture/translation/presentation diagnostics do not identify the same event flow.");
        diagnostics.ThrowOnWrite = true;
        for (var restart = 0; restart < 20; restart++)
        {
            await controller.StartAsync(Request(), CancellationToken.None);
            var readers = Task.Run(() => Parallel.For(0, 100, _ =>
            {
                var snapshot = controller.Metrics;
                Assert(snapshot.SessionId is not null && snapshot.Stages.Count == 6 &&
                    snapshot.Stages.All(stage => stage.Count >= 0 && stage.P50Milliseconds >= 0 &&
                        stage.P95Milliseconds >= stage.P50Milliseconds), "Concurrent metric snapshot is inconsistent.");
                Assert(snapshot.Stages is IList<StageMeasurement> { IsReadOnly: true }, "Stage metrics snapshot can be mutated.");
            }));
            await UntilAsync(() => display.Current is not null);
            Assert(display.Current!.Metadata.SessionId != firstId, "Restart reused a session.");
            firstId = display.Current.Metadata.SessionId;
            await controller.StopAsync(CancellationToken.None);
            await readers;
            factory.Last!.AssertDisposed();
        }

        var metrics = controller.Metrics;
        Assert(metrics.LastCorrelationId is not null && metrics.Stages.All(stage => stage.Count > 0), "Every stage needs correlated timings.");
        Assert(metrics.CacheHits == 1, "Cache-hit outcome not measured.");
    }

    private static async Task PressureAndStaleAsync()
    {
        var time = new ManualTime();
        var factory = new Factory { BlockOcr = true };
        var display = new Display();
        await using var controller = new SessionController(factory, display, time);
        await controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => factory.Last!.OcrCalls == 1);
        for (var i = 0; i < 20; i++)
        {
            await NextFrameAsync(factory.Last!, time);
        }
        Assert(controller.Metrics.DroppedFrames >= 18, "Capacity-two frames failed to drop old work.");
        factory.Last!.OcrGate.TrySetResult();
        await UntilAsync(() => display.Current?.Sequence == 20);
        await controller.StopAsync(CancellationToken.None);
        factory.Last.AssertDisposed();

        factory.BlockOcr = false;
        factory.BlockTranslation = true;
        factory.IgnoreTranslationCancellation = true;
        await controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => factory.Last!.TranslationCalls == 1);
        for (var i = 0; i < 10; i++)
        {
            await NextFrameAsync(factory.Last!, time);
            var expected = i + 2;
            await UntilAsync(() => factory.Last!.StableCalls >= expected);
        }
        factory.Last!.TranslationGate.TrySetResult();
        await UntilAsync(() => display.Current?.Sequence == 10);
        Assert(!display.Seen.Contains((controller.Metrics.SessionId!.Value, 0)), "Old translation escaped after a newer subtitle.");
        Assert(controller.Metrics.DroppedText >= 9 && controller.Metrics.StaleResults > 0, "Latest-text policy not measured.");
        await controller.StopAsync(CancellationToken.None);
        factory.Last.AssertDisposed();

        factory.IgnoreTranslationCancellation = false;
        await controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => factory.Last!.TranslationCalls == 1);
        await NextFrameAsync(factory.Last!, time);
        await UntilAsync(() => factory.Last!.TranslationCalls == 2);
        await controller.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        factory.Last!.AssertDisposed();

        // Disappearance is delivered independently of a slow translation request.
        await controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => factory.Last!.TranslationCalls == 1);
        factory.Last!.Disappear = true;
        await NextFrameAsync(factory.Last, time);
        await UntilAsync(() => factory.Last.StableCalls >= 2);
        factory.Last.TranslationGate.TrySetResult();
        await UntilAsync(() => controller.Metrics.StaleResults > 0);
        Assert(display.Current is null, "Disappeared text must not be redisplayed by a late result.");
        await controller.StopAsync(CancellationToken.None);
        factory.Last.AssertDisposed();
    }

    private sealed class RecordingDiagnostics : IDiagnosticEvents
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<DiagnosticEvent> Events = new();
        public bool ThrowOnWrite;
        public bool TryWrite(DiagnosticEvent entry)
        {
            if (ThrowOnWrite)
            { throw new IOException("SECRET diagnostic failure"); }
            Events.Enqueue(entry);
            return true;
        }
    }

    private static async Task StartupCancellationAsync()
    {
        var factory = new Factory { BlockStartup = true };
        var display = new Display();
        await using var controller = new SessionController(factory, display);
        var start = controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => factory.Started);
        await controller.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        try
        { await start; throw new InvalidOperationException("Startup should have been cancelled."); }
        catch (OperationCanceledException) { }
        Assert(controller.State == SessionState.Stopped, "Stop did not interrupt pending acquisition.");
        factory.BlockStartup = false;
        factory.Fault = "capture";
        await controller.StartAsync(Request(), CancellationToken.None);
        Assert(controller.State == SessionState.Faulted && display.Current is null, "First-frame failure must roll back startup.");
        factory.Last!.AssertDisposed();
    }

    private static async Task FaultsAndRecoveryAsync()
    {
        foreach (var stage in new[] { "preprocess", "ocr", "stabilize", "translate", "metadata", "dispose", "present" })
        {
            var factory = new Factory { Fault = stage };
            var display = new Display { Fail = stage == "present" };
            await using var controller = new SessionController(factory, display);
            await controller.StartAsync(Request(), CancellationToken.None);
            if (stage == "dispose")
            { await controller.StopAsync(CancellationToken.None); }
            await UntilAsync(() => controller.State == SessionState.Faulted && factory.Last!.Disposals == 1);
            Assert(!controller.Failure!.Message.Contains("SECRET", StringComparison.Ordinal), "Raw exception leaked into failure.");
            await controller.StopAsync(CancellationToken.None);
            factory.Last!.AssertDisposed();
            factory.Fault = null;
            display.Fail = false;
            await controller.StartAsync(Request(), CancellationToken.None);
            await UntilAsync(() => display.Current is not null);
            await controller.StopAsync(CancellationToken.None);
            factory.Last!.AssertDisposed();
        }
    }

    private static async Task TransientRetryAsync()
    {
        var time = new ManualTime();
        var factory = new Factory { TransientTranslations = 2 };
        var display = new Display();
        await using var controller = new SessionController(factory, display, time);
        await controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => factory.Last!.TranslationCalls == 1 && time.Pending >= 2);
        time.Advance(TimeSpan.FromMilliseconds(250));
        await UntilAsync(() => factory.Last!.TranslationCalls == 2 && time.Pending >= 1);
        // Wait until the second backoff timer is registered, independent of worker scheduling.
        await UntilAsync(() => time.HasDelay(TimeSpan.FromMilliseconds(500)));
        time.Advance(TimeSpan.FromMilliseconds(500));
        await UntilAsync(() => display.Current is not null);
        Assert(controller.State == SessionState.Running && controller.Metrics.Errors == 2, "Transient retries did not recover.");
        await controller.StopAsync(CancellationToken.None);
        factory.Last!.AssertDisposed();
    }

    private static async Task SyntheticThirtyMinuteSoakAsync()
    {
        var time = new ManualTime();
        var factory = new Factory();
        var display = new Display { TrackHistory = false };
        await using var controller = new SessionController(factory, display, time);
        await controller.StartAsync(Request(), CancellationToken.None);
        await UntilAsync(() => display.Current is not null);
        var memoryBefore = GC.GetTotalMemory(true);
        var realWatch = Stopwatch.StartNew();
        for (var second = 1; second <= 1800; second++)
        {
            await NextFrameAsync(factory.Last!, time);
            var expected = second;
            await UntilAsync(() => display.Current?.Sequence == expected);
        }
        await controller.StopAsync(CancellationToken.None);
        factory.Last!.AssertDisposed();
        var growth = GC.GetTotalMemory(true) - memoryBefore;
        Assert(time.GetUtcNow() == DateTimeOffset.UnixEpoch.AddMinutes(30), "Synthetic time must cover exactly thirty minutes.");
        Assert(growth < 5 * 1024 * 1024, "Synthetic managed-memory growth exceeded 5 MiB.");
        Assert(controller.Metrics.Stages.All(stage => stage.Count == 1801), "Soak dropped an acknowledged replay item.");
        Console.WriteLine($"Pipeline synthetic soak: 30 virtual minutes, 1,801 frames, wall {realWatch.Elapsed.TotalSeconds:F2}s, managed delta {growth} bytes.");
    }

    private static async Task NextFrameAsync(Adapters adapters, ManualTime time)
    {
        await UntilAsync(() => time.Pending > 0);
        time.Advance(TimeSpan.FromSeconds(1));
        var prior = adapters.Source.Emitted;
        await adapters.Source.Pulses.Writer.WriteAsync(true);
        await UntilAsync(() => adapters.Source.Emitted > prior);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
            { throw new TimeoutException("Pipeline test condition timed out."); }
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private static void Assert(bool value, string message)
    {
        if (!value)
        { throw new InvalidOperationException(message); }
    }

    internal sealed class Factory : ISessionResourcesFactory
    {
        public Adapters? Last { get; private set; }
        public bool BlockOcr;
        public bool BlockTranslation;
        public bool IgnoreTranslationCancellation;
        public bool BlockStartup;
        public bool Started;
        public int Calls;
        public int TransientTranslations;
        public string? Fault;
        public async Task<SessionResources> CreateAsync(SessionStartRequest request, SessionId sessionId, CancellationToken token)
        {
            Started = true;
            Calls++;
            if (BlockStartup)
            { await Task.Delay(Timeout.Infinite, token); }
            Last = new Adapters(sessionId, Fault, TransientTranslations, IgnoreTranslationCancellation);
            if (!BlockOcr)
            { Last.OcrGate.TrySetResult(); }
            if (!BlockTranslation)
            { Last.TranslationGate.TrySetResult(); }
            return new SessionResources(Last.Source, Last, Last, Last, Last, Last, Last.Source);
        }
    }

    internal sealed class Adapters(SessionId id, string? fault, int transientTranslations, bool ignoreTranslationCancellation) :
        IFramePreprocessor, IOcrEngine, ITextStabilizer, ITranslationProvider
    {
        public Source Source { get; } = new(id, fault);
        public TaskCompletionSource OcrGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TranslationGate { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TranslationCancellations;
        public TimeSpan TranslationDuration { get; set; }
        public int OcrCalls;
        public int TranslationCalls;
        public int StableCalls;
        public int Disposals;
        public int Images;
        public int ReleasedImages;
        public bool Disappear;
        public string Id => "fake";
        public OcrEngineCapabilities Capabilities => new(new HashSet<OcrLanguage> { OcrLanguage.English },
            new HashSet<OcrExecutionDevice> { OcrExecutionDevice.Cpu }, true, false);

        public Task<PreprocessedFrame> ProcessAsync(CaptureFrame frame, IReadOnlyList<CaptureRegion> regions,
            PreprocessingOptions options, CancellationToken token)
        {
            if (fault == "preprocess")
            { throw new IOException("SECRET preprocessing"); }
            Interlocked.Increment(ref Images);
            return Task.FromResult(new PreprocessedFrame(frame.Metadata, [new PreprocessedRegionImage(Region,
                new NormalizedRegion(0, 0, 1, 1), 1, 1, OcrPixelFormat.Gray8, new byte[1],
                new Lease(() => Interlocked.Increment(ref ReleasedImages)))]));
        }

        public async Task<OperationResult<OcrResult>> RecognizeAsync(PreprocessedFrame frame, OcrRecognitionOptions options, CancellationToken token)
        {
            Interlocked.Increment(ref OcrCalls);
            await OcrGate.Task.WaitAsync(token);
            if (fault == "ocr")
            { throw new IOException("SECRET OCR"); }
            var metadata = fault == "metadata" ? new PipelineMetadata(SessionId.New(), frame.Metadata.Sequence,
                frame.Metadata.CapturedAtUtc, frame.Metadata.CorrelationId) : frame.Metadata;
            return OperationResult.Success(new OcrResult(metadata, [new OcrTextSpan("Subtitle", .99, new NormalizedRegion(0, 0, 1, 1), Region)]));
        }

        public ValueTask<StableTextEvent?> ObserveAsync(OcrResult result, StabilizationContext context, CancellationToken token)
        {
            if (fault == "stabilize")
            { throw new IOException("SECRET stabilization"); }
            var stable = new StableTextEvent(result.Metadata, Disappear ? StableTextEventKind.Disappeared : StableTextEventKind.Changed,
                Disappear ? null : $"Subtitle {result.Sequence}", Disappear ? [] : result.Spans, 1);
            Interlocked.Increment(ref StableCalls);
            return ValueTask.FromResult<StableTextEvent?>(stable);
        }

        public ValueTask ResetAsync(CancellationToken token) => ValueTask.CompletedTask;

        public async Task<OperationResult<TranslationResult>> TranslateAsync(TranslationRequest request, CancellationToken token)
        {
            using var cancellation = token.Register(() => Interlocked.Increment(ref TranslationCancellations));
            var call = Interlocked.Increment(ref TranslationCalls);
            if (ignoreTranslationCancellation)
            { await TranslationGate.Task; }
            else
            { await TranslationGate.Task.WaitAsync(token); }
            if (fault == "translate")
            { throw new IOException("SECRET translation"); }
            if (call <= transientTranslations)
            {
                return OperationResult.Failed<TranslationResult>(new PipelineFailure(FailureCode.ProviderUnavailable,
                    "Temporary provider failure.", request.Metadata.CorrelationId, true));
            }
            return OperationResult.Success(new TranslationResult(request.Metadata, request.Text, "Translation", Id, "model",
                TranslationDuration, true, true));
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            if (fault == "dispose")
            { throw new IOException("SECRET disposal"); }
            return ValueTask.CompletedTask;
        }

        public void AssertDisposed()
        {
            Assert(Disposals == 1 && Source.Disposals == 1, "Owned adapters must be disposed once each.");
            Assert(Source.Active == 0 && Source.Leases == Source.Released, "Capture enumeration or dropped frame leaked.");
            Assert(Images == ReleasedImages, "Preprocessed images leaked.");
        }
    }

    internal sealed class Source(SessionId id, string? fault) : ICaptureSource
    {
        public Channel<bool> Pulses { get; } = Channel.CreateBounded<bool>(1);
        public int Emitted;
        public int Active;
        public int Disposals;
        public int Leases;
        public int Released;
        public Task BeforeEnumeration { get; set; } = Task.CompletedTask;
        private long _sequence;
        public async IAsyncEnumerable<CaptureFrame> CaptureAsync([EnumeratorCancellation] CancellationToken token)
        {
            Interlocked.Increment(ref Active);
            try
            {
                await BeforeEnumeration.WaitAsync(token);
                if (fault == "capture")
                { throw new IOException("SECRET capture"); }
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref Leases);
                    var metadata = new PipelineMetadata(id, new SequenceNumber(_sequence++), DateTimeOffset.UnixEpoch, CorrelationId.New());
                    Interlocked.Increment(ref Emitted);
                    yield return new CaptureFrame(metadata, 1, 1, new byte[4], new Lease(() => Interlocked.Increment(ref Released)));
                    await Pulses.Reader.ReadAsync(token);
                }
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
    }

    private sealed class Lease(Action release) : IFrameLease
    {
        public void Dispose() => release();
    }

    private sealed class Display : IOverlayPresenter
    {
        public TranslationResult? Current;
        public bool Fail;
        public bool TrackHistory = true;
        public HashSet<(SessionId, long)> Seen { get; } = [];
        public Task ShowAsync(TranslationResult result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Fail)
            { throw new IOException("SECRET presentation"); }
            if (TrackHistory)
            { Seen.Add((result.Metadata.SessionId, result.Sequence)); }
            Volatile.Write(ref Current, result);
            return Task.CompletedTask;
        }
        public Task HideAsync(CancellationToken token) { Volatile.Write(ref Current, null); return Task.CompletedTask; }
    }

    private sealed class ManualTime : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        public int Pending { get { lock (_sync) { return _timers.Count(timer => timer.Due != long.MaxValue); } } }
        public bool HasDelay(TimeSpan delay) { lock (_sync) { return _timers.Any(timer => timer.Due - _ticks == delay.Ticks); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan elapsed)
        {
            ManualTimer[] ready;
            lock (_sync)
            {
                _ticks += elapsed.Ticks;
                ready = _timers.Where(timer => timer.Due <= _ticks).ToArray();
                foreach (var timer in ready)
                { timer.Due = long.MaxValue; }
            }
            foreach (var timer in ready)
            { timer.Invoke(); }
        }
        private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
        {
            public long Due = long.MaxValue;
            public void Invoke() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert(period == Timeout.InfiniteTimeSpan, "Tests support one-shot timers only.");
                lock (owner._sync)
                {
                    if (!owner._timers.Contains(this))
                    { owner._timers.Add(this); }
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                }
                return true;
            }
            public void Dispose() { lock (owner._sync) { owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}

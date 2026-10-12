using System.Threading.Channels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

/// <summary>One owned session, four supervised workers, and fixed 2/1/1 queue capacities.</summary>
public sealed class SessionController(ISessionResourcesFactory factory, IOverlayPresenter presenter,
    TimeProvider? timeProvider = null, IDiagnosticEvents? diagnostics = null) : ISessionController, IAsyncDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _sync = new();
    private SessionState _state;
    private SessionStageException? _failure;
    private SessionResources? _resources;
    private SessionStartRequest? _request;
    private Cycle? _cycle;
    private PipelineMetrics? _metrics;
    private MonotonicSequenceGuard _sequences = new();
    private CancellationTokenSource? _starting;
    private bool _disposed;

    public SessionState State { get { lock (_sync) { return _state; } } }
    public PipelineFailure? Failure { get { lock (_sync) { return _failure?.Failure; } } }
    public SessionMetrics Metrics => _metrics?.Snapshot() ?? new(null, null, 0, 0, 0, 0, 0, 0, []);

    public async Task StartAsync(SessionStartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (State is not (SessionState.Stopped or SessionState.Faulted))
            {
                throw new InvalidOperationException("Stop the current session before starting another.");
            }

            await ReleaseAsync().ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15), _time);
            using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            lock (_sync)
            { _starting = starting; _failure = null; ChangeState(SessionState.Starting); }
            _request = request;
            var id = SessionId.New();
            _metrics = new PipelineMetrics(id);
            Emit(new(DiagnosticEventKind.SessionStarting, id));
            _sequences = new MonotonicSequenceGuard();
            try
            {
                await presenter.HideAsync(starting.Token).ConfigureAwait(false);
                _resources = await factory.CreateAsync(request, id, starting.Token).ConfigureAwait(false);
                starting.Token.ThrowIfCancellationRequested();
                await BeginCycleAsync(id, starting.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var released = await ReleaseAsync().ConfigureAwait(false);
                if (error is OperationCanceledException && !timeout.IsCancellationRequested)
                {
                    if (released)
                    {
                        if (State == SessionState.Starting)
                        { ChangeState(SessionState.Stopping); }
                        ChangeState(SessionState.Stopped);
                    }
                    throw;
                }

                Fault(error, timeout.IsCancellationRequested ? FailureCode.Timeout : FailureCode.Unexpected);
            }
            finally { lock (_sync) { _starting = null; } }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != SessionState.Running)
            { return; }
            await EndCycleAsync().ConfigureAwait(false);
            if (State != SessionState.Faulted)
            {
                await _resources!.Stabilizer.ResetAsync(CancellationToken.None).ConfigureAwait(false);
                ChangeState(SessionState.Paused);
            }
        }
        catch (Exception error) { Fault(error); await ReleaseAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != SessionState.Paused)
            { return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15), _time);
            using var starting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            lock (_sync)
            { _starting = starting; }
            try
            { await BeginCycleAsync(_metrics!.Snapshot().SessionId!.Value, starting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
            {
                await EndCycleAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception error) { Fault(error); await ReleaseAsync().ConfigureAwait(false); }
            finally { lock (_sync) { _starting = null; } }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Interrupt a pending startup before waiting for its lifecycle ownership.
        lock (_sync)
        { _starting?.Cancel(); }
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == SessionState.Stopped && _resources is null)
            { return; }
            if (State != SessionState.Faulted)
            { ChangeState(SessionState.Stopping); }
            if (await ReleaseAsync().ConfigureAwait(false))
            { ChangeState(SessionState.Stopped); }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        { _starting?.Cancel(); }
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            if (State is not (SessionState.Stopped or SessionState.Faulted))
            { ChangeState(SessionState.Stopping); }
            if (await ReleaseAsync().ConfigureAwait(false))
            { ChangeState(SessionState.Stopped); }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task BeginCycleAsync(SessionId id, CancellationToken cancellationToken)
    {
        var cycle = new Cycle(id, _metrics!);
        _cycle = cycle;
        var workers = new[]
        {
            RunWorkerAsync(cycle, () => CaptureAsync(cycle)),
            RunWorkerAsync(cycle, () => RecognizeAsync(cycle)),
            RunWorkerAsync(cycle, () => TranslateAsync(cycle)),
            RunWorkerAsync(cycle, () => PresentAsync(cycle))
        };
        cycle.Completion = SuperviseAsync(cycle, workers);
        try
        {
            await cycle.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (cycle.Error is not null)
                { throw cycle.Error; }
                ChangeState(SessionState.Running);
                cycle.Active.TrySetResult();
            }
        }
        catch { cycle.Cancellation.Cancel(); throw; }
    }

    private static Task RunWorkerAsync(Cycle cycle, Func<Task> worker) => Task.Run(async () =>
    {
        try
        { await worker().ConfigureAwait(false); }
        catch (OperationCanceledException) when (cycle.Token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Interlocked.CompareExchange(ref cycle.Error, error, null);
            cycle.Ready.TrySetException(error);
            cycle.Cancellation.Cancel();
        }
    });

    private async Task SuperviseAsync(Cycle cycle, Task[] workers)
    {
        await Task.WhenAll(workers).ConfigureAwait(false);
        cycle.Ready.TrySetCanceled();
        while (cycle.Frames.Reader.TryRead(out var frame))
        { frame.Dispose(); }
        while (cycle.Text.Reader.TryRead(out _))
        { }
        while (cycle.Results.Reader.TryRead(out _))
        { }
        try
        { await presenter.HideAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { cycle.Error ??= error; }
        if (cycle.Error is not null)
        {
            Fault(cycle.Error);
            try
            { await _resources!.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Fault(error); }
        }
    }

    private async Task EndCycleAsync()
    {
        if (_cycle is not { } cycle)
        { return; }
        cycle.Cancellation.Cancel();
        await cycle.Completion.ConfigureAwait(false);
        // A cancelled startup may stop awaiting Ready before capture reports its final error.
        if (cycle.Ready.Task.IsFaulted)
        { _ = cycle.Ready.Task.Exception; }
        cycle.Cancellation.Dispose();
        cycle.PresentationGate.Dispose();
        _cycle = null;
    }

    private async Task<bool> ReleaseAsync()
    {
        var released = true;
        await EndCycleAsync().ConfigureAwait(false);
        if (_resources is { } resources)
        {
            try
            { await resources.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { Fault(error); released = false; }
            _resources = null;
        }
        return released;
    }

    private async Task CaptureAsync(Cycle cycle)
    {
        await using var frames = _resources!.Capture.CaptureAsync(cycle.Token).GetAsyncEnumerator(cycle.Token);
        while (true)
        {
            var started = _time.GetTimestamp();
            if (!await frames.MoveNextAsync().ConfigureAwait(false))
            {
                throw new SessionStageException(new PipelineFailure(FailureCode.TargetUnavailable,
                    "The selected window closed or capture ended. Select the window again and restart.", CorrelationId.New(), false));
            }

            var frame = frames.Current;
            if (frame.Metadata.SessionId != cycle.Id || !_sequences.TryAccept(frame.Metadata.Sequence))
            {
                frame.Dispose();
                Interlocked.Increment(ref cycle.Metrics.DroppedFrames);
                continue;
            }

            cycle.Metrics.Record(PipelineStage.Capture, _time.GetElapsedTime(started), frame.Metadata);
            Emit(new(DiagnosticEventKind.StageCompleted, cycle.Id, frame.Metadata.CorrelationId, PipelineStage.Capture));
            if (!cycle.Frames.Writer.TryWrite(frame))
            { frame.Dispose(); }
            cycle.Ready.TrySetResult();
            await cycle.Active.Task.WaitAsync(cycle.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1d / _request!.Profile.CaptureFramesPerSecond), _time, cycle.Token).ConfigureAwait(false);
        }
    }

    private async Task RecognizeAsync(Cycle cycle)
    {
        await cycle.Active.Task.WaitAsync(cycle.Token).ConfigureAwait(false);
        await foreach (var frame in cycle.Frames.Reader.ReadAllAsync(cycle.Token).ConfigureAwait(false))
        {
            using (frame)
            {
                var started = _time.GetTimestamp();
                using var processed = await _resources!.Preprocessor.ProcessAsync(frame, _request!.Profile.Regions,
                    _request.Profile.Preprocessing, cycle.Token).ConfigureAwait(false);
                EnsureMetadata(frame.Metadata, processed.Metadata);
                cycle.Metrics.Record(PipelineStage.Preprocessing, _time.GetElapsedTime(started), frame.Metadata);
                started = _time.GetTimestamp();
                var recognized = await _resources.Ocr.RecognizeAsync(processed, _request.OcrOptions, cycle.Token).ConfigureAwait(false);
                cycle.Metrics.Record(PipelineStage.Ocr, _time.GetElapsedTime(started), frame.Metadata);
                if (!recognized.IsSuccess)
                {
                    Interlocked.Increment(ref cycle.Metrics.Errors);
                    Emit(new(DiagnosticEventKind.StageFailed, cycle.Id, frame.Metadata.CorrelationId,
                        PipelineStage.Ocr, recognized.Failure!.Code));
                    if (recognized.Failure!.IsTransient)
                    { continue; }
                    throw new SessionStageException(recognized.Failure);
                }

                EnsureMetadata(frame.Metadata, recognized.Value!.Metadata);
                started = _time.GetTimestamp();
                var stable = await _resources.Stabilizer.ObserveAsync(recognized.Value, _request.Context, cycle.Token).ConfigureAwait(false);
                cycle.Metrics.Record(PipelineStage.Stabilization, _time.GetElapsedTime(started), frame.Metadata);
                if (stable is null)
                { continue; }
                EnsureMetadata(frame.Metadata, stable.Metadata);
                await cycle.PresentationGate.WaitAsync(cycle.Token).ConfigureAwait(false);
                try
                {
                    Interlocked.Exchange(ref cycle.LatestSequence, stable.Metadata.Sequence.Value);
                    lock (cycle.TranslationSync)
                    { cycle.TranslationCancellation?.Cancel(); }
                    if (stable.Kind == StableTextEventKind.Disappeared)
                    {
                        cycle.Results.Writer.TryWrite(new DisplayUpdate(stable.Metadata, null));
                    }
                    else
                    { cycle.Text.Writer.TryWrite(stable); }
                }
                finally { cycle.PresentationGate.Release(); }
            }
        }
    }

    private async Task TranslateAsync(Cycle cycle)
    {
        await cycle.Active.Task.WaitAsync(cycle.Token).ConfigureAwait(false);
        var profile = _request!.Profile;
        var revision = _request.Glossary.Revision(profile.Id, profile.SourceLanguage, profile.TargetLanguage);
        await foreach (var stable in cycle.Text.Reader.ReadAllAsync(cycle.Token).ConfigureAwait(false))
        {
            if (!IsCurrent(cycle, stable.Metadata))
            { continue; }
            var request = new TranslationRequest(stable.Metadata, profile.SourceLanguage, profile.TargetLanguage,
                stable.Text!, [], _request.Glossary.Select(profile.Id, profile.SourceLanguage, profile.TargetLanguage, stable.Text!),
                _request.Provider.Model, _request.PromptOptions, revision);
            using var translating = CancellationTokenSource.CreateLinkedTokenSource(cycle.Token);
            lock (cycle.TranslationSync)
            {
                if (!IsCurrent(cycle, stable.Metadata))
                { continue; }
                cycle.TranslationCancellation = translating;
            }
            try
            {
                // Adapter retries are already bounded. This small recovery loop also covers temporary local/cache gateway failures.
                for (var attempt = 0; attempt < 3 && IsCurrent(cycle, stable.Metadata); attempt++)
                {
                    var started = _time.GetTimestamp();
                    var translated = await _resources!.Translation.TranslateAsync(request, translating.Token).ConfigureAwait(false);
                    cycle.Metrics.Record(PipelineStage.Translation, _time.GetElapsedTime(started), stable.Metadata);
                    if (translated.IsSuccess)
                    {
                        EnsureMetadata(stable.Metadata, translated.Value!.Metadata);
                        Emit(new(DiagnosticEventKind.StageCompleted, cycle.Id, stable.Metadata.CorrelationId, PipelineStage.Translation));
                        if (translated.Value.FromCache)
                        { Interlocked.Increment(ref cycle.Metrics.CacheHits); }
                        if (IsCurrent(cycle, stable.Metadata))
                        { cycle.Results.Writer.TryWrite(new(stable.Metadata, translated.Value)); }
                        break;
                    }

                    Interlocked.Increment(ref cycle.Metrics.Errors);
                    Emit(new(DiagnosticEventKind.StageFailed, cycle.Id, stable.Metadata.CorrelationId,
                        PipelineStage.Translation, translated.Failure!.Code));
                    if (!translated.Failure!.IsTransient)
                    { throw new SessionStageException(translated.Failure); }
                    if (attempt < 2)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), _time, translating.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        // Allow unchanged subtitles to be observed again after bounded retry exhaustion.
                        await _resources.Stabilizer.ResetAsync(cycle.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (translating.IsCancellationRequested)
            {
                Interlocked.Increment(ref cycle.Metrics.StaleResults);
                cycle.Token.ThrowIfCancellationRequested();
            }
            finally
            {
                lock (cycle.TranslationSync)
                { cycle.TranslationCancellation = null; }
            }
        }
    }

    private async Task PresentAsync(Cycle cycle)
    {
        await cycle.Active.Task.WaitAsync(cycle.Token).ConfigureAwait(false);
        await foreach (var update in cycle.Results.Reader.ReadAllAsync(cycle.Token).ConfigureAwait(false))
        {
            await cycle.PresentationGate.WaitAsync(cycle.Token).ConfigureAwait(false);
            try
            {
                if (!IsCurrent(cycle, update.Metadata))
                { continue; }
                var started = _time.GetTimestamp();
                if (update.Result is null)
                { await presenter.HideAsync(cycle.Token).ConfigureAwait(false); }
                else
                { await presenter.ShowAsync(update.Result, cycle.Token).ConfigureAwait(false); }
                cycle.Metrics.Record(PipelineStage.Presentation, _time.GetElapsedTime(started), update.Metadata);
                Emit(new(DiagnosticEventKind.StageCompleted, cycle.Id, update.Metadata.CorrelationId, PipelineStage.Presentation));
            }
            finally { cycle.PresentationGate.Release(); }
        }
    }

    private static void EnsureMetadata(PipelineMetadata expected, PipelineMetadata actual)
    {
        if (expected != actual)
        { throw new InvalidOperationException("Stage metadata was not preserved."); }
    }

    private static bool IsCurrent(Cycle cycle, PipelineMetadata metadata)
    {
        if (!cycle.Token.IsCancellationRequested && metadata.SessionId == cycle.Id &&
            metadata.Sequence.Value == Interlocked.Read(ref cycle.LatestSequence))
        { return true; }
        Interlocked.Increment(ref cycle.Metrics.StaleResults);
        return false;
    }

    private void Fault(Exception error, FailureCode code = FailureCode.Unexpected)
    {
        lock (_sync)
        {
            _failure = error as SessionStageException ?? new SessionStageException(new PipelineFailure(code,
                "The session stopped safely. Check the selected window, OCR model and provider settings, then restart.",
                _metrics?.Snapshot().LastCorrelationId ?? CorrelationId.New(), false));
            if (_metrics is not null && _state != SessionState.Faulted)
            { Interlocked.Increment(ref _metrics.Errors); }
            ChangeState(SessionState.Faulted);
        }
    }

    private void ChangeState(SessionState next)
    {
        lock (_sync)
        {
            if (_state != next)
            {
                _state = SessionTransitions.Transition(_state, next);
                var kind = next switch
                {
                    SessionState.Running => DiagnosticEventKind.SessionRunning,
                    SessionState.Paused => DiagnosticEventKind.SessionPaused,
                    SessionState.Stopped => DiagnosticEventKind.SessionStopped,
                    SessionState.Faulted => DiagnosticEventKind.StageFailed,
                    _ => (DiagnosticEventKind?)null
                };
                if (kind is { } eventKind)
                {
                    var snapshot = Metrics;
                    Emit(new(eventKind, snapshot.SessionId, _failure?.Failure.CorrelationId ?? snapshot.LastCorrelationId,
                        ErrorCategory: next == SessionState.Faulted ? _failure?.Failure.Code : null));
                }
            }
        }
    }

    private void Emit(DiagnosticEvent entry)
    {
        // A faulty diagnostics implementation must not fault the session or recurse into logging.
        try
        { diagnostics?.TryWrite(entry); }
        catch (Exception) { }
    }

    private sealed record DisplayUpdate(PipelineMetadata Metadata, TranslationResult? Result);

    private sealed class Cycle
    {
        public Cycle(SessionId id, PipelineMetrics metrics)
        {
            Id = id;
            Metrics = metrics;
            Frames = Channel.CreateBounded<CaptureFrame>(Options(2), frame =>
            {
                frame.Dispose();
                Interlocked.Increment(ref metrics.DroppedFrames);
            });
            Text = Channel.CreateBounded<StableTextEvent>(Options(1), _ => Interlocked.Increment(ref metrics.DroppedText));
            Results = Channel.CreateBounded<DisplayUpdate>(Options(1), _ => Interlocked.Increment(ref metrics.DroppedResults));
        }

        private static BoundedChannelOptions Options(int capacity) => new(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false
        };

        public SessionId Id { get; }
        public PipelineMetrics Metrics { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken Token => Cancellation.Token;
        public Channel<CaptureFrame> Frames { get; }
        public Channel<StableTextEvent> Text { get; }
        public Channel<DisplayUpdate> Results { get; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Active { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim PresentationGate { get; } = new(1, 1);
        public object TranslationSync { get; } = new();
        public CancellationTokenSource? TranslationCancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
        public Exception? Error;
        public long LatestSequence = -1;
    }
}

using System.Threading.Channels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Services;

/// <summary>Best-effort, bounded local history after presentation; storage never blocks a subtitle.</summary>
public sealed class HistoryRecordingPresenter(IOverlayPresenter display, ITranslationHistoryRepository repository) : IOverlayPresenter, IAsyncDisposable
{
    private readonly object _sync = new();
    private Channel<TranslationHistoryEntry>? _queue;
    private CancellationTokenSource? _cancellation;
    private Task _worker = Task.CompletedTask;
    private SessionId? _session;
    private ProfileId? _profile;
    private long _sequence = -1, _dropped, _errors;
    private bool _disposed;

    public long Dropped => Interlocked.Read(ref _dropped);
    public long Errors => Interlocked.Read(ref _errors);

    /// <summary>Lifecycle calls are serialized by the owning session command boundary.</summary>
    public async Task BeginAsync(bool enabled, ProfileId? persistedProfile, CancellationToken cancellationToken)
    {
        await StopAsync().ConfigureAwait(false);
        if (!enabled)
        { return; }
        try
        { await repository.ConfigureAsync(new HistoryPolicy(true), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { Interlocked.Increment(ref _errors); return; }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _profile = persistedProfile;
            _session = null;
            _sequence = -1;
            _queue = Channel.CreateBounded<TranslationHistoryEntry>(new BoundedChannelOptions(32)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
            _cancellation = new CancellationTokenSource();
            _worker = RunAsync(_queue, _cancellation.Token);
        }
    }

    public async Task ShowAsync(TranslationResult result, CancellationToken cancellationToken)
    {
        await display.ShowAsync(result, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (_queue is null)
            { return; }
            _session ??= result.Metadata.SessionId;
            if (_session != result.Metadata.SessionId || result.Sequence <= _sequence)
            { return; }
            _sequence = result.Sequence;
            try
            {
                var entry = new TranslationHistoryEntry(result.Metadata.SessionId, _profile, result.SourceText,
                    result.TranslatedText, result.Metadata.CapturedAtUtc, (long)result.Duration.TotalMilliseconds);
                if (!_queue.Writer.TryWrite(entry))
                { Interlocked.Increment(ref _dropped); }
            }
            catch (ArgumentException) { Interlocked.Increment(ref _dropped); }
        }
    }

    public Task HideAsync(CancellationToken cancellationToken) => display.HideAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        { _disposed = true; }
        return new ValueTask(StopAsync());
    }

    public async Task StopAsync()
    {
        Task worker;
        CancellationTokenSource? cancellation;
        lock (_sync)
        {
            _queue?.Writer.TryComplete();
            _queue = null;
            cancellation = _cancellation;
            _cancellation = null;
            worker = _worker;
        }
        if (cancellation is null)
        { await worker.ConfigureAwait(false); return; }
        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        { await worker.ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }

    private async Task RunAsync(Channel<TranslationHistoryEntry> queue, CancellationToken token)
    {
        var reader = queue.Reader;
        try
        {
            await foreach (var entry in reader.ReadAllAsync(token).ConfigureAwait(false))
            { await repository.AppendAsync(entry, token).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { queue.Writer.TryComplete(); Interlocked.Increment(ref _errors); }
        finally { while (reader.TryRead(out _)) { Interlocked.Increment(ref _dropped); } }
    }
}

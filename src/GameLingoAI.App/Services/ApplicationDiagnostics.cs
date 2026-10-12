using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;

namespace GameLingoAI.App.Services;

/// <summary>App-owned lifetime: no logger starts during DI validation or window construction.</summary>
public sealed class ApplicationDiagnostics : IDiagnosticEvents
{
    private readonly object _sync = new();
    private BufferedDiagnosticWriter? _writer;
    private Task? _shutdown;
    private bool _started;
    private long _failures;

    public async Task<DiagnosticMetrics> SnapshotMetricsAsync(SessionMetrics session, CancellationToken cancellationToken)
    {
        var resources = await CurrentProcessMetrics.SampleAsync(cancellationToken).ConfigureAwait(false);
        return new DiagnosticMetrics(DateTimeOffset.UtcNow, session, resources, Dropped, Failures);
    }

    /// <summary>Reads only the retention number; never forwards configuration or text opt-ins to a logger.</summary>
    public async Task InitializeAsync(IConfigurationStore configuration, Func<int, BufferedDiagnosticWriter> factory,
        CancellationToken cancellationToken)
    {
        var retained = 7;
        try
        {
            var loaded = await configuration.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.IsSuccess && loaded.Value!.Logging.RetainedFileCount is >= 1 and <= 30)
            { retained = loaded.Value.Logging.RetainedFileCount; }
            else
            { Interlocked.Increment(ref _failures); }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { Interlocked.Increment(ref _failures); }
        cancellationToken.ThrowIfCancellationRequested();
        Start(() => factory(retained));
    }

    public void Start(Func<BufferedDiagnosticWriter> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_sync)
        {
            if (_started || _shutdown is not null)
            { return; }
            _started = true;
            try
            {
                _writer = factory();
                _writer.TryWrite(new(DiagnosticEventKind.ApplicationStarted));
            }
            catch (Exception) { Interlocked.Increment(ref _failures); }
        }
    }

    public bool TryWrite(DiagnosticEvent entry)
    {
        lock (_sync)
        { return _shutdown is null && _writer?.TryWrite(entry) == true; }
    }

    public long Failures { get { lock (_sync) { return Interlocked.Read(ref _failures) + (_writer?.Failures ?? 0); } } }
    public long Dropped { get { lock (_sync) { return _writer?.Dropped ?? 0; } } }
    public IReadOnlyList<string> SnapshotRecent()
    {
        lock (_sync)
        { return _writer?.SnapshotRecent() ?? []; }
    }

    public Task ShutdownAsync()
    {
        lock (_sync)
        { return _shutdown ??= DrainAsync(); }
    }

    /// <summary>Best-effort fallback for abnormal exit. Normal window close awaits ShutdownAsync.</summary>
    public void RequestShutdown() => _ = ShutdownAsync();

    private async Task DrainAsync()
    {
        try
        {
            if (_writer is not null)
            { await _writer.DisposeAsync().ConfigureAwait(false); }
        }
        catch (Exception) { Interlocked.Increment(ref _failures); }
    }
}

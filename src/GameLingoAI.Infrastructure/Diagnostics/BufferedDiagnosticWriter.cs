using System.Globalization;
using System.Threading.Channels;
using GameLingoAI.Core.Models;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Parsing;

namespace GameLingoAI.Infrastructure.Diagnostics;

/// <summary>Nonblocking admission, bounded memory, and one owned background sink lifetime.</summary>
public sealed class BufferedDiagnosticWriter : GameLingoAI.Core.Abstractions.IDiagnosticEvents, IAsyncDisposable
{
    private static readonly MessageTemplate Template = new MessageTemplateParser().Parse("Diagnostic {Kind}");
    private readonly Channel<LogEvent> _queue;
    private readonly Func<ILogEventSink> _destinationFactory;
    private readonly Queue<string> _recent = new();
    private readonly int _recentCapacity;
    private readonly Task _worker;
    private readonly DiagnosticFailureCounter _sinkFailures;
    private long _dropped;
    private long _failures;

    public BufferedDiagnosticWriter(Func<ILogEventSink> destinationFactory, int capacity = 256, int recentCapacity = 100)
        : this(destinationFactory, new DiagnosticFailureCounter(), capacity, recentCapacity)
    { }

    internal BufferedDiagnosticWriter(Func<ILogEventSink> destinationFactory, DiagnosticFailureCounter sinkFailures,
        int capacity = 256, int recentCapacity = 100)
    {
        ArgumentNullException.ThrowIfNull(destinationFactory);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThan(recentCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recentCapacity, 1000);
        _destinationFactory = destinationFactory;
        _sinkFailures = sinkFailures;
        _recentCapacity = recentCapacity;
        _queue = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
        _worker = Task.Run(ConsumeAsync);
    }

    public long Dropped => Interlocked.Read(ref _dropped);
    public long Failures => Interlocked.Read(ref _failures) + _sinkFailures.Count;

    /// <summary>False means full or shutting down. Never waits for disk or a sink.</summary>
    public bool TryWrite(DiagnosticEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var properties = new List<LogEventProperty> { new("Kind", new ScalarValue(entry.Kind)) };
        if (entry.SessionId is { } session)
        { properties.Add(new("SessionId", new ScalarValue(session.Value))); }
        if (entry.CorrelationId is { } correlation)
        { properties.Add(new("CorrelationId", new ScalarValue(correlation.Value))); }
        if (entry.Stage is { } stage)
        { properties.Add(new("Stage", new ScalarValue(stage))); }
        if (entry.ErrorCategory is { } category)
        { properties.Add(new("ErrorCategory", new ScalarValue(category))); }
        var safe = RedactingDiagnosticSink.Sanitize(new LogEvent(DateTimeOffset.UtcNow,
            entry.ErrorCategory is null ? LogEventLevel.Information : LogEventLevel.Warning, null, Template, properties));
        if (_queue.Writer.TryWrite(safe))
        { return true; }
        Interlocked.Increment(ref _dropped);
        return false;
    }

    /// <summary>Independent immutable JSON strings; consumers cannot mutate queued events.</summary>
    public IReadOnlyList<string> SnapshotRecent()
    {
        lock (_recent)
        { return _recent.ToArray(); }
    }

    public ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        return new ValueTask(_worker);
    }

    private async Task ConsumeAsync()
    {
        ILogEventSink? destination = null;
        try
        {
            try
            { destination = _destinationFactory(); }
            catch (Exception) { Interlocked.Increment(ref _failures); }
            await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                using var output = new StringWriter(CultureInfo.InvariantCulture);
                new JsonFormatter().Format(entry, output);
                lock (_recent)
                {
                    if (_recent.Count == _recentCapacity)
                    { _recent.Dequeue(); }
                    _recent.Enqueue(output.ToString());
                }
                // Already projected once before admission. Do not re-sanitize enum strings.
                try
                { destination?.Emit(entry); }
                catch (Exception) { Interlocked.Increment(ref _failures); }
            }
        }
        finally
        {
            try
            {
                if (destination is IAsyncDisposable asyncDisposable)
                { await asyncDisposable.DisposeAsync().ConfigureAwait(false); }
                else if (destination is IDisposable disposable)
                { disposable.Dispose(); }
            }
            catch (Exception) { Interlocked.Increment(ref _failures); }
        }
    }
}

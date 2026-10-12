using System.Threading.Channels;

namespace GameLingoAI.Infrastructure.Capture;

/// <summary>A single-consumer, capacity-two queue that disposes the oldest stale frame.</summary>
public sealed class LatestFrameBuffer<T> : IDisposable
    where T : IDisposable
{
    private readonly Channel<T> _channel;
    private int _disposed;

    public LatestFrameBuffer()
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(2)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public bool TryWrite(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_channel.Writer.TryWrite(item))
        {
            return true;
        }

        if (_channel.Reader.TryRead(out var stale))
        {
            stale.Dispose();
        }

        return _channel.Writer.TryWrite(item);
    }

    public IAsyncEnumerable<T> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public void Complete(Exception? exception = null) => _channel.Writer.TryComplete(exception);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var frame))
        {
            frame.Dispose();
        }
    }
}

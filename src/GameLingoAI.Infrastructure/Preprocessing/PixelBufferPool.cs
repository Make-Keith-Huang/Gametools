using System.Buffers;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Preprocessing;

public interface IPixelBufferPool
{
    byte[] Rent(int minimumLength);

    void ReturnBuffer(byte[] buffer, bool clearBuffer);
}

public sealed class SharedPixelBufferPool : IPixelBufferPool
{
    public byte[] Rent(int minimumLength) => ArrayPool<byte>.Shared.Rent(minimumLength);

    public void ReturnBuffer(byte[] buffer, bool clearBuffer) => ArrayPool<byte>.Shared.Return(buffer, clearBuffer);
}

internal sealed class PooledPixelLease : IFrameLease
{
    private readonly IPixelBufferPool _pool;
    private byte[]? _buffer;

    public PooledPixelLease(IPixelBufferPool pool, int length)
    {
        _pool = pool;
        Length = length;
        _buffer = pool.Rent(length);
    }

    public int Length { get; }

    public Memory<byte> Memory => (_buffer ?? throw new ObjectDisposedException(nameof(PooledPixelLease))).AsMemory(0, Length);

    public void Dispose()
    {
        var buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer is not null)
        {
            _pool.ReturnBuffer(buffer, clearBuffer: true);
        }
    }
}

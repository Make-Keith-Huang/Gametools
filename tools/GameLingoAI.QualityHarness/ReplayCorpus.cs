using System.Buffers.Binary;
using GameLingoAI.Core.Models;

namespace GameLingoAI.QualityHarness;

/// <summary>Versioned synthetic input, generated lazily. No game assets, machine clock, randomness or retained frames.</summary>
public sealed class ReplayCorpus
{
    public const string Version = "synthetic-bgra-v1";
    public const int Width = 640;
    public const int Height = 360;
    private static readonly SessionId Session = new(Guid.Parse("23000000-0000-0000-0000-000000000001"));

    public ReplayCorpus(int count = 600, TimeSpan? interval = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 100_000);
        Count = count;
        Interval = interval ?? TimeSpan.FromMilliseconds(100);
        ArgumentOutOfRangeException.ThrowIfLessThan(Interval, TimeSpan.FromMilliseconds(1));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Interval, TimeSpan.FromMinutes(1));
    }

    public int Count { get; }
    public TimeSpan Interval { get; }

    public PipelineMetadata Metadata(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        Span<byte> identifier = stackalloc byte[16];
        identifier.Clear();
        BinaryPrimitives.WriteInt64LittleEndian(identifier, index + 1L);
        identifier[15] = 0x23;
        return new PipelineMetadata(Session, new SequenceNumber(index),
            DateTimeOffset.UnixEpoch.AddTicks(checked(Interval.Ticks * index)), new CorrelationId(new Guid(identifier)));
    }

    /// <summary>Each group repeats eight frames; every fourth group is blank. Caller owns and disposes the pixels.</summary>
    public CaptureFrame CreateFrame(int index, CancellationToken cancellationToken)
    {
        var metadata = Metadata(index);
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = new byte[Width * Height * 4];
        try
        {
            var scene = index / 8;
            for (var y = 0; y < Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < Width; x++)
                {
                    var offset = (y * Width + x) * 4;
                    // Hand-authored geometric glyph-like boxes; deliberately not an OCR accuracy fixture.
                    var glyph = scene % 4 != 3 && y is >= 280 and < 312 && x is >= 80 and < 560 &&
                        ((x + scene * 3) % 24 < 4 || (y - 280) % 15 < 3);
                    var luminance = glyph ? (byte)240 : (byte)16;
                    pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = luminance;
                    pixels[offset + 3] = 255;
                }
            }
            return new CaptureFrame(metadata, Width, Height, pixels, new PixelLease(pixels));
        }
        catch
        {
            pixels.AsSpan().Clear();
            throw;
        }
    }

    private sealed class PixelLease(byte[] pixels) : IFrameLease
    {
        private byte[]? _pixels = pixels;
        public void Dispose() => Interlocked.Exchange(ref _pixels, null)?.AsSpan().Clear();
    }
}

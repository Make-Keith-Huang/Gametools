using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Preprocessing;

public sealed class FramePreprocessor : IFramePreprocessor
{
    private readonly IPixelBufferPool _bufferPool;
    private readonly IPreprocessingDiagnosticSink _diagnosticSink;

    public FramePreprocessor(
        IPixelBufferPool bufferPool,
        IPreprocessingDiagnosticSink diagnosticSink)
    {
        _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        _diagnosticSink = diagnosticSink ?? throw new ArgumentNullException(nameof(diagnosticSink));
    }

    public async Task<PreprocessedFrame> ProcessAsync(
        CaptureFrame frame,
        IReadOnlyList<CaptureRegion> regions,
        PreprocessingOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(options);
        Validate(frame, regions, options);
        cancellationToken.ThrowIfCancellationRequested();

        var result = await Task.Run(
            () => ProcessCore(frame, regions, options, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (options.EnableDiagnosticImages)
            {
                foreach (var image in result.Regions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _diagnosticSink.WriteAsync(result.Metadata, image, cancellationToken).ConfigureAwait(false);
                }
            }

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private PreprocessedFrame ProcessCore(
        CaptureFrame frame,
        IReadOnlyList<CaptureRegion> regions,
        PreprocessingOptions options,
        CancellationToken cancellationToken)
    {
        var outputs = new List<PreprocessedRegionImage>(regions.Count);
        try
        {
            foreach (var region in regions.OrderBy(region => region.SortOrder))
            {
                cancellationToken.ThrowIfCancellationRequested();
                outputs.Add(ProcessRegion(frame, region, options, cancellationToken));
            }

            return new PreprocessedFrame(frame.Metadata, outputs);
        }
        catch
        {
            foreach (var output in outputs)
            {
                output.Dispose();
            }

            throw;
        }
    }

    private PreprocessedRegionImage ProcessRegion(
        CaptureFrame frame,
        CaptureRegion region,
        PreprocessingOptions options,
        CancellationToken cancellationToken)
    {
        var cropBounds = RegionCoordinateMapper.NormalizedToClientPixels(
            region.Bounds,
            new PixelBounds(0, 0, frame.Width, frame.Height));
        ValidateOutputSize(cropBounds.Width, cropBounds.Height, options);
        WorkingImage? image = null;
        try
        {
            image = Crop(frame, cropBounds, cancellationToken);
            if (options.Grayscale)
            {
                image = Replace(image, ConvertToGrayscale(image, cancellationToken));
                ApplyPointOperations(image, options, cancellationToken);
                if (options.Sharpen)
                {
                    image = Replace(image, Sharpen(image, cancellationToken));
                }
            }

            if (options.Scale != 1)
            {
                image = Replace(image, Scale(image, options.Scale, options, cancellationToken));
            }

            if (options.PaddingPixels > 0)
            {
                image = Replace(image, Pad(image, options.PaddingPixels, options.PaddingValue, options, cancellationToken));
            }

            ValidateOutputSize(image.Width, image.Height, options);
            var lease = image.DetachLease();
            try
            {
                return new PreprocessedRegionImage(
                    region.Id,
                    region.Bounds,
                    image.Width,
                    image.Height,
                    image.PixelFormat,
                    lease.Memory,
                    lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        finally
        {
            image?.Dispose();
        }
    }

    private WorkingImage Crop(CaptureFrame frame, PixelBounds bounds, CancellationToken cancellationToken)
    {
        var rowBytes = checked(bounds.Width * 4);
        var image = CreateImage(bounds.Width, bounds.Height, OcrPixelFormat.Bgra32);
        try
        {
            var source = frame.BgraPixels.Span;
            var destination = image.Pixels.Span;
            for (var row = 0; row < bounds.Height; row++)
            {
                CheckRowCancellation(row, cancellationToken);
                var sourceOffset = checked(((bounds.Top + row) * frame.Width + bounds.Left) * 4);
                source.Slice(sourceOffset, rowBytes).CopyTo(destination.Slice(row * rowBytes, rowBytes));
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private WorkingImage ConvertToGrayscale(WorkingImage source, CancellationToken cancellationToken)
    {
        var output = CreateImage(source.Width, source.Height, OcrPixelFormat.Gray8);
        try
        {
            var inputPixels = source.Pixels.Span;
            var outputPixels = output.Pixels.Span;
            for (var row = 0; row < source.Height; row++)
            {
                CheckRowCancellation(row, cancellationToken);
                var rowOffset = row * source.Width;
                for (var column = 0; column < source.Width; column++)
                {
                    var sourceOffset = checked((rowOffset + column) * 4);
                    outputPixels[rowOffset + column] = (byte)((
                        (29 * inputPixels[sourceOffset]) +
                        (150 * inputPixels[sourceOffset + 1]) +
                        (77 * inputPixels[sourceOffset + 2]) + 128) >> 8);
                }
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void ApplyPointOperations(
        WorkingImage image,
        PreprocessingOptions options,
        CancellationToken cancellationToken)
    {
        var pixels = image.Pixels.Span;
        for (var row = 0; row < image.Height; row++)
        {
            CheckRowCancellation(row, cancellationToken);
            var rowOffset = row * image.Width;
            for (var column = 0; column < image.Width; column++)
            {
                var index = rowOffset + column;
                var value = pixels[index];
                if (options.Contrast != 1)
                {
                    value = (byte)Math.Clamp(
                        (int)Math.Round(((value - 128) * options.Contrast) + 128),
                        0,
                        255);
                }

                if (options.Threshold is { } threshold)
                {
                    value = value >= threshold ? byte.MaxValue : byte.MinValue;
                }

                pixels[index] = options.Invert ? (byte)(byte.MaxValue - value) : value;
            }
        }
    }

    private WorkingImage Sharpen(WorkingImage source, CancellationToken cancellationToken)
    {
        var output = CreateImage(source.Width, source.Height, OcrPixelFormat.Gray8);
        try
        {
            var input = source.Pixels.Span;
            var result = output.Pixels.Span;
            input.CopyTo(result);
            for (var row = 1; row < source.Height - 1; row++)
            {
                CheckRowCancellation(row, cancellationToken);
                for (var column = 1; column < source.Width - 1; column++)
                {
                    var index = (row * source.Width) + column;
                    var sharpened = (5 * input[index]) - input[index - 1] - input[index + 1] -
                        input[index - source.Width] - input[index + source.Width];
                    result[index] = (byte)Math.Clamp(sharpened, 0, 255);
                }
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private WorkingImage Scale(
        WorkingImage source,
        double factor,
        PreprocessingOptions options,
        CancellationToken cancellationToken)
    {
        var width = checked((int)Math.Round(source.Width * factor, MidpointRounding.AwayFromZero));
        var height = checked((int)Math.Round(source.Height * factor, MidpointRounding.AwayFromZero));
        ValidateOutputSize(width, height, options);
        var output = CreateImage(width, height, source.PixelFormat);
        var bytesPerPixel = source.PixelFormat == OcrPixelFormat.Bgra32 ? 4 : 1;
        try
        {
            var input = source.Pixels.Span;
            var result = output.Pixels.Span;
            for (var row = 0; row < height; row++)
            {
                CheckRowCancellation(row, cancellationToken);
                var sourceRow = Math.Min(source.Height - 1, (int)(row / factor));
                for (var column = 0; column < width; column++)
                {
                    var sourceColumn = Math.Min(source.Width - 1, (int)(column / factor));
                    var sourceOffset = ((sourceRow * source.Width) + sourceColumn) * bytesPerPixel;
                    var targetOffset = ((row * width) + column) * bytesPerPixel;
                    input.Slice(sourceOffset, bytesPerPixel).CopyTo(result.Slice(targetOffset, bytesPerPixel));
                }
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private WorkingImage Pad(
        WorkingImage source,
        int padding,
        byte value,
        PreprocessingOptions options,
        CancellationToken cancellationToken)
    {
        var width = checked(source.Width + (2 * padding));
        var height = checked(source.Height + (2 * padding));
        ValidateOutputSize(width, height, options);
        var output = CreateImage(width, height, source.PixelFormat);
        var bytesPerPixel = source.PixelFormat == OcrPixelFormat.Bgra32 ? 4 : 1;
        try
        {
            output.Pixels.Span.Fill(value);
            var sourcePixels = source.Pixels.Span;
            var targetPixels = output.Pixels.Span;
            var sourceRowBytes = source.Width * bytesPerPixel;
            for (var row = 0; row < source.Height; row++)
            {
                CheckRowCancellation(row, cancellationToken);
                var targetOffset = (((row + padding) * width) + padding) * bytesPerPixel;
                sourcePixels.Slice(row * sourceRowBytes, sourceRowBytes)
                    .CopyTo(targetPixels.Slice(targetOffset, sourceRowBytes));
            }

            if (source.PixelFormat == OcrPixelFormat.Bgra32)
            {
                for (var index = 3; index < targetPixels.Length; index += 4)
                {
                    targetPixels[index] = byte.MaxValue;
                }
            }

            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private WorkingImage CreateImage(int width, int height, OcrPixelFormat format)
    {
        var bytesPerPixel = format == OcrPixelFormat.Bgra32 ? 4 : 1;
        var length = CheckedLength(width, height, bytesPerPixel);
        return new WorkingImage(width, height, format, new PooledPixelLease(_bufferPool, length));
    }

    private static WorkingImage Replace(WorkingImage previous, WorkingImage replacement)
    {
        previous.Dispose();
        return replacement;
    }

    private static void Validate(
        CaptureFrame frame,
        IReadOnlyList<CaptureRegion> regions,
        PreprocessingOptions options)
    {
        if (regions.Count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(regions), "Between one and eight regions are required.");
        }

        if (regions.Select(region => region.Id).Distinct().Count() != regions.Count)
        {
            throw new ArgumentException("Region IDs must be unique.", nameof(regions));
        }

        if (!double.IsFinite(options.Contrast) || options.Contrast is < 0.25 or > 4 ||
            !double.IsFinite(options.Scale) || options.Scale is < 0.5 or > 4 ||
            options.PaddingPixels is < 0 or > 64 ||
            options.MaximumOutputWidth is < 64 or > 8192 ||
            options.MaximumOutputHeight is < 64 or > 8192 ||
            options.MaximumOutputPixels is < 4096 or > 33_554_432)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Preprocessing limits are invalid.");
        }

        if (!options.Grayscale &&
            (options.Contrast != 1 || options.Threshold is not null || options.Invert || options.Sharpen))
        {
            throw new ArgumentException("Grayscale operations require grayscale output.", nameof(options));
        }

        _ = CheckedLength(frame.Width, frame.Height, 4);
    }

    private static void ValidateOutputSize(int width, int height, PreprocessingOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var pixels = checked((long)width * height);
        if (width > options.MaximumOutputWidth || height > options.MaximumOutputHeight ||
            pixels > options.MaximumOutputPixels)
        {
            throw new InvalidOperationException(
                $"Preprocessed output {width}×{height} exceeds the configured local resolution cap.");
        }
    }

    private static int CheckedLength(int width, int height, int bytesPerPixel)
    {
        var length = checked((long)width * height * bytesPerPixel);
        if (length > int.MaxValue)
        {
            throw new OverflowException("Pixel buffer exceeds the supported managed array size.");
        }

        return checked((int)length);
    }

    private static void CheckRowCancellation(int row, CancellationToken cancellationToken)
    {
        if ((row & 31) == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class WorkingImage : IDisposable
    {
        private PooledPixelLease? _lease;

        public WorkingImage(int width, int height, OcrPixelFormat pixelFormat, PooledPixelLease lease)
        {
            Width = width;
            Height = height;
            PixelFormat = pixelFormat;
            _lease = lease;
        }

        public int Width { get; }

        public int Height { get; }

        public OcrPixelFormat PixelFormat { get; }

        public Memory<byte> Pixels => (_lease ?? throw new ObjectDisposedException(nameof(WorkingImage))).Memory;

        public PooledPixelLease DetachLease() =>
            Interlocked.Exchange(ref _lease, null) ?? throw new ObjectDisposedException(nameof(WorkingImage));

        public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
    }
}

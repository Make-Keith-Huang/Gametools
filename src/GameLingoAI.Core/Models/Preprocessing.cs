namespace GameLingoAI.Core.Models;

public enum PreprocessingPreset
{
    None,
    PlainSubtitle,
    OutlinedSubtitle,
    Custom
}

public sealed record PreprocessingOptions
{
    public PreprocessingPreset Preset { get; init; } = PreprocessingPreset.PlainSubtitle;

    public bool Grayscale { get; init; } = true;

    public double Contrast { get; init; } = 1.15;

    public byte? Threshold { get; init; }

    public bool Invert { get; init; }

    public bool Sharpen { get; init; }

    public double Scale { get; init; } = 1.5;

    public int PaddingPixels { get; init; } = 4;

    public byte PaddingValue { get; init; }

    public int MaximumOutputWidth { get; init; } = 4096;

    public int MaximumOutputHeight { get; init; } = 4096;

    public int MaximumOutputPixels { get; init; } = 16_777_216;

    public bool EnableDiagnosticImages { get; init; }

    public static PreprocessingOptions NoOp { get; } = new()
    {
        Preset = PreprocessingPreset.None,
        Grayscale = false,
        Contrast = 1,
        Scale = 1,
        PaddingPixels = 0
    };

    public static PreprocessingOptions PlainSubtitle { get; } = new();

    public static PreprocessingOptions OutlinedSubtitle { get; } = new()
    {
        Preset = PreprocessingPreset.OutlinedSubtitle,
        Grayscale = true,
        Contrast = 1.5,
        Threshold = 160,
        Sharpen = true,
        Scale = 2,
        PaddingPixels = 8
    };
}

public enum OcrPixelFormat
{
    Bgra32,
    Gray8
}

/// <summary>
/// A local OCR-ready image. The consumer owns and must dispose it; its pixel memory is never
/// valid provider input and remains available only until disposal.
/// </summary>
public sealed class PreprocessedRegionImage : IDisposable
{
    private IFrameLease? _lease;

    public PreprocessedRegionImage(
        Guid regionId,
        NormalizedRegion sourceBounds,
        int width,
        int height,
        OcrPixelFormat pixelFormat,
        ReadOnlyMemory<byte> pixels,
        IFrameLease lease)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(regionId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var bytesPerPixel = pixelFormat == OcrPixelFormat.Bgra32 ? 4 : 1;
        var requiredLength = checked(width * height * bytesPerPixel);
        if (pixels.Length < requiredLength)
        {
            throw new ArgumentException("Pixel storage is smaller than the declared image.", nameof(pixels));
        }

        RegionId = regionId;
        SourceBounds = sourceBounds;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Pixels = pixels[..requiredLength];
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    public Guid RegionId { get; }

    public NormalizedRegion SourceBounds { get; }

    public int Width { get; }

    public int Height { get; }

    public OcrPixelFormat PixelFormat { get; }

    public ReadOnlyMemory<byte> Pixels { get; }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

/// <summary>Owns every region image produced from one source-frame sequence.</summary>
public sealed class PreprocessedFrame : IDisposable
{
    private IReadOnlyList<PreprocessedRegionImage>? _regions;

    public PreprocessedFrame(PipelineMetadata metadata, IReadOnlyList<PreprocessedRegionImage> regions)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        ArgumentNullException.ThrowIfNull(regions);
        if (regions.Count == 0)
        {
            throw new ArgumentException("At least one preprocessed region is required.", nameof(regions));
        }

        _regions = regions;
    }

    public PipelineMetadata Metadata { get; }

    public IReadOnlyList<PreprocessedRegionImage> Regions =>
        _regions ?? throw new ObjectDisposedException(nameof(PreprocessedFrame));

    public void Dispose()
    {
        var regions = Interlocked.Exchange(ref _regions, null);
        if (regions is null)
        {
            return;
        }

        foreach (var region in regions)
        {
            region.Dispose();
        }
    }
}

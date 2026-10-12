namespace GameLingoAI.Core.Models;

public readonly record struct DipPoint(double X, double Y);

public readonly record struct DipRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

public readonly record struct PixelPoint(double X, double Y);

public sealed record RegionCoordinateContext
{
    public RegionCoordinateContext(
        int captureWidth,
        int captureHeight,
        double previewWidthDips,
        double previewHeightDips,
        double dpiX,
        double dpiY)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(captureHeight);
        ValidateFinitePositive(previewWidthDips, nameof(previewWidthDips));
        ValidateFinitePositive(previewHeightDips, nameof(previewHeightDips));
        ValidateFinitePositive(dpiX, nameof(dpiX));
        ValidateFinitePositive(dpiY, nameof(dpiY));
        CaptureWidth = captureWidth;
        CaptureHeight = captureHeight;
        PreviewWidthDips = previewWidthDips;
        PreviewHeightDips = previewHeightDips;
        DpiX = dpiX;
        DpiY = dpiY;
    }

    public int CaptureWidth { get; }

    public int CaptureHeight { get; }

    public double PreviewWidthDips { get; }

    public double PreviewHeightDips { get; }

    public double DpiX { get; }

    public double DpiY { get; }

    private static void ValidateFinitePositive(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The value must be finite and positive.");
        }
    }
}

public readonly record struct PreviewLayout(
    double ContentLeftDips,
    double ContentTopDips,
    double ContentWidthDips,
    double ContentHeightDips,
    double DipsPerPixelX,
    double DipsPerPixelY);

/// <summary>
/// Converts local preview DIPs, capture pixels, client pixels, and normalized geometry. The
/// service has no screen-capture or persistence side effects.
/// </summary>
public static class RegionCoordinateMapper
{
    public static PreviewLayout GetPreviewLayout(RegionCoordinateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var naturalWidthDips = context.CaptureWidth * 96d / context.DpiX;
        var naturalHeightDips = context.CaptureHeight * 96d / context.DpiY;
        var uniformScale = Math.Min(
            context.PreviewWidthDips / naturalWidthDips,
            context.PreviewHeightDips / naturalHeightDips);
        var contentWidth = naturalWidthDips * uniformScale;
        var contentHeight = naturalHeightDips * uniformScale;
        return new PreviewLayout(
            (context.PreviewWidthDips - contentWidth) / 2d,
            (context.PreviewHeightDips - contentHeight) / 2d,
            contentWidth,
            contentHeight,
            contentWidth / context.CaptureWidth,
            contentHeight / context.CaptureHeight);
    }

    public static DipRect NormalizedToPreviewDips(
        NormalizedRegion region,
        RegionCoordinateContext context)
    {
        var layout = GetPreviewLayout(context);
        return new DipRect(
            layout.ContentLeftDips + (region.X * layout.ContentWidthDips),
            layout.ContentTopDips + (region.Y * layout.ContentHeightDips),
            region.Width * layout.ContentWidthDips,
            region.Height * layout.ContentHeightDips);
    }

    public static NormalizedRegion PreviewDipsToNormalized(
        DipRect previewRect,
        RegionCoordinateContext context,
        int minimumWidthPixels = 8,
        int minimumHeightPixels = 8)
    {
        ValidateMinimumPixels(minimumWidthPixels, minimumHeightPixels);
        ValidateRect(previewRect);
        var layout = GetPreviewLayout(context);
        var minimumWidth = Math.Min(1d, (double)minimumWidthPixels / context.CaptureWidth);
        var minimumHeight = Math.Min(1d, (double)minimumHeightPixels / context.CaptureHeight);
        var width = Math.Clamp(previewRect.Width / layout.ContentWidthDips, minimumWidth, 1d);
        var height = Math.Clamp(previewRect.Height / layout.ContentHeightDips, minimumHeight, 1d);
        var x = Math.Clamp(
            (previewRect.X - layout.ContentLeftDips) / layout.ContentWidthDips,
            0d,
            1d - width);
        var y = Math.Clamp(
            (previewRect.Y - layout.ContentTopDips) / layout.ContentHeightDips,
            0d,
            1d - height);
        return new NormalizedRegion(x, y, width, height);
    }

    public static PixelPoint PreviewDipsToCapturePixels(
        DipPoint previewPoint,
        RegionCoordinateContext context,
        bool clampToContent)
    {
        if (!double.IsFinite(previewPoint.X) || !double.IsFinite(previewPoint.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(previewPoint));
        }

        var layout = GetPreviewLayout(context);
        var x = (previewPoint.X - layout.ContentLeftDips) / layout.DipsPerPixelX;
        var y = (previewPoint.Y - layout.ContentTopDips) / layout.DipsPerPixelY;
        if (clampToContent)
        {
            x = Math.Clamp(x, 0, context.CaptureWidth);
            y = Math.Clamp(y, 0, context.CaptureHeight);
        }

        return new PixelPoint(x, y);
    }

    public static DipPoint CapturePixelsToPreviewDips(PixelPoint pixel, RegionCoordinateContext context)
    {
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(pixel));
        }

        var layout = GetPreviewLayout(context);
        return new DipPoint(
            layout.ContentLeftDips + (pixel.X * layout.DipsPerPixelX),
            layout.ContentTopDips + (pixel.Y * layout.DipsPerPixelY));
    }

    public static PixelBounds NormalizedToClientPixels(NormalizedRegion region, PixelBounds clientBounds)
    {
        var left = checked(clientBounds.Left + RoundPixel(region.X * clientBounds.Width));
        var top = checked(clientBounds.Top + RoundPixel(region.Y * clientBounds.Height));
        var right = checked(clientBounds.Left + RoundPixel((region.X + region.Width) * clientBounds.Width));
        var bottom = checked(clientBounds.Top + RoundPixel((region.Y + region.Height) * clientBounds.Height));
        return new PixelBounds(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    public static NormalizedRegion ClientPixelsToNormalized(PixelBounds region, PixelBounds clientBounds)
    {
        var localLeft = region.Left - clientBounds.Left;
        var localTop = region.Top - clientBounds.Top;
        if (localLeft < 0 || localTop < 0 ||
            region.Left + region.Width > clientBounds.Left + clientBounds.Width ||
            region.Top + region.Height > clientBounds.Top + clientBounds.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "The region must be inside the client bounds.");
        }

        return new NormalizedRegion(
            (double)localLeft / clientBounds.Width,
            (double)localTop / clientBounds.Height,
            (double)region.Width / clientBounds.Width,
            (double)region.Height / clientBounds.Height);
    }

    public static NormalizedRegion Clamp(
        NormalizedRegion region,
        RegionCoordinateContext context,
        int minimumWidthPixels = 8,
        int minimumHeightPixels = 8)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateMinimumPixels(minimumWidthPixels, minimumHeightPixels);
        var minimumWidth = Math.Min(1d, (double)minimumWidthPixels / context.CaptureWidth);
        var minimumHeight = Math.Min(1d, (double)minimumHeightPixels / context.CaptureHeight);
        var width = Math.Clamp(region.Width, minimumWidth, 1d);
        var height = Math.Clamp(region.Height, minimumHeight, 1d);
        var x = Math.Clamp(region.X, 0d, 1d - width);
        var y = Math.Clamp(region.Y, 0d, 1d - height);
        return new NormalizedRegion(x, y, width, height);
    }

    public static DipPoint ScreenPixelsToClientDips(
        PixelPoint screenPixel,
        PixelBounds clientBounds,
        double dpiX,
        double dpiY)
    {
        if (!double.IsFinite(dpiX) || dpiX <= 0 || !double.IsFinite(dpiY) || dpiY <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dpiX), "DPI must be finite and positive.");
        }

        return new DipPoint(
            (screenPixel.X - clientBounds.Left) * 96d / dpiX,
            (screenPixel.Y - clientBounds.Top) * 96d / dpiY);
    }

    private static int RoundPixel(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static void ValidateMinimumPixels(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
    }

    private static void ValidateRect(DipRect rect)
    {
        if (!double.IsFinite(rect.X) || !double.IsFinite(rect.Y) ||
            !double.IsFinite(rect.Width) || !double.IsFinite(rect.Height) ||
            rect.Width <= 0 || rect.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rect), "The rectangle must be finite and positive.");
        }
    }
}

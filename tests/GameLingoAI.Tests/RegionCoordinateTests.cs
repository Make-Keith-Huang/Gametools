using GameLingoAI.Core.Models;

internal static class RegionCoordinateTests
{
    private static readonly double[] SupportedDpi = [96, 144, 192];

    public static void Run()
    {
        TestPropertyRoundTrips();
        TestLetterboxAndDpiMapping();
        TestClampingAndMinimumSize();
        TestInvalidInputs();
    }

    private static void TestPropertyRoundTrips()
    {
        var random = new Random(7_031);
        foreach (var dpi in SupportedDpi)
        {
            for (var index = 0; index < 500; index++)
            {
                var captureWidth = random.Next(320, 3841);
                var captureHeight = random.Next(240, 2161);
                var previewWidth = random.Next(240, 1201);
                var previewHeight = random.Next(180, 901);
                var width = 0.02 + (random.NextDouble() * 0.78);
                var height = 0.02 + (random.NextDouble() * 0.78);
                var x = random.NextDouble() * (1 - width);
                var y = random.NextDouble() * (1 - height);
                var region = new NormalizedRegion(x, y, width, height);
                var context = new RegionCoordinateContext(
                    captureWidth,
                    captureHeight,
                    previewWidth,
                    previewHeight,
                    dpi,
                    dpi);

                var preview = RegionCoordinateMapper.NormalizedToPreviewDips(region, context);
                var roundTrip = RegionCoordinateMapper.PreviewDipsToNormalized(preview, context, 1, 1);
                AssertClose(region.X, roundTrip.X, 1d / captureWidth, "Preview X round-trip");
                AssertClose(region.Y, roundTrip.Y, 1d / captureHeight, "Preview Y round-trip");
                AssertClose(region.Width, roundTrip.Width, 1d / captureWidth, "Preview width round-trip");
                AssertClose(region.Height, roundTrip.Height, 1d / captureHeight, "Preview height round-trip");

                var client = new PixelBounds(-700, 125, captureWidth, captureHeight);
                var pixels = RegionCoordinateMapper.NormalizedToClientPixels(region, client);
                var normalizedAgain = RegionCoordinateMapper.ClientPixelsToNormalized(pixels, client);
                var pixelsAgain = RegionCoordinateMapper.NormalizedToClientPixels(normalizedAgain, client);
                Assert(Math.Abs(pixels.Left - pixelsAgain.Left) <= 1, "Client X must round-trip within one pixel.");
                Assert(Math.Abs(pixels.Top - pixelsAgain.Top) <= 1, "Client Y must round-trip within one pixel.");
                Assert(Math.Abs(pixels.Width - pixelsAgain.Width) <= 1, "Client width must round-trip within one pixel.");
                Assert(Math.Abs(pixels.Height - pixelsAgain.Height) <= 1, "Client height must round-trip within one pixel.");
            }
        }
    }

    private static void TestLetterboxAndDpiMapping()
    {
        var context = new RegionCoordinateContext(1920, 1080, 500, 500, 144, 144);
        var layout = RegionCoordinateMapper.GetPreviewLayout(context);
        AssertClose(0, layout.ContentLeftDips, 0.0001, "Wide content should fill preview width.");
        Assert(layout.ContentTopDips > 100, "Wide content should be letterboxed vertically.");

        var topLeft = RegionCoordinateMapper.CapturePixelsToPreviewDips(new PixelPoint(0, 0), context);
        var bottomRight = RegionCoordinateMapper.CapturePixelsToPreviewDips(new PixelPoint(1920, 1080), context);
        AssertClose(layout.ContentLeftDips, topLeft.X, 0.0001, "Capture origin X mapping");
        AssertClose(layout.ContentTopDips, topLeft.Y, 0.0001, "Capture origin Y mapping");
        AssertClose(layout.ContentLeftDips + layout.ContentWidthDips, bottomRight.X, 0.0001, "Capture edge X mapping");
        AssertClose(layout.ContentTopDips + layout.ContentHeightDips, bottomRight.Y, 0.0001, "Capture edge Y mapping");

        var clamped = RegionCoordinateMapper.PreviewDipsToCapturePixels(new DipPoint(-20, 700), context, true);
        AssertClose(0, clamped.X, 0.0001, "Letterbox point should clamp X.");
        AssertClose(1080, clamped.Y, 0.0001, "Letterbox point should clamp Y.");

        var clientDips = RegionCoordinateMapper.ScreenPixelsToClientDips(
            new PixelPoint(250, 350),
            new PixelBounds(100, 200, 800, 600),
            144,
            192);
        AssertClose(100, clientDips.X, 0.0001, "150% horizontal DPI conversion");
        AssertClose(75, clientDips.Y, 0.0001, "200% vertical DPI conversion");
    }

    private static void TestClampingAndMinimumSize()
    {
        var context = new RegionCoordinateContext(800, 600, 400, 300, 96, 96);
        var clamped = RegionCoordinateMapper.PreviewDipsToNormalized(
            new DipRect(-200, -100, 1, 1),
            context,
            16,
            12);
        AssertClose(0, clamped.X, 0.0001, "Clamped X");
        AssertClose(0, clamped.Y, 0.0001, "Clamped Y");
        AssertClose(16d / 800, clamped.Width, 0.0001, "Minimum pixel width");
        AssertClose(12d / 600, clamped.Height, 0.0001, "Minimum pixel height");

        var nearEdge = RegionCoordinateMapper.PreviewDipsToNormalized(
            new DipRect(399, 299, 100, 100),
            context);
        Assert(nearEdge.X + nearEdge.Width <= 1 && nearEdge.Y + nearEdge.Height <= 1,
            "Clamped region must remain inside normalized content.");
    }

    private static void TestInvalidInputs()
    {
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new RegionCoordinateContext(0, 100, 200, 100, 96, 96));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = RegionCoordinateMapper.PreviewDipsToNormalized(
                new DipRect(0, 0, double.NaN, 2),
                new RegionCoordinateContext(100, 100, 100, 100, 96, 96)));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = RegionCoordinateMapper.ClientPixelsToNormalized(
                new PixelBounds(95, 95, 10, 10),
                new PixelBounds(0, 0, 100, 100)));
        AssertThrows<ArgumentOutOfRangeException>(() =>
            _ = new CaptureRegion(Guid.Empty, "Dialogue", new NormalizedRegion(0, 0, 1, 1), 0));
        AssertThrows<ArgumentException>(() =>
            _ = new CaptureRegion(Guid.NewGuid(), " ", new NormalizedRegion(0, 0, 1, 1), 0));
    }

    private static void AssertClose(double expected, double actual, double tolerance, string description)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            throw new InvalidOperationException(
                $"{description}: expected {expected}, actual {actual}, tolerance {tolerance}.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}

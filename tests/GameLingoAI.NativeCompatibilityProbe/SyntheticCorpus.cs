using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Ocr;

// Fixed authored text only: no screen capture, files, user text or remote requests.
internal static class SyntheticCorpus
{
    private const int Width = 1000;
    private const int Height = 160;

    public static async Task<int> RunAsync(OcrModelPackage package, string models, CancellationToken token)
    {
        var fixtures = new (string Id, string Text)[]
        {
            ("latin", "HELLO WORLD 123"),
            ("simplified", "欢迎来到新的世界"),
            ("traditional", "歡迎來到新的世界"),
            ("japanese", "こんにちは世界")
        };
        token.ThrowIfCancellationRequested();
        using var runtime = new SdcbPaddleOcrRuntimeFactory().Create(package, models);
        var failures = 0;
        foreach (var fixture in fixtures)
        {
            foreach (var style in new[] { "normal", "small", "outlined" })
            {
                token.ThrowIfCancellationRequested();
                using var image = await RenderAsync(fixture.Text, style).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                var hash = Convert.ToHexString(SHA256.HashData(image.Pixels.Span));
                var stopwatch = Stopwatch.StartNew();
                var spans = runtime.Recognize(image, token);
                stopwatch.Stop();
                var actual = string.Concat(spans.Select(span => span.Text));
                var correct = WithoutWhitespace(actual) == WithoutWhitespace(fixture.Text) &&
                    spans.All(span => double.IsFinite(span.Confidence) && span.Width > 0 && span.Height > 0);
                if (!correct)
                { failures++; }
                Console.WriteLine(FormattableString.Invariant(
                    $"Corpus v1 {fixture.Id}/{style}: {(correct ? "PASS" : "FAIL")}; pixels={hash}; nativeMs={stopwatch.Elapsed.TotalMilliseconds:F1}"));
            }
        }

        Console.WriteLine($"Corpus v1: {12 - failures}/12 exact-text cases passed. Native inference only; not capture-to-overlay latency or general OCR accuracy.");
        return failures == 0 ? 0 : 3;
    }

    private static string WithoutWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));

    internal static Task<PreprocessedRegionImage> RenderAsync(string text, string style)
    {
        var completion = new TaskCompletionSource<PreprocessedRegionImage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var outlined = style == "outlined";
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(outlined ? Brushes.DimGray : Brushes.White, null, new Rect(0, 0, Width, Height));
                    var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Microsoft YaHei"), style == "small" ? 24 : 48,
                        outlined ? Brushes.White : Brushes.Black, 1);
                    var geometry = formatted.BuildGeometry(new Point(32, 48));
                    drawing.DrawGeometry(outlined ? Brushes.White : Brushes.Black,
                        outlined ? new Pen(Brushes.Black, 2) : null, geometry);
                }

                var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                // The opaque full background makes premultiplied BGRA identical to BGRA.
                var pixels = new byte[Width * Height * 4];
                bitmap.CopyPixels(pixels, Width * 4, 0);
                completion.SetResult(new PreprocessedRegionImage(Guid.NewGuid(), new NormalizedRegion(0, 0, 1, 1),
                    Width, Height, OcrPixelFormat.Bgra32, pixels, new PixelLease(pixels)));
            }
            catch (Exception error) { completion.SetException(error); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

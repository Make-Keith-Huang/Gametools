using System.Runtime.InteropServices;
using System.Security.Cryptography;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Ocr;
using OpenCvSharp;

// Explicit, isolated maintainer probe; never invoked by the app or ordinary tests.
try
{
    return await RunProbeAsync(args);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Native probe cancelled.");
    return 1;
}
catch (Exception error)
{
    // Test failures must not escape into Windows managed-crash reporting. Do not print private paths/messages.
    Console.Error.WriteLine("Native probe failed: " + error.GetType().Name);
    return 1;
}

static async Task<int> RunProbeAsync(string[] args)
{
    if (args.Length != 2 || args[0] is not ("inspect" or "download-and-test" or "test" or "corpus" or "benchmark" or "stages" or "reuse" or "reuse-cropped" or "reuse-integrated" or "reuse-lifecycle" or "reuse-lifecycle-cached" or "reuse-sustained" or "reuse-cancellation" or "reuse-multiline" or "reuse-multiline-batched" or "reuse-production"))
    {
        Console.Error.WriteLine("Usage: inspect|download-and-test|test|corpus|benchmark|stages|reuse|reuse-cropped|reuse-integrated|reuse-lifecycle|reuse-lifecycle-cached|reuse-sustained|reuse-cancellation|reuse-multiline|reuse-multiline-batched <isolated-model-root>");
        return 2;
    }

    Console.WriteLine(Cv2.GetBuildInformation());
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(args[0] == "reuse-sustained" ? 65 : 10));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    var provisioner = new OcrModelProvisioner(client, args[1]);
    var package = PaddleOcrModelCatalog.PpOcrV5Mobile;
    if (args[0] == "inspect")
    {
        using var source = new Mat(32, 32, MatType.CV_8UC1, Scalar.White);
        using var destination = new Mat();
        Cv2.CvtColor(source, destination, ColorConversionCodes.GRAY2BGR);
        Console.WriteLine("Native load and color conversion passed; OCR not tested.");
        return 0;
    }

    var status = args[0] == "download-and-test"
        ? await provisioner.ProvisionAsync(package, null, cancellation.Token)
        : await provisioner.GetStatusAsync(package, cancellation.Token);
    if (status.State != OcrModelState.Ready)
    {
        throw new InvalidOperationException("Isolated models are not verified/ready.");
    }

    Console.WriteLine("Models verified. Starting production runtime on synthetic text only.");
    if (args[0] == "reuse-production")
    {
        return await ProductionReuseProbe.RunAsync(package, status.InstallationPath!, cancellation.Token);
    }
    if (args[0] is "reuse-multiline" or "reuse-multiline-batched")
    {
        return await OcrReuseProbe.RunMultilineAsync(package, status.InstallationPath!, cancellation.Token, args[0] == "reuse-multiline-batched");
    }
    if (args[0] == "reuse-cancellation")
    {
        return await OcrReuseProbe.RunCancellationAsync(package, status.InstallationPath!, cancellation.Token);
    }
    if (args[0] == "reuse-sustained")
    {
        return await OcrReuseProbe.RunSustainedAsync(package, status.InstallationPath!, cancellation.Token);
    }
    if (args[0] is "reuse-cropped" or "reuse-integrated" or "reuse-lifecycle" or "reuse-lifecycle-cached")
    {
        return await OcrReuseProbe.RunCroppedAsync(package, status.InstallationPath!, cancellation.Token,
            args[0] != "reuse-cropped", args[0].StartsWith("reuse-lifecycle", StringComparison.Ordinal) ? 10 : 2, args[0] == "reuse-lifecycle-cached");
    }
    if (args[0] == "reuse")
    {
        return await OcrReuseProbe.RunAsync(package, status.InstallationPath!, cancellation.Token);
    }
    if (args[0] is "benchmark" or "stages")
    {
        try
        {
            return args[0] == "stages"
                ? await OcrStageProbe.RunAsync(package, status.InstallationPath!, cancellation.Token)
                : await OcrPerformanceProbe.RunAsync(provisioner, package, status.InstallationPath!, cancellation.Token);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Performance probe failed: " + error.GetType().Name);
            return 1;
        }
    }
    if (args[0] == "corpus")
    {
        return await SyntheticCorpus.RunAsync(package, status.InstallationPath!, cancellation.Token);
    }

    for (int cycle = 0; cycle < 2; cycle++)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        using var runtime = new SdcbPaddleOcrRuntimeFactory().Create(package, status.InstallationPath!);
        foreach (var format in new[] { OcrPixelFormat.Gray8, OcrPixelFormat.Bgra32 })
        {
            using var mat = new Mat(160, 1000,
                format == OcrPixelFormat.Gray8 ? MatType.CV_8UC1 : MatType.CV_8UC4, Scalar.All(255));
            Cv2.PutText(mat, "HELLO WORLD 123", new Point(35, 105), HersheyFonts.HersheySimplex,
                2, Scalar.All(0), 3, LineTypes.AntiAlias);
            var pixels = new byte[checked((int)(mat.Total() * mat.ElemSize()))];
            Marshal.Copy(mat.Data, pixels, 0, pixels.Length);
            using var image = new PreprocessedRegionImage(Guid.NewGuid(), new NormalizedRegion(0, 0, 1, 1),
                mat.Width, mat.Height, format, pixels, new PixelLease(pixels));
            var spans = runtime.Recognize(image);
            var text = string.Join(" ", spans.Select(span => span.Text));
            if (!text.Contains("HELLO", StringComparison.OrdinalIgnoreCase) ||
                !text.Contains("123", StringComparison.Ordinal) ||
                spans.Any(span => !double.IsFinite(span.Confidence) || span.Width <= 0 || span.Height <= 0))
            {
                throw new InvalidOperationException("Synthetic OCR text/geometry assertion failed.");
            }

            Console.WriteLine($"Cycle {cycle + 1}, {format}: synthetic text/geometry passed ({spans.Count} spans).");
            pixels.AsSpan().Fill(255);
            if (runtime.Recognize(image).Count != 0)
            {
                throw new InvalidOperationException("Blank synthetic image must have no OCR spans.");
            }

            if (format == OcrPixelFormat.Bgra32)
            {
                runtime.Dispose();
                try
                {
                    runtime.Recognize(image);
                    throw new InvalidOperationException("Disposed native runtime accepted an image.");
                }
                catch (ObjectDisposedException)
                {
                    Console.WriteLine("Blank image and disposed-runtime guards passed.");
                }
            }
        }
    }

    Console.WriteLine("Native OCR compatibility passed: two runtime create/dispose cycles, both pixel formats.");
    return 0;
}

internal sealed class PixelLease(byte[] pixels) : IFrameLease
{
    public void Dispose() => CryptographicOperations.ZeroMemory(pixels);
}

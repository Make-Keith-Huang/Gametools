using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Ocr;

internal static class OcrTests
{
    public static async Task RunAsync()
    {
        TestCatalogMetadata();
        foreach (var count in new[] { 1, 2, 4, 8 })
        { _ = new SdcbPaddleOcrRuntimeFactory(count); }
        foreach (var count in new[] { 0, -1, 9, int.MaxValue })
        {
            var rejected = false;
            try
            { _ = new SdcbPaddleOcrRuntimeFactory(count); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Assert(rejected, "Native OCR thread count must be bounded to 1..8 without loading native code.");
        }
        await TestProvisioningResumeIntegrityAndFailuresAsync().ConfigureAwait(false);
        await TestMappingLanguagesOrderingAndErrorsAsync().ConfigureAwait(false);
        await TestCancellationAndSerializationAsync().ConfigureAwait(false);
        await TestThousandFrameResourceSmokeAsync().ConfigureAwait(false);
    }

    private static void TestCatalogMetadata()
    {
        var package = PaddleOcrModelCatalog.PpOcrV5Mobile;
        Assert(package.DownloadSizeBytes == 21_770_240, "Built-in model size metadata changed unexpectedly.");
        Assert(package.LicenseName == "Apache-2.0" && package.LicenseUri.Scheme == Uri.UriSchemeHttps,
            "Model catalog must disclose its license over HTTPS.");
        Assert(package.SourceUri.Host == "github.com" && package.Artifacts.All(item => item.Sha256.Length == 64),
            "Model catalog must disclose source and SHA-256 metadata.");
        Assert(Enum.GetValues<OcrLanguage>().All(package.Languages.Contains),
            "The selected V1 model must declare every supported language.");
    }

    private static async Task TestProvisioningResumeIntegrityAndFailuresAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var detection = CreateModelTar("det");
            var recognition = CreateModelTar("rec");
            var package = CreatePackage("test-model", detection, recognition);
            var handler = new ModelDownloadHandler(new Dictionary<Uri, byte[]>
            {
                [package.Artifacts[0].DownloadUri] = detection,
                [package.Artifacts[1].DownloadUri] = recognition
            });
            var partialDirectory = Path.Combine(root, ".downloads", package.Id);
            Directory.CreateDirectory(partialDirectory);
            await File.WriteAllBytesAsync(
                Path.Combine(partialDirectory, package.Artifacts[0].FileName + ".partial"),
                detection[..17]).ConfigureAwait(false);

            var provisioner = new OcrModelProvisioner(new HttpClient(handler), root);
            var before = await provisioner.GetStatusAsync(package, CancellationToken.None).ConfigureAwait(false);
            Assert(before.State == OcrModelState.Partial, "A partial artifact must be reported as resumable.");
            var progress = new List<double>();
            var ready = await provisioner.ProvisionAsync(
                package,
                new InlineProgress(value => progress.Add(value)),
                CancellationToken.None).ConfigureAwait(false);
            Assert(handler.SawRangeRequest, "Provisioning must attempt an HTTP range resume for a partial artifact.");
            Assert(ready.State == OcrModelState.Ready && progress.Count > 0 && progress[^1] == 1,
                "Verified provisioning must finish in the ready state and report completion.");

            var installedFile = Path.Combine(ready.InstallationPath!, "rec", "inference.json");
            await File.AppendAllTextAsync(installedFile, "tampered").ConfigureAwait(false);
            var corrupt = await provisioner.GetStatusAsync(package, CancellationToken.None).ConfigureAwait(false);
            Assert(corrupt.State == OcrModelState.Corrupt, "A modified installed model must be reported as corrupt.");

            var badPackage = CreatePackage("bad-checksum", detection, recognition, corruptFirstHash: true);
            var badHandler = new ModelDownloadHandler(new Dictionary<Uri, byte[]>
            {
                [badPackage.Artifacts[0].DownloadUri] = detection,
                [badPackage.Artifacts[1].DownloadUri] = recognition
            });
            var badProvisioner = new OcrModelProvisioner(new HttpClient(badHandler), root);
            await AssertThrowsAsync<InvalidDataException>(() => badProvisioner.ProvisionAsync(
                badPackage,
                progress: null,
                CancellationToken.None)).ConfigureAwait(false);
            var afterBadHash = await badProvisioner.GetStatusAsync(badPackage, CancellationToken.None).ConfigureAwait(false);
            Assert(afterBadHash.State == OcrModelState.Missing,
                "A bad download checksum must be discarded so retry starts cleanly.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() => provisioner.ProvisionAsync(
                package,
                progress: null,
                cancelled.Token)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestMappingLanguagesOrderingAndErrorsAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = await ProvisionTestModelAsync(root).ConfigureAwait(false);
            var factory = new FakeRuntimeFactory(_ => new[]
            {
                new PaddleRuntimeSpan(" second ", 0.90, 50, 30, 20, 10),
                new PaddleRuntimeSpan("Ｆｉｒｓｔ", 0.95, 10, 5, 30, 10),
                new PaddleRuntimeSpan("low", 0.2, 0, 0, 10, 10),
                new PaddleRuntimeSpan("outside", 0.99, 200, 0, 10, 10)
            });
            var provisioner = new OcrModelProvisioner(new HttpClient(new ModelDownloadHandler(new Dictionary<Uri, byte[]>())), root);
            await using var engine = new PaddleOcrEngine(provisioner, factory, package);

            Assert(engine.Capabilities.Devices.SetEquals(new[] { OcrExecutionDevice.Cpu }) &&
                engine.Capabilities.SerializesInference && engine.Capabilities.RequiresExplicitModelProvisioning,
                "OCR capabilities must state CPU, serialized inference, and explicit provisioning.");

            foreach (var language in Enum.GetValues<OcrLanguage>())
            {
                using var frame = CreateFrame();
                var result = await engine.RecognizeAsync(
                    frame,
                    new OcrRecognitionOptions(language, 0.5),
                    CancellationToken.None).ConfigureAwait(false);
                Assert(result.IsSuccess && result.Value!.Spans.Count == 2,
                    $"Known {language} fixture must yield non-empty thresholded spans.");
                var value = result.Value ?? throw new InvalidOperationException("Successful OCR result had no value.");
                Assert(value.Spans[0].Text == "First" && value.Spans[1].Text == "second",
                    "Mapped spans must be Unicode-normalized and sorted in reading order.");
                Assert(value.Spans.All(span => span.RegionId != Guid.Empty),
                    "Mapped spans must preserve the source region ID.");
            }

            var missingRoot = CreateTemporaryDirectory();
            try
            {
                await using var missingEngine = new PaddleOcrEngine(
                    new OcrModelProvisioner(new HttpClient(new ModelDownloadHandler(new Dictionary<Uri, byte[]>())), missingRoot),
                    factory,
                    package);
                using var frame = CreateFrame();
                var missing = await missingEngine.RecognizeAsync(
                    frame,
                    new OcrRecognitionOptions(OcrLanguage.English),
                    CancellationToken.None).ConfigureAwait(false);
                Assert(missing.Failure?.Code == FailureCode.ModelMissing,
                    "An uninstalled model must produce ModelMissing.");
            }
            finally
            {
                Directory.Delete(missingRoot, recursive: true);
            }

            await File.AppendAllTextAsync(
                Path.Combine(root, package.Id, "det", "inference.json"),
                "tampered").ConfigureAwait(false);
            using (var frame = CreateFrame())
            {
                var corrupt = await engine.RecognizeAsync(
                    frame,
                    new OcrRecognitionOptions(OcrLanguage.English),
                    CancellationToken.None).ConfigureAwait(false);
                Assert(corrupt.Failure?.Code == FailureCode.ModelCorrupt,
                    "A modified model must produce ModelCorrupt, distinct from ModelMissing.");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestCancellationAndSerializationAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = await ProvisionTestModelAsync(root).ConfigureAwait(false);
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var runtime = new BlockingRuntime(started, release);
            var factory = new SingleRuntimeFactory(runtime);
            var provisioner = new OcrModelProvisioner(new HttpClient(new ModelDownloadHandler(new Dictionary<Uri, byte[]>())), root);
            await using var engine = new PaddleOcrEngine(provisioner, factory, package);
            using var firstFrame = CreateFrame();
            using var secondFrame = CreateFrame();
            using var cancellation = new CancellationTokenSource();
            var first = engine.RecognizeAsync(
                firstFrame,
                new OcrRecognitionOptions(OcrLanguage.English),
                cancellation.Token);
            Assert(started.Wait(TimeSpan.FromSeconds(5)), "Fake inference did not start.");
            var second = engine.RecognizeAsync(
                secondFrame,
                new OcrRecognitionOptions(OcrLanguage.Japanese),
                CancellationToken.None);
            await Task.Delay(25).ConfigureAwait(false);
            Assert(runtime.MaximumConcurrency == 1 && !second.IsCompleted,
                "A non-thread-safe runtime must never receive concurrent inference.");
            cancellation.Cancel();
            release.Set();
            await AssertThrowsAsync<OperationCanceledException>(() => first).ConfigureAwait(false);
            var secondResult = await second.ConfigureAwait(false);
            Assert(secondResult.IsSuccess && runtime.MaximumConcurrency == 1,
                "Queued inference must continue after the prior caller cancels after native work.");
            Assert(runtime.FirstToken == cancellation.Token,
                "The caller's cancellation token must reach the runtime overload unchanged.");

            using var cancelledBefore = new CancellationTokenSource();
            cancelledBefore.Cancel();
            using var thirdFrame = CreateFrame();
            await AssertThrowsAsync<OperationCanceledException>(() => engine.RecognizeAsync(
                thirdFrame,
                new OcrRecognitionOptions(OcrLanguage.English),
                cancelledBefore.Token)).ConfigureAwait(false);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestThousandFrameResourceSmokeAsync()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var package = await ProvisionTestModelAsync(root).ConfigureAwait(false);
            var factory = new FakeRuntimeFactory(_ => new[] { new PaddleRuntimeSpan("ok", 1, 0, 0, 5, 5) });
            var provisioner = new OcrModelProvisioner(new HttpClient(new ModelDownloadHandler(new Dictionary<Uri, byte[]>())), root);
            for (var cycle = 0; cycle < 20; cycle++)
            {
                await using var engine = new PaddleOcrEngine(provisioner, factory, package);
                for (var frameIndex = 0; frameIndex < 50; frameIndex++)
                {
                    var lease = new CountingLease();
                    using (var frame = CreateFrame(lease))
                    {
                        var result = await engine.RecognizeAsync(
                            frame,
                            new OcrRecognitionOptions(OcrLanguage.SimplifiedChinese),
                            CancellationToken.None).ConfigureAwait(false);
                        Assert(result.IsSuccess && result.Value!.Spans.Count == 1,
                            "1,000-frame smoke inference must keep returning mapped results.");
                    }

                    Assert(lease.DisposeCount == 1, "Every preprocessed image lease must be released exactly once.");
                }
            }

            Assert(factory.Created == 20 && factory.Disposed == 20,
                "Repeated initialization/inference/disposal must release every runtime instance.");
            Console.WriteLine("PaddleOCR resource smoke: 1,000 frames, 20 runtimes created/disposed.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    public static Task RunOptInNativeIntegrationAsync()
    {
        var enabled = string.Equals(
            Environment.GetEnvironmentVariable("GAMELINGOAI_RUN_NATIVE_OCR_TESTS"),
            "1",
            StringComparison.Ordinal);
        if (!enabled)
        {
            Console.WriteLine("Native PaddleOCR integration skipped; set GAMELINGOAI_RUN_NATIVE_OCR_TESTS=1 with a provisioned model and fixture to opt in.");
            return Task.CompletedTask;
        }

        var modelRoot = Environment.GetEnvironmentVariable("GAMELINGOAI_OCR_MODELS_ROOT");
        var fixture = Environment.GetEnvironmentVariable("GAMELINGOAI_OCR_NATIVE_FIXTURE");
        if (string.IsNullOrWhiteSpace(modelRoot) || string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture))
        {
            throw new InvalidOperationException(
                "Native OCR opt-in requires GAMELINGOAI_OCR_MODELS_ROOT and GAMELINGOAI_OCR_NATIVE_FIXTURE.");
        }

        return RunNativeFixtureAsync(modelRoot, fixture);
    }

    private static async Task RunNativeFixtureAsync(string modelRoot, string fixture)
    {
        var lines = await File.ReadAllLinesAsync(fixture).ConfigureAwait(false);
        var rows = lines.Where(line => line.Length > 0).ToArray();
        Assert(rows.Length > 0 && rows.All(row => row.Length == rows[0].Length),
            "Native opt-in fixture must be a rectangular ASCII luminance map.");
        var pixels = rows.SelectMany(row => row.Select(value => value == '#' ? byte.MaxValue : byte.MinValue)).ToArray();
        using var frame = CreateFrame(width: rows[0].Length, height: rows.Length, pixels: pixels);
        var provisioner = new OcrModelProvisioner(new HttpClient(), modelRoot);
        await using var engine = new PaddleOcrEngine(
            provisioner,
            new SdcbPaddleOcrRuntimeFactory(),
            PaddleOcrModelCatalog.PpOcrV5Mobile);
        var result = await engine.RecognizeAsync(
            frame,
            new OcrRecognitionOptions(OcrLanguage.English, 0.3),
            CancellationToken.None).ConfigureAwait(false);
        Assert(result.IsSuccess && result.Value!.Spans.Count > 0,
            "Native PaddleOCR fixture must produce at least one span.");
    }

    private static async Task<OcrModelPackage> ProvisionTestModelAsync(string root)
    {
        var detection = CreateModelTar("det");
        var recognition = CreateModelTar("rec");
        var package = CreatePackage("engine-model", detection, recognition);
        var handler = new ModelDownloadHandler(new Dictionary<Uri, byte[]>
        {
            [package.Artifacts[0].DownloadUri] = detection,
            [package.Artifacts[1].DownloadUri] = recognition
        });
        var provisioner = new OcrModelProvisioner(new HttpClient(handler), root);
        var result = await provisioner.ProvisionAsync(package, progress: null, CancellationToken.None).ConfigureAwait(false);
        Assert(result.State == OcrModelState.Ready, "Synthetic model must provision successfully.");
        return package;
    }

    private static OcrModelPackage CreatePackage(
        string id,
        byte[] detection,
        byte[] recognition,
        bool corruptFirstHash = false)
    {
        static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
        var firstHash = corruptFirstHash ? new string('0', 64) : Hash(detection);
        return new OcrModelPackage(
            id,
            "Synthetic test model",
            Enum.GetValues<OcrLanguage>().ToHashSet(),
            new[]
            {
                new OcrModelArtifact(
                    "det.tar",
                    new Uri($"https://models.invalid/{id}/det.tar"),
                    detection.Length,
                    firstHash,
                    OcrModelArchiveKind.Tar),
                new OcrModelArtifact(
                    "rec.tar",
                    new Uri($"https://models.invalid/{id}/rec.tar"),
                    recognition.Length,
                    Hash(recognition),
                    OcrModelArchiveKind.Tar)
            },
            "Apache-2.0",
            new Uri("https://licenses.invalid/apache-2.0"),
            new Uri("https://source.invalid/paddleocr"),
            "det",
            "rec");
    }

    private static byte[] CreateModelTar(string directory)
    {
        using var output = new MemoryStream();
        using (var writer = new TarWriter(output, leaveOpen: true))
        {
            foreach (var name in new[] { "inference.json", "inference.pdiparams", "inference.yml" })
            {
                using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"{directory}:{name}"));
                var entry = new PaxTarEntry(TarEntryType.RegularFile, $"{directory}/{name}") { DataStream = data };
                writer.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private static PreprocessedFrame CreateFrame(CountingLease? lease = null) =>
        CreateFrame(100, 50, new byte[100 * 50], lease);

    private static PreprocessedFrame CreateFrame(int width, int height, byte[] pixels, CountingLease? lease = null)
    {
        var actualLease = lease ?? new CountingLease();
        var image = new PreprocessedRegionImage(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            new NormalizedRegion(0.1, 0.2, 0.8, 0.6),
            width,
            height,
            OcrPixelFormat.Gray8,
            pixels,
            actualLease);
        return new PreprocessedFrame(
            new PipelineMetadata(SessionId.New(), new SequenceNumber(1), DateTimeOffset.UnixEpoch, CorrelationId.New()),
            new[] { image });
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameLingoAI-OcrTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class InlineProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private sealed class CountingLease : IFrameLease
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class ModelDownloadHandler(IReadOnlyDictionary<Uri, byte[]> content) : HttpMessageHandler
    {
        public bool SawRangeRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri is null || !content.TryGetValue(request.RequestUri, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var offset = request.Headers.Range?.Ranges.Single().From ?? 0;
            SawRangeRequest |= offset > 0;
            var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes[(int)offset..])
            };
            if (offset > 0)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
            }

            return Task.FromResult(response);
        }
    }

    private sealed class FakeRuntimeFactory(Func<PreprocessedRegionImage, IReadOnlyList<PaddleRuntimeSpan>> recognize)
        : IPaddleOcrRuntimeFactory
    {
        private int _created;
        private int _disposed;

        public int Created => Volatile.Read(ref _created);

        public int Disposed => Volatile.Read(ref _disposed);

        public IPaddleOcrRuntime Create(OcrModelPackage package, string installationPath)
        {
            Interlocked.Increment(ref _created);
            return new DelegateRuntime(recognize, () => Interlocked.Increment(ref _disposed));
        }
    }

    private sealed class DelegateRuntime(
        Func<PreprocessedRegionImage, IReadOnlyList<PaddleRuntimeSpan>> recognize,
        Action dispose) : IPaddleOcrRuntime
    {
        public IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image) => recognize(image);

        public void Dispose() => dispose();
    }

    private sealed class SingleRuntimeFactory(IPaddleOcrRuntime runtime) : IPaddleOcrRuntimeFactory
    {
        public IPaddleOcrRuntime Create(OcrModelPackage package, string installationPath) => runtime;
    }

    private sealed class BlockingRuntime(ManualResetEventSlim started, ManualResetEventSlim release) : IPaddleOcrRuntime
    {
        private int _calls;
        public CancellationToken FirstToken { get; private set; }

        public IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            { FirstToken = cancellationToken; }
            cancellationToken.ThrowIfCancellationRequested();
            var result = Recognize(image);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        private int _concurrency;
        private int _maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            InterlockedExtensions.Max(ref _maximumConcurrency, concurrency);
            started.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            Interlocked.Decrement(ref _concurrency);
            return new[] { new PaddleRuntimeSpan("ok", 1, 0, 0, 5, 5) };
        }

        public void Dispose()
        {
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}

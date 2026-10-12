using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Ocr;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class ModelPackagingTests
{
    private static readonly string[] ModelFiles = ["inference.json", "inference.yml", "inference.pdiparams"];

    [Fact]
    public async Task CancelledCompleteArchivesRecoverOfflineAndManifestOmissionsFail()
    {
        var directory = Directory.CreateTempSubdirectory("gamelingo-model-");
        try
        {
            var detection = Archive("det");
            var recognition = Archive("rec");
            var package = Package(detection, recognition);
            using var cancellation = new CancellationTokenSource();
            using var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(detection) });
            using var client = new HttpClient(handler);
            var provisioner = new OcrModelProvisioner(client, directory.FullName);
            Assert.Equal(OcrModelState.Missing, (await provisioner.GetStatusAsync(package, CancellationToken.None)).State);
            Assert.Equal(0, handler.Calls);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provisioner.ProvisionAsync(package,
                new InlineProgress(value => { if (value > 0) { cancellation.Cancel(); } }), cancellation.Token));
            var partialRoot = Path.Combine(directory.FullName, ".downloads", package.Id);
            Assert.Equal(detection, await File.ReadAllBytesAsync(Path.Combine(partialRoot, "det.tar.partial")));
            Assert.False(Directory.Exists(provisioner.GetInstallationPath(package)));
            // Emulate a previous fully downloaded second archive, then disallow all networking.
            await File.WriteAllBytesAsync(Path.Combine(partialRoot, "rec.tar.partial"), recognition);
            using var offline = new Handler(_ => throw new HttpRequestException("Offline fixture"));
            using var offlineClient = new HttpClient(offline);
            var recovering = new OcrModelProvisioner(offlineClient, directory.FullName);
            var ready = await recovering.ProvisionAsync(package, null, CancellationToken.None);
            Assert.Equal(OcrModelState.Ready, ready.State);
            Assert.Equal(0, offline.Calls);
            var manifestPath = Path.Combine(ready.InstallationPath!, ".integrity.json");
            foreach (var files in new object?[] { Array.Empty<object>(), null, new object?[] { null } })
            {
                await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(new { PackageId = package.Id, Files = files }));
                Assert.Equal(OcrModelState.Corrupt, (await recovering.GetStatusAsync(package, CancellationToken.None)).State);
            }
            Assert.Equal(0, offline.Calls);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task IncorrectResumeRangePreservesPartialAndPreCancellationDoesNotCreateRoot()
    {
        var directory = Directory.CreateTempSubdirectory("gamelingo-model-");
        try
        {
            var detection = Archive("det");
            var package = Package(detection, Archive("rec"));
            using var handler = new Handler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(detection) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, detection.Length - 1, detection.Length);
                return response;
            });
            using var client = new HttpClient(handler);
            var untouched = Path.Combine(directory.FullName, "untouched");
            var provisioner = new OcrModelProvisioner(client, untouched);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provisioner.ProvisionAsync(package, null, cancelled.Token));
            Assert.False(Directory.Exists(untouched));
            var partialRoot = Path.Combine(untouched, ".downloads", package.Id);
            Directory.CreateDirectory(partialRoot);
            var partial = Path.Combine(partialRoot, "det.tar.partial");
            await File.WriteAllBytesAsync(partial, detection[..17]);
            await Assert.ThrowsAsync<InvalidDataException>(() => provisioner.ProvisionAsync(package, null, CancellationToken.None));
            Assert.Equal(detection[..17], await File.ReadAllBytesAsync(partial));
            Assert.False(Directory.Exists(provisioner.GetInstallationPath(package)));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task PackagedModelMetadataMatchesCatalogAndMsixPreservesUserData()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "packaging", "model-manifest.json")));
        var root = document.RootElement;
        var catalog = PaddleOcrModelCatalog.PpOcrV5Mobile;
        Assert.Equal(catalog.Id, root.GetProperty("id").GetString());
        Assert.False(root.GetProperty("includedInPackage").GetBoolean());
        Assert.True(root.GetProperty("downloadRequiresExplicitUserAction").GetBoolean());
        Assert.Equal(catalog.LicenseName, root.GetProperty("license").GetString());
        Assert.Equal(catalog.SourceUri.AbsoluteUri, root.GetProperty("source").GetString());
        Assert.Equal(catalog.LicenseUri.AbsoluteUri, root.GetProperty("licenseUrl").GetString());
        Assert.Equal(catalog.Languages.Select(value => value.ToString()).Order(), root.GetProperty("languages").EnumerateArray().Select(value => value.GetString()!).Order());
        var artifacts = root.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal(catalog.Artifacts.Count, artifacts.Length);
        for (var index = 0; index < artifacts.Length; index++)
        {
            Assert.Equal(catalog.Artifacts[index].FileName, artifacts[index].GetProperty("fileName").GetString());
            Assert.Equal(catalog.Artifacts[index].SizeBytes, artifacts[index].GetProperty("sizeBytes").GetInt64());
            Assert.Equal(catalog.Artifacts[index].Sha256, artifacts[index].GetProperty("sha256").GetString());
            Assert.Equal(catalog.Artifacts[index].DownloadUri.AbsoluteUri, artifacts[index].GetProperty("source").GetString());
        }
        var manifest = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "packaging", "AppxManifest.xml"));
        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";
        Assert.Equal("x64", manifest.Root!.Element(foundation + "Identity")!.Attribute("ProcessorArchitecture")!.Value);
        Assert.Equal("disabled", manifest.Root.Element(foundation + "Properties")!.Element(desktop + "FileSystemWriteVirtualization")!.Value);
        Assert.DoesNotContain(manifest.Descendants(), node => node.Name.LocalName == "DeviceCapability");
    }

    private static byte[] Archive(string folder)
    {
        using var stream = new MemoryStream();
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var name in ModelFiles)
            {
                using var contents = new MemoryStream("synthetic-model"u8.ToArray());
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, folder + "/" + name) { DataStream = contents });
            }
        }
        return stream.ToArray();
    }

    private static OcrModelPackage Package(byte[] detection, byte[] recognition) => new("packaging-fixture", "Fixture",
        new HashSet<OcrLanguage> { OcrLanguage.English },
        [new("det.tar", new Uri("https://example.test/det"), detection.Length, Convert.ToHexString(SHA256.HashData(detection)), OcrModelArchiveKind.Tar),
         new("rec.tar", new Uri("https://example.test/rec"), recognition.Length, Convert.ToHexString(SHA256.HashData(recognition)), OcrModelArchiveKind.Tar)],
        "CC0-1.0", new Uri("https://example.test/license"), new Uri("https://example.test/source"), "det", "rec");

    private sealed class InlineProgress(Action<double> callback) : IProgress<double>
    { public void Report(double value) => callback(value); }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(response(request));
        }
    }
}

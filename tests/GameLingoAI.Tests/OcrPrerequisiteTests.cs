using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Infrastructure.Ocr;
using Xunit;

namespace GameLingoAI.Tests;

[Trait("Suite", "Default")]
public sealed class OcrPrerequisiteTests
{
    [Fact]
    public void CpuAndNativeRequirementsAreExplicitAndPresenceOnly()
    {
        Assert.Empty(OcrRuntimePrerequisites.Evaluate("app", "system", true, true, _ => true));
        var missing = OcrRuntimePrerequisites.Evaluate("app", "system", false, false, _ => false);
        Assert.Equal(4, missing.Count);
        Assert.Contains(missing, value => value.Contains("Visual C++", StringComparison.Ordinal));
        Assert.Contains(missing, value => value.Contains("Media Foundation", StringComparison.Ordinal));
        var onlyMedia = OcrRuntimePrerequisites.Evaluate("app", "system", true, true,
            path => path.StartsWith("app" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Single(onlyMedia);
        Assert.Contains("Media Foundation", onlyMedia[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingModelsAndRuntimeAreVisibleWithoutDownloadOrCrash()
    {
        var directory = Directory.CreateTempSubdirectory("gamelingo-prerequisite-");
        try
        {
            using var handler = new NoNetwork();
            using var client = new HttpClient(handler);
            var reporter = new ErrorReporter();
            var errors = 0;
            reporter.ErrorReported += (_, _) => errors++;
            using var viewModel = new OcrModelViewModel(new OcrModelProvisioner(client, directory.FullName),
                PaddleOcrModelCatalog.PpOcrV5Mobile, reporter,
                () => OcrRuntimePrerequisites.Evaluate("app", "system", true, true, _ => false));
            Assert.Contains("never downloaded automatically", viewModel.Status, StringComparison.Ordinal);
            Assert.Contains("Visual C++", viewModel.Status, StringComparison.Ordinal);
            await ((AsyncRelayCommand)viewModel.CheckStatusCommand).ExecuteAsync();
            Assert.Contains("has not been downloaded", viewModel.Status, StringComparison.Ordinal);
            Assert.Contains("Media Foundation", viewModel.Status, StringComparison.Ordinal);
            Assert.Equal(0, viewModel.DownloadProgress);
            Assert.Equal(0, handler.Calls);
            Assert.Equal(0, errors);
            Assert.Empty(directory.GetFileSystemInfos());
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Status checking must not download anything.");
        }
    }
}

using System.Windows.Input;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Infrastructure.Ocr;

namespace GameLingoAI.App.ViewModels;

public sealed class OcrModelViewModel : ObservableObject, IDisposable
{
    private readonly OcrModelProvisioner _provisioner;
    private readonly OcrModelPackage _package;
    private readonly Func<IReadOnlyList<string>> _checkPrerequisites;
    private string _status = "Select Check status; models are never downloaded automatically.";
    private double _downloadProgress;

    public OcrModelViewModel(
        OcrModelProvisioner provisioner,
        OcrModelPackage package,
        IErrorReporter errorReporter,
        Func<IReadOnlyList<string>>? checkPrerequisites = null)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _package = package ?? throw new ArgumentNullException(nameof(package));
        ArgumentNullException.ThrowIfNull(errorReporter);
        _checkPrerequisites = checkPrerequisites ?? OcrRuntimePrerequisites.Check;
        _status = WithPrerequisites(_status);
        CheckStatusCommand = new AsyncRelayCommand(CheckStatusAsync, errorReporter);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, errorReporter);
    }

    public string DisplayName => _package.DisplayName;

    public string Languages => string.Join(", ", _package.Languages.Order());

    public string DownloadSize => $"{_package.DownloadSizeBytes / (1024d * 1024d):F1} MiB";

    public string License => _package.LicenseName;

    public string LicenseUrl => _package.LicenseUri.AbsoluteUri;

    public string SourceUrl => _package.SourceUri.AbsoluteUri;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    public ICommand CheckStatusCommand { get; }

    public ICommand DownloadCommand { get; }

    public void Dispose()
    {
        ((AsyncRelayCommand)CheckStatusCommand).Dispose();
        ((AsyncRelayCommand)DownloadCommand).Dispose();
    }

    private async Task CheckStatusAsync(CancellationToken cancellationToken)
    {
        var status = await _provisioner.GetStatusAsync(_package, cancellationToken).ConfigureAwait(true);
        Status = WithPrerequisites(status.Message);
        DownloadProgress = status.State == OcrModelState.Ready ? 1 : 0;
    }

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        Status = "Downloading and verifying the selected local model…";
        DownloadProgress = 0;
        var progress = new Progress<double>(value => DownloadProgress = value);
        var status = await _provisioner.ProvisionAsync(_package, progress, cancellationToken).ConfigureAwait(true);
        Status = WithPrerequisites(status.Message);
    }

    private string WithPrerequisites(string modelStatus)
    {
        var missing = _checkPrerequisites();
        return missing.Count == 0 ? modelStatus : modelStatus + " OCR prerequisites: " + string.Join(" ", missing);
    }
}

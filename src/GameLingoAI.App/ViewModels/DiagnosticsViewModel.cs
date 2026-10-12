using System.Text.Json;
using System.Text.Json.Serialization;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;

namespace GameLingoAI.App.ViewModels;

public sealed class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly Func<CancellationToken, Task<DiagnosticMetrics>> _sample;
    private readonly Func<IReadOnlyList<string>> _recent;
    private bool _closed;
    private Task _refresh = Task.CompletedTask;
    private string _metricsJson = string.Empty;
    private string _eventsJson = string.Empty;
    private SupportBundlePreview? _preview;
    private Task _bundleWork = Task.CompletedTask;
    private string _previewText = string.Empty;
    private string _exportPath = string.Empty;
    private bool _consent;
    private bool _exportSucceeded;

    public DiagnosticsViewModel(Func<CancellationToken, Task<DiagnosticMetrics>> sample,
        Func<IReadOnlyList<string>> recent, IErrorReporter errors)
    {
        _sample = sample;
        _recent = recent;
        RefreshCommand = new AsyncRelayCommand(token => _refresh = RefreshAsync(token), errors, () => Idle);
        PreviewCommand = new AsyncRelayCommand(token => _bundleWork = PreviewAsync(token), errors, () => Idle);
        ExportCommand = new AsyncRelayCommand(token => _bundleWork = ExportAsync(token), errors,
            () => Idle && _preview is not null && Consent && !string.IsNullOrWhiteSpace(ExportPath));
        CancelCommand = new RelayCommand(() => { RefreshCommand.Cancel(); PreviewCommand.Cancel(); ExportCommand.Cancel(); },
            () => !_closed && !Idle);
        foreach (var command in new[] { RefreshCommand, PreviewCommand, ExportCommand })
        { command.PropertyChanged += (_, _) => NotifyCommands(); }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    private bool Idle => !_closed && !RefreshCommand.IsExecuting && !PreviewCommand.IsExecuting && !ExportCommand.IsExecuting;
    public string PreviewText { get => _previewText; private set => SetProperty(ref _previewText, value); }
    public bool ExportSucceeded { get => _exportSucceeded; private set => SetProperty(ref _exportSucceeded, value); }
    public bool Consent
    {
        get => _consent;
        set { if (SetProperty(ref _consent, value)) { ExportCommand.NotifyCanExecuteChanged(); } }
    }
    public string ExportPath
    {
        get => _exportPath;
        set { if (SetProperty(ref _exportPath, value)) { Consent = false; ExportCommand.NotifyCanExecuteChanged(); } }
    }
    public string MetricsJson { get => _metricsJson; private set => SetProperty(ref _metricsJson, value); }
    public string EventsJson { get => _eventsJson; private set => SetProperty(ref _eventsJson, value); }

    private async Task RefreshAsync(CancellationToken token)
    {
        InvalidatePreview();
        var metrics = await _sample(token);
        token.ThrowIfCancellationRequested();
        if (_closed)
        { return; }
        MetricsJson = JsonSerializer.Serialize(metrics, JsonOptions);
        EventsJson = string.Join(Environment.NewLine, _recent());
    }

    private async Task PreviewAsync(CancellationToken token)
    {
        InvalidatePreview();
        var metrics = await _sample(token);
        token.ThrowIfCancellationRequested();
        if (_closed)
        { return; }
        _preview = SupportBundlePreview.Create(metrics, _recent(), typeof(DiagnosticsViewModel).Assembly.GetName().Version ?? new Version(0, 0),
            AppSettings.CurrentSchemaVersion);
        PreviewText = string.Join(Environment.NewLine + Environment.NewLine, _preview.Entries.Select(entry => entry.Key + Environment.NewLine + entry.Value));
        NotifyCommands();
    }

    private async Task ExportAsync(CancellationToken token)
    {
        ExportSucceeded = false;
        var preview = _preview ?? throw new InvalidOperationException("Prepare a preview first.");
        var path = ExportPath;
        if (!System.IO.Path.IsPathFullyQualified(path) || !string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Select an absolute local .zip destination."); }
        var consent = Consent;
        Consent = false;
        await preview.ExportAsync(path, consent, null, token);
        if (!_closed)
        { ExportSucceeded = true; }
    }

    private void InvalidatePreview()
    {
        _preview = null;
        ExportSucceeded = false;
        PreviewText = string.Empty;
        Consent = false;
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        PreviewCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    public async Task ShutdownAsync()
    {
        Dispose();
        try
        { await Task.WhenAll(_refresh, _bundleWork); }
        catch (Exception) { /* RefreshCommand owns safe error reporting; shutdown only drains. */ }
    }

    public void Dispose()
    {
        _closed = true;
        RefreshCommand.Dispose();
        PreviewCommand.Dispose();
        ExportCommand.Dispose();
        NotifyCommands();
    }
}

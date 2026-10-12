using System.Collections.ObjectModel;
using System.Windows.Threading;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Overlay;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.App.ViewModels;

public sealed class SessionViewModel : ObservableObject, IDisposable, IStoppedSessionMaintenance
{
    private readonly SessionController _controller;
    private readonly HistoryRecordingPresenter _history;
    private readonly IConfigurationStore _configuration;
    private readonly IProfileRepository? _profiles;
    private readonly IHotkeyService? _hotkeys;
    private GameProfile? _selectedSavedProfile;
    private readonly WindowSelectionViewModel _selection;
    private readonly RegionEditorViewModel _regions;
    private readonly GlossaryViewModel _glossary;
    private readonly ILocalRegionPreview _preview;
    private readonly SessionDisplay _display;
    private readonly SessionStatusViewModel _status;
    private readonly OverlayController _overlay;
    private readonly DispatcherTimer _timer;
    private readonly ProfileId _profileId = ProfileId.New();
    private ProviderOptions? _selectedProvider;
    private string _text = string.Empty;
    private string _message = "Load configured providers, select one, and confirm a capture window before starting.";
    private string _metrics = string.Empty;
    private bool _closing;
    private bool _disposed;
    private bool _stopping;
    private Task? _stopTask;
    private ConfirmedCaptureWindow? _activeTarget;
    private string? _activeProfileName;
    private string? _activeProviderId;
    private string _sessionDetails = "No active session.";
    private string _latency = "Latest translation latency: unavailable.";
    private readonly SemaphoreSlim _startMaintenanceGate = new(1, 1);

    public SessionViewModel(ISessionResourcesFactory factory, IConfigurationStore configuration,
        WindowSelectionViewModel selection, RegionEditorViewModel regions, GlossaryViewModel glossary,
        ILocalRegionPreview preview, SessionDisplay display, SessionStatusViewModel status, IErrorReporter errors,
        OverlayController overlay, OverlaySettingsViewModel overlaySettings, ITranslationHistoryRepository history,
        IProfileRepository? profiles = null, IHotkeyService? hotkeys = null, IDiagnosticEvents? diagnostics = null)
    {
        _history = new HistoryRecordingPresenter(display, history);
        _controller = new SessionController(factory, _history, diagnostics: diagnostics);
        _configuration = configuration;
        _profiles = profiles;
        _hotkeys = hotkeys;
        _selection = selection;
        _regions = regions;
        _glossary = glossary;
        _preview = preview;
        _display = display;
        _status = status;
        _overlay = overlay;
        OverlaySettings = overlaySettings;
        _overlay.LayoutChanged += OnOverlayLayoutChanged;
        LoadProvidersCommand = new AsyncRelayCommand(LoadProvidersAsync, errors, () => CanChangeSession && IsIdle);
        StartCommand = new AsyncRelayCommand(StartAsync, errors, () => CanChangeSession && IsIdle && !LoadProvidersCommand.IsExecuting &&
            (SelectedProvider is not null || SelectedSavedProfile is not null));
        UseAdHocCommand = new AsyncRelayCommand(_ => { SelectedSavedProfile = null; return Task.CompletedTask; }, errors,
            () => CanChangeSession && IsIdle);
        SuggestWindowsCommand = new AsyncRelayCommand(SuggestWindowsAsync, errors,
            () => CanChangeSession && IsIdle && SelectedSavedProfile is not null);
        PauseCommand = new AsyncRelayCommand(token => _controller.PauseAsync(token), errors,
            () => CanChangeSession && _controller.State == SessionState.Running);
        ResumeCommand = new AsyncRelayCommand(ResumeAsync, errors,
            () => CanChangeSession && _controller.State == SessionState.Paused);
        StopCommand = new AsyncRelayCommand(StopAsync, errors, () => !_closing && !_stopping &&
            (_controller.State != SessionState.Stopped || StartCommand.IsExecuting || _overlay.IsEditing));
        EmergencyStopCommand = new AsyncRelayCommand(StopAsync, errors, () => !_closing);
        ToggleOverlayCommand = new AsyncRelayCommand(_ =>
        {
            _overlay.SetSuppressed(!_overlay.IsSuppressed);
            Refresh();
            return Task.CompletedTask;
        }, errors, () => CanChangeSession && (_activeTarget is not null || _overlay.IsEditing));
        EditOverlayCommand = new AsyncRelayCommand(EditOverlayAsync, errors,
            () => CanChangeSession && _controller.State is SessionState.Stopped or SessionState.Running or SessionState.Paused);
        if (_hotkeys is not null)
        { _hotkeys.Triggered += OnHotkeyTriggered; }
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => Refresh(), Dispatcher.CurrentDispatcher);
    }

    private bool IsIdle => !_stopping && _controller.State is SessionState.Stopped or SessionState.Faulted;
    private bool CanChangeSession => !_closing && !_stopping && !StartCommand.IsExecuting &&
        !PauseCommand.IsExecuting && !ResumeCommand.IsExecuting && !EditOverlayCommand.IsExecuting;
    public ObservableCollection<ProviderOptions> Providers { get; } = new();
    public ObservableCollection<GameProfile> SavedProfiles { get; } = new();
    public ObservableCollection<CaptureWindow> SuggestedWindows { get; } = new();
    public string ProfileIdentity => (SelectedSavedProfile?.Id ?? _profileId).Value.ToString();
    public GameProfile? SelectedSavedProfile
    {
        get => _selectedSavedProfile;
        set
        {
            if (!CanChangeSession || !IsIdle || !SetProperty(ref _selectedSavedProfile, value))
            { return; }
            // A different profile must not inherit capture authorization from the previous choice.
            _selection.SelectedWindow = null;
            SuggestedWindows.Clear();
            if (value is not null)
            { OverlaySettings.Replace(value.Settings.Overlay); }
            OnPropertyChanged(nameof(ProfileIdentity));
            StartCommand.NotifyCanExecuteChanged();
            Message = "Profile choice changed. Select and explicitly confirm the capture window again; no capture has started.";
        }
    }
    public ProviderOptions? SelectedProvider
    {
        get => _selectedProvider;
        set { if (SetProperty(ref _selectedProvider, value)) { StartCommand.NotifyCanExecuteChanged(); } }
    }
    public AsyncRelayCommand LoadProvidersCommand { get; }
    public AsyncRelayCommand UseAdHocCommand { get; }
    public AsyncRelayCommand SuggestWindowsCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand PauseCommand { get; }
    public AsyncRelayCommand ResumeCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public AsyncRelayCommand EmergencyStopCommand { get; }
    public AsyncRelayCommand ToggleOverlayCommand { get; }
    public string OverlayVisibilityLabel => _overlay.IsSuppressed ? "Show subtitles" : "Hide subtitles";
    public AsyncRelayCommand EditOverlayCommand { get; }
    public OverlaySettingsViewModel OverlaySettings { get; }
    public string Text { get => _text; private set => SetProperty(ref _text, value); }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public string Metrics { get => _metrics; private set => SetProperty(ref _metrics, value); }
    public SessionMetrics GetDiagnosticMetrics() => _controller.Metrics;
    public string SessionDetails { get => _sessionDetails; private set => SetProperty(ref _sessionDetails, value); }
    public string Latency { get => _latency; private set => SetProperty(ref _latency, value); }

    private async Task LoadProvidersAsync(CancellationToken token)
    {
        var loaded = await _configuration.LoadAsync(token);
        // A read may finish just before cancellation while its UI continuation arrives after shutdown.
        token.ThrowIfCancellationRequested();
        if (!loaded.IsSuccess)
        { Message = "Correct the configuration before loading providers."; return; }
        SelectedProvider = null;
        Providers.Clear();
        foreach (var provider in loaded.Value!.Providers)
        { Providers.Add(provider); }
        if (_profiles is not null)
        {
            try
            {
                var profiles = await _profiles.ListAsync(token);
                token.ThrowIfCancellationRequested();
                SelectedSavedProfile = null;
                SavedProfiles.Clear();
                foreach (var profile in profiles)
                { SavedProfiles.Add(profile); }
            }
            catch (DatabaseStorageException error) { Message = error.Message; return; }
        }
        Message = "Choose the provider explicitly. Remote text processing requires consent in settings.";
    }

    private async Task StartAsync(CancellationToken token)
    {
        await _startMaintenanceGate.WaitAsync(token);
        try
        {
            if (_closing || !IsIdle)
            { return; }
            await StartCoreAsync(token);
        }
        finally { _startMaintenanceGate.Release(); }
    }

    private async Task SuggestWindowsAsync(CancellationToken token)
    {
        var profile = SelectedSavedProfile;
        if (profile is null)
        { return; }
        var windows = _selection.Windows.Take(257).ToArray();
        var suggestions = await Task.Run(() => ProfileWindowHints.Find(profile, windows, token), token);
        if (_closing || SelectedSavedProfile?.Id != profile.Id)
        { return; }
        SuggestedWindows.Clear();
        foreach (var window in suggestions.Windows)
        { SuggestedWindows.Add(window); }
        Message = suggestions.LimitReached
            ? "Window hint limit reached. Review suggestions or select manually on Window/Region; confirmation is still required."
            : "Suggestions only. Select and confirm the intended window on Window/Region; capture has not started.";
    }

    public async Task RunAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await _startMaintenanceGate.WaitAsync(cancellationToken);
        try
        {
            if (_closing || _stopping || _controller.State != SessionState.Stopped)
            { throw new InvalidOperationException("Stop the session before database maintenance."); }
            await _history.StopAsync();
            await action(cancellationToken);
        }
        finally { _startMaintenanceGate.Release(); }
    }

    private async Task StartCoreAsync(CancellationToken token)
    {
        var target = _selection.ConfirmedSelection;
        var savedChoice = SelectedSavedProfile;
        if (target is null || savedChoice is null && (!_regions.CanSave || SelectedProvider is null))
        {
            Message = "Confirm a capture window and valid regions, then select a configured provider.";
            return;
        }

        var selected = SelectedProvider;
        var regionSnapshot = _regions.Regions.Select(region => new CaptureRegion(region.Id, region.Name, region.Bounds, region.SortOrder)).ToArray();
        var glossary = _glossary.Snapshot;
        var options = new TranslationPromptOptions(_glossary.PreserveLineBreaks, _glossary.PreserveTags);
        var loaded = await _configuration.LoadAsync(token);
        token.ThrowIfCancellationRequested();
        if (!loaded.IsSuccess)
        { Message = "Correct the configuration before starting."; return; }
        var settings = loaded.Value!;
        var profile = new GameProfile(_profileId, "Current session", settings.Languages.Source, settings.Languages.Target,
            settings.Capture.FramesPerSecond, regionSnapshot, false, settings.Ocr.Preprocessing);
        if (savedChoice is not null)
        {
            try
            {
                var current = _profiles is null ? null : await _profiles.GetAsync(savedChoice.Id, token);
                token.ThrowIfCancellationRequested();
                if (current is null)
                { Message = "Saved profile no longer exists. Reload profiles and select again."; return; }
                var resolved = ProfileSessionSettings.Resolve(current, settings);
                profile = resolved.Profile;
                settings = resolved.Settings;
                selected = resolved.Provider;
            }
            catch (DatabaseStorageException error) { Message = error.Message; return; }
            catch (ArgumentException) { Message = "Correct the saved profile's model, local provider/reference and OCR settings before starting."; return; }
        }
        var request = new SessionStartRequest(target, profile, settings, selected!, glossary, options);
        _overlay.Unbind();
        _regions.OnNavigatedFrom();
        _preview.RequestStop();
        await _preview.WhenStopped.WaitAsync(token);
        await _history.BeginAsync(settings.Privacy.HistoryEnabled, savedChoice is null ? null : profile.Id, token);
        try
        { await _controller.StartAsync(request, token); }
        finally { if (_controller.State != SessionState.Running) { await _history.StopAsync(); } }
        token.ThrowIfCancellationRequested();
        if (!_closing && !_stopping && _controller.State == SessionState.Running && _controller.Metrics.SessionId is { } id)
        {
            _activeTarget = target;
            _activeProfileName = profile.Name;
            _activeProviderId = selected!.Id;
            _overlay.Bind(id, target, settings.Overlay);
        }
        Message = _controller.Failure?.Message ?? "Session running. Settings and regions are snapshotted until the next start.";
        Refresh();
    }

    private Task StopAsync(CancellationToken token)
    {
        // Once accepted, stopping must drain even when a command is cancelled by window shutdown.
        if (_stopTask is { IsCompleted: false })
        { return _stopTask; }
        _stopTask = StopCoreAsync();
        return _stopTask;
    }

    private async Task StopCoreAsync()
    {
        _stopping = true;
        StartCommand.Cancel();
        PauseCommand.Cancel();
        ResumeCommand.Cancel();
        EditOverlayCommand.Cancel();
        try
        {
            _overlay.Unbind();
            _activeTarget = null;
            _activeProfileName = null;
            _activeProviderId = null;
            var stopped = _controller.StopAsync(CancellationToken.None);
            await _display.HideAsync(CancellationToken.None);
            Refresh();
            await stopped;
            // Startup may still be draining a preview or a configuration read outside the controller.
            await _startMaintenanceGate.WaitAsync();
            try
            { await _history.StopAsync(); }
            finally { _startMaintenanceGate.Release(); }
            Message = "Session stopped. Subtitles hidden; saved settings are unchanged.";
        }
        finally { _stopping = false; Refresh(); }
    }

    private async Task ResumeAsync(CancellationToken token)
    {
        _overlay.EndEdit();
        await _controller.ResumeAsync(token);
        Refresh();
    }

    private async Task EditOverlayAsync(CancellationToken token)
    {
        var target = IsIdle ? _selection.ConfirmedSelection : _activeTarget;
        if (target is null)
        { Message = "Confirm a target window before editing the overlay."; return; }
        if (OverlayLayout.Validate(OverlaySettings.Options).Count > 0)
        {
            Message = "Correct the overlay settings before editing layout.";
            return;
        }
        await _controller.PauseAsync(token);
        token.ThrowIfCancellationRequested();
        if (_closing || _stopping)
        { return; }
        _overlay.BeginEdit(target, OverlaySettings.Options);
        Message = "Layout edit is interactive. Use Done to restore click-through; Resume explicitly restarts translation.";
        Refresh();
    }

    private void OnOverlayLayoutChanged(object? sender, OverlayOptions options) => OverlaySettings.Replace(options);

    private void OnHotkeyTriggered(object? sender, HotkeyAction action)
    {
        if (_closing || _disposed)
        { return; }
        var command = action switch
        {
            HotkeyAction.Start => StartCommand,
            HotkeyAction.PauseResume => _controller.State == SessionState.Paused ? ResumeCommand : PauseCommand,
            HotkeyAction.Stop => StopCommand,
            HotkeyAction.ToggleOverlay => ToggleOverlayCommand,
            HotkeyAction.EmergencyStop => EmergencyStopCommand,
            _ => null
        };
        if (command?.CanExecute(null) == true)
        { command.Execute(null); }
    }

    private void Refresh()
    {
        _status.State = _controller.State;
        Text = _display.Current?.TranslatedText ?? string.Empty;
        var target = _activeTarget ?? _selection.ConfirmedSelection;
        SessionDetails = $"Window: {target?.Window.Title ?? "not confirmed"}; profile: {_activeProfileName ?? SelectedSavedProfile?.Name ?? "Ad-hoc"}; provider: {_activeProviderId ?? SelectedSavedProfile?.Settings.ProviderId ?? SelectedProvider?.Id ?? "not selected"}.";
        Latency = _display.Current is { } result
            ? $"Latest translation latency: {result.Duration.TotalMilliseconds:F0} ms{(result.FromCache ? " (cache)" : string.Empty)}."
            : "Latest translation latency: unavailable.";
        OnPropertyChanged(nameof(OverlayVisibilityLabel));
        if (_controller.State == SessionState.Faulted && _controller.Failure is { } failure)
        { Message = $"{failure.Message} [{failure.CorrelationId.Value}]"; }
        var metrics = _controller.Metrics;
        Metrics = $"Dropped frames: {metrics.DroppedFrames}; stale results: {metrics.StaleResults}; cache hits: {metrics.CacheHits}; errors: {metrics.Errors}; history dropped: {_history.Dropped}; history errors: {_history.Errors}";
        foreach (var command in Commands())
        { command.NotifyCanExecuteChanged(); }
    }

    public async Task ShutdownAsync()
    {
        _closing = true;
        if (_hotkeys is not null)
        { _hotkeys.Triggered -= OnHotkeyTriggered; }
        foreach (var command in Commands())
        { command.Cancel(); }
        _overlay.Unbind();
        if (_stopTask is not null)
        { await _stopTask; }
        await _controller.DisposeAsync();
        await _history.DisposeAsync();
        await _startMaintenanceGate.WaitAsync();
        _startMaintenanceGate.Release();
        _overlay.Unbind();
        _preview.RequestStop();
        await _preview.WhenStopped;
        _timer.Stop();
        _overlay.LayoutChanged -= OnOverlayLayoutChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        { return; }
        _disposed = true;
        if (_hotkeys is not null)
        { _hotkeys.Triggered -= OnHotkeyTriggered; }
        _startMaintenanceGate.Dispose();
        _timer.Stop();
        _overlay.LayoutChanged -= OnOverlayLayoutChanged;
        foreach (var command in Commands())
        { command.Dispose(); }
    }

    private AsyncRelayCommand[] Commands() => [LoadProvidersCommand, UseAdHocCommand, SuggestWindowsCommand, StartCommand, PauseCommand, ResumeCommand, StopCommand, EmergencyStopCommand, ToggleOverlayCommand, EditOverlayCommand];
}

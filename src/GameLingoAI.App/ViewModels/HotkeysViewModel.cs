using System.IO;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;

namespace GameLingoAI.App.ViewModels;

/// <summary>Saving definitions and registering keys are separate explicit actions.</summary>
public sealed class HotkeysViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationStore _configuration;
    private readonly IHotkeyService _service;
    private bool _enabled, _busy, _closing, _dirty, _discardConfirmed;
    private string _status = "Hotkeys are inactive. Load saved definitions or edit, then explicitly Apply for this run.";
    private TaskCompletionSource _drained = Completed();

    public HotkeysViewModel(IConfigurationStore configuration, IHotkeyService service, IErrorReporter errors)
    {
        _configuration = configuration;
        _service = service;
        Rows = Enum.GetValues<HotkeyAction>().Select(action =>
            new HotkeyRowViewModel(action, () => CanEdit, () => HasUnsavedChanges = true)).ToArray();
        LoadCommand = Command(LoadAsync, errors);
        SaveCommand = Command(SaveAsync, errors);
        ApplyCommand = new RelayCommand(() =>
        {
            var options = ReadDraft();
            if (options is not null)
            { RunNative(() => _service.Apply(options), errors); }
        }, () => CanEdit);
        DisableCommand = new RelayCommand(() => RunNative(_service.Clear, errors), () => !_closing);
    }

    public IReadOnlyList<HotkeyRowViewModel> Rows { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand DisableCommand { get; }
    public bool CanEdit => !_busy && !_closing;
    public bool Enabled
    {
        get => _enabled;
        set { if (CanEdit && SetProperty(ref _enabled, value)) { HasUnsavedChanges = true; } }
    }
    public bool HasUnsavedChanges { get => _dirty; private set => SetProperty(ref _dirty, value); }
    public bool DiscardConfirmed { get => _discardConfirmed; set => SetProperty(ref _discardConfirmed, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    private AsyncRelayCommand Command(Func<CancellationToken, Task> action, IErrorReporter errors) => new(async token =>
    {
        _busy = true;
        _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyCommands();
        try
        { await action(token); }
        catch (ConfigurationValidationException) { Status = "Correct configuration errors before saving. No shortcut registration changed."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Status = "Settings could not be accessed. Check file access and retry; your draft remains available."; }
        finally { _busy = false; _drained.TrySetResult(); NotifyCommands(); }
    }, errors, () => CanEdit);

    private async Task LoadAsync(CancellationToken token)
    {
        if (HasUnsavedChanges && !DiscardConfirmed)
        { Status = "Save your definitions or confirm discarding the draft before Load."; return; }
        DiscardConfirmed = false;
        var result = await _configuration.LoadAsync(token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
        { Status = "Correct the local settings file before loading shortcuts. Draft unchanged."; return; }
        var options = result.Value!.Hotkeys;
        if (HotkeyOptionsValidator.Validate(options).Count > 0)
        { Status = "Saved shortcut definitions are invalid. Draft unchanged."; return; }
        _enabled = options.Enabled;
        foreach (var row in Rows)
        { row.Load(options.Bindings.SingleOrDefault(binding => binding.Action == row.Action)); }
        HasUnsavedChanges = false;
        OnPropertyChanged(nameof(Enabled));
        Status = "Definitions loaded only. Choose Apply to register them for this run; capture will not start.";
    }

    private async Task SaveAsync(CancellationToken token)
    {
        var options = ReadDraft();
        if (options is null)
        { return; }
        var result = await _configuration.LoadAsync(token);
        token.ThrowIfCancellationRequested();
        if (!result.IsSuccess)
        { Status = "Correct local settings before saving shortcuts. Draft unchanged."; return; }
        await _configuration.SaveAsync(result.Value! with { Hotkeys = options }, token);
        token.ThrowIfCancellationRequested();
        HasUnsavedChanges = false;
        Status = "Definitions saved. Registration is unchanged; Apply explicitly for this run.";
    }

    public HotkeyOptions? ReadDraft()
    {
        var options = new HotkeyOptions
        {
            Enabled = Enabled,
            Bindings = Rows.Where(row => row.Assigned).Select(row => row.ToBinding()).ToArray()
        };
        var errors = HotkeyOptionsValidator.Validate(options);
        if (errors.Count == 0)
        { return options; }
        Status = string.Join(" ", errors.Select(error => error.Field + ": " + error.Message));
        return null;
    }

    public async Task ShutdownAsync()
    {
        _closing = true;
        LoadCommand.Cancel();
        SaveCommand.Cancel();
        NotifyCommands();
        var released = _service.Clear();
        await _drained.Task;
        if (!released.IsSuccess)
        { throw new InvalidOperationException(released.Message); }
    }

    public void Dispose()
    { _closing = true; LoadCommand.Dispose(); SaveCommand.Dispose(); }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        LoadCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ApplyCommand.NotifyCanExecuteChanged();
        DisableCommand.NotifyCanExecuteChanged();
    }

    private void RunNative(Func<HotkeyApplyResult> action, IErrorReporter errors)
    {
        try
        { Status = action().Message; }
        catch (Exception error)
        { Status = "Shortcut operation failed. Retry Disable or close the application before applying again."; errors.Report(error); }
    }

    private static TaskCompletionSource Completed()
    { var task = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); task.SetResult(); return task; }
}

public sealed class HotkeyRowViewModel(HotkeyAction action, Func<bool> canEdit, Action changed) : ObservableObject
{
    private bool _assigned, _control = true, _alt = true, _shift;
    private string _key = string.Empty;
    public HotkeyAction Action { get; } = action;
    public bool Assigned { get => _assigned; set { if (canEdit() && SetProperty(ref _assigned, value)) { changed(); } } }
    public bool Control { get => _control; set { if (canEdit() && SetProperty(ref _control, value)) { changed(); } } }
    public bool Alt { get => _alt; set { if (canEdit() && SetProperty(ref _alt, value)) { changed(); } } }
    public bool Shift { get => _shift; set { if (canEdit() && SetProperty(ref _shift, value)) { changed(); } } }
    public string Key { get => _key; set { if (canEdit() && SetProperty(ref _key, value)) { changed(); } } }
    public HotkeyBinding ToBinding() => new(Action,
        (Control ? HotkeyModifiers.Control : 0) | (Alt ? HotkeyModifiers.Alt : 0) | (Shift ? HotkeyModifiers.Shift : 0),
        HotkeyOptionsValidator.NormalizeKey(Key) ?? Key);

    internal void Load(HotkeyBinding? binding)
    {
        _assigned = binding is not null;
        _control = binding?.Modifiers.HasFlag(HotkeyModifiers.Control) ?? true;
        _alt = binding?.Modifiers.HasFlag(HotkeyModifiers.Alt) ?? true;
        _shift = binding?.Modifiers.HasFlag(HotkeyModifiers.Shift) ?? false;
        _key = binding?.Key ?? string.Empty;
        foreach (var name in new[] { nameof(Assigned), nameof(Control), nameof(Alt), nameof(Shift), nameof(Key) })
        { OnPropertyChanged(name); }
    }
}

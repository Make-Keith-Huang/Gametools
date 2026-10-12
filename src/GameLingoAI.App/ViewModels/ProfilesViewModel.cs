using System.Collections.ObjectModel;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.App.ViewModels;

/// <summary>Explicit profile operations. Selection/import never authorize or start capture.</summary>
public sealed class ProfilesViewModel : ObservableObject, IDisposable
{
    private readonly IProfileRepository _repository;
    private bool _busy, _closing, _dirty, _discardConfirmed, _deleteConfirmed, _readOnly;
    private string _draftJson = string.Empty, _transferJson = string.Empty, _secretReference = string.Empty, _status = string.Empty;
    private GameProfile? _selected;
    private ProfileId? _draftId;
    private ProfileFormViewModel? _form;
    private TaskCompletionSource _drained = Completed();

    public ProfilesViewModel(IProfileRepository repository, IErrorReporter errors)
    {
        _repository = repository;
        RefreshCommand = Command(RefreshAsync, errors);
        LoadCommand = Command(LoadAsync, errors);
        SaveCommand = Command(SaveAsync, errors);
        DeleteCommand = Command(DeleteAsync, errors);
        NewCommand = EditCommand(New);
        ImportCommand = EditCommand(Import);
        ExportCommand = EditCommand(Export);
        DuplicateCommand = EditCommand(Duplicate);
        LoadFormCommand = EditCommand(() => { Form = CreateForm(ReadDraft()); Status = "Form loaded from validated draft."; });
        AddRegionCommand = EditCommand(() => ChangeRegions(true));
        RemoveRegionCommand = EditCommand(() => ChangeRegions(false));
        PlainPresetCommand = EditCommand(() => ApplyPreset(PreprocessingOptions.PlainSubtitle));
        OutlinedPresetCommand = EditCommand(() => ApplyPreset(PreprocessingOptions.OutlinedSubtitle));
        NoPreprocessingCommand = EditCommand(() => ApplyPreset(PreprocessingOptions.NoOp));
    }

    public ObservableCollection<GameProfile> Profiles { get; } = new();
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public RelayCommand NewCommand { get; }
    public RelayCommand ImportCommand { get; }
    public RelayCommand ExportCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand LoadFormCommand { get; }
    public RelayCommand AddRegionCommand { get; }
    public RelayCommand RemoveRegionCommand { get; }
    public RelayCommand PlainPresetCommand { get; }
    public RelayCommand OutlinedPresetCommand { get; }
    public RelayCommand NoPreprocessingCommand { get; }
    public int SelectedRegionNumber { get; set; } = 1;
    public ProfileFormViewModel? Form { get => _form; private set => SetProperty(ref _form, value); }
    public bool CanEdit => !_busy && !_closing && !_readOnly;
    public bool IsReadOnly { get => _readOnly; private set { SetProperty(ref _readOnly, value); OnPropertyChanged(nameof(CanEdit)); } }
    public bool HasUnsavedChanges { get => _dirty; private set => SetProperty(ref _dirty, value); }
    public bool DiscardConfirmed { get => _discardConfirmed; set => SetProperty(ref _discardConfirmed, value); }
    public bool DeleteConfirmed { get => _deleteConfirmed; set => SetProperty(ref _deleteConfirmed, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public GameProfile? SelectedProfile
    {
        get => _selected;
        set { if (!_busy && !_closing && SetProperty(ref _selected, value)) { DeleteConfirmed = false; } }
    }
    public string DraftJson
    {
        get => _draftJson;
        set
        {
            if (CanEdit && Form?.HasErrors == true)
            { Status = "Correct unfinished form values before editing the advanced document, or confirm discarding the draft and reload."; return; }
            if (CanEdit && SetProperty(ref _draftJson, value))
            { Form = null; HasUnsavedChanges = true; }
        }
    }
    public string TransferJson
    {
        get => _transferJson;
        set { if (!_busy && !_closing) { SetProperty(ref _transferJson, value); } }
    }
    public string SecretReference
    {
        get => _secretReference;
        set { if (CanEdit && SetProperty(ref _secretReference, value)) { HasUnsavedChanges = true; } }
    }

    private AsyncRelayCommand Command(Func<CancellationToken, Task> action, IErrorReporter errors) => new(async token =>
    {
        _busy = true;
        _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NotifyCommands();
        try
        { await action(token); }
        catch (DatabaseStorageException error) { Status = error.Message + " No draft was discarded."; }
        catch (ArgumentException) { Status = "Correct the profile fields before saving. Last saved data is unchanged."; }
        finally { _busy = false; _drained.TrySetResult(); NotifyCommands(); }
    }, errors, () => !_busy && !_closing);

    private RelayCommand EditCommand(Action action) => new(() =>
    {
        try
        { action(); }
        catch (ArgumentException) { Status = "Profile document is invalid or too large. Existing draft and saved data are unchanged."; }
    }, () => !_busy && !_closing);

    private bool MayReplaceDraft()
    {
        if (HasUnsavedChanges && !DiscardConfirmed)
        { Status = "Save/export your draft or confirm discarding it before replacing it."; return false; }
        DiscardConfirmed = false;
        return true;
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        var profiles = await _repository.ListAsync(token);
        Profiles.Clear();
        foreach (var profile in profiles)
        { Profiles.Add(profile); }
        _selected = null;
        OnPropertyChanged(nameof(SelectedProfile));
        DeleteConfirmed = false;
        Status = "Saved profiles refreshed; your draft is unchanged.";
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (_selected is null || !MayReplaceDraft())
        { return; }
        var profile = await _repository.GetAsync(_selected.Id, token);
        if (profile is null)
        { Status = "This profile was removed. Refresh the list; your draft is unchanged."; return; }
        ReplaceDraft(profile, false);
        Status = "Saved profile loaded. Edit then Save; capture does not start automatically.";
    }

    private void New()
    {
        if (!MayReplaceDraft())
        { return; }
        ReplaceDraft(new GameProfile(ProfileId.New(), "New profile", "auto", "en", 8,
            new[] { new CaptureRegion(Guid.NewGuid(), "Dialogue", new NormalizedRegion(.1, .7, .8, .2), 0) }, false), true);
        Status = "New unsaved profile. Choose a configured provider and model before use.";
    }

    private void Import()
    {
        if (!MayReplaceDraft())
        { return; }
        var result = ProfileJson.Import(TransferJson);
        if (result.IsReadOnly)
        {
            _draftId = null;
            _draftJson = TransferJson;
            _secretReference = string.Empty;
            IsReadOnly = true;
            Form = null;
            HasUnsavedChanges = false;
            OnPropertyChanged(nameof(DraftJson));
            OnPropertyChanged(nameof(SecretReference));
            Status = "Newer profile schema: read-only preview. No saved data or capture state changed.";
            return;
        }
        ReplaceDraft(ProfileJson.Duplicate(result.Profile!, result.Profile!.Name), true);
        Status = "Imported as a NEW unsaved profile. Credentials/unknown private fields omitted; safe numeric/boolean x-* fields retained. Review and Save explicitly.";
    }

    private void Duplicate()
    {
        var profile = ReadDraft();
        if (!MayReplaceDraft())
        { return; }
        var name = profile.Name.Length <= 193 ? profile.Name + " (copy)" : profile.Name;
        ReplaceDraft(ProfileJson.Duplicate(profile, name), true);
        Status = "Duplicate has new profile and region IDs. Save explicitly; no glossary/history content was copied.";
    }

    private void Export()
    {
        var json = ProfileJson.Export(ReadDraft());
        TransferJson = json;
        Status = "Portable profile exported without credential references, history/cache content or private paths.";
    }

    private void ChangeRegions(bool add)
    {
        var profile = ReadDraft();
        var regions = profile.Regions.ToList();
        if (add)
        {
            if (regions.Count >= 32)
            { Status = "A profile supports at most 32 regions."; return; }
            regions.Add(new CaptureRegion(Guid.NewGuid(), "Dialogue " + (regions.Count + 1), new NormalizedRegion(.1, .7, .8, .2), regions.Count));
        }
        else
        {
            if (regions.Count <= 1 || SelectedRegionNumber < 1 || SelectedRegionNumber > regions.Count)
            { Status = "Select a region number; at least one region must remain."; return; }
            regions.RemoveAt(SelectedRegionNumber - 1);
        }
        ReplaceDraft(new GameProfile(profile.Id, profile.Name, profile.SourceLanguage, profile.TargetLanguage,
            profile.CaptureFramesPerSecond, regions.ToArray(), profile.HistoryEnabled, profile.Preprocessing, profile.Settings), true);
        Status = "Region draft updated. Coordinates are normalized from 0 to 1; Save validates containment.";
    }

    private void ApplyPreset(PreprocessingOptions options)
    {
        var profile = ReadDraft();
        ReplaceDraft(new GameProfile(profile.Id, profile.Name, profile.SourceLanguage, profile.TargetLanguage,
            profile.CaptureFramesPerSecond, profile.Regions, profile.HistoryEnabled, options, profile.Settings), true);
        Status = "OCR preset applied to the draft. Save explicitly; further preprocessing field edits make it Custom.";
    }

    public GameProfile ReadDraft()
    {
        if (Form?.HasErrors == true)
        { throw new ArgumentException("Correct unfinished form values first."); }
        if (IsReadOnly || _draftId is null)
        { throw new ArgumentException("Load or create an editable profile first."); }
        var parsed = ProfileJson.Import(DraftJson).Profile ?? throw new ArgumentException("Future schema is read-only.");
        if (parsed.Id != _draftId)
        { throw new ArgumentException("Use Duplicate instead of editing profile identity."); }
        var profile = new GameProfile(parsed.Id, parsed.Name, parsed.SourceLanguage, parsed.TargetLanguage,
            parsed.CaptureFramesPerSecond, parsed.Regions, parsed.HistoryEnabled, parsed.Preprocessing,
            parsed.Settings with { SecretReference = string.IsNullOrWhiteSpace(SecretReference) ? null : SecretReference.Trim() });
        GameProfileValidator.Validate(profile);
        return profile;
    }

    private async Task SaveAsync(CancellationToken token)
    {
        var profile = ReadDraft();
        await _repository.SaveAsync(profile, token);
        var existing = Profiles.FirstOrDefault(item => item.Id == profile.Id);
        if (existing is not null)
        { Profiles.Remove(existing); }
        Profiles.Add(profile);
        _selected = profile;
        OnPropertyChanged(nameof(SelectedProfile));
        ReplaceDraft(profile, false);
        Status = "Profile and regions saved transactionally. Confirm a window explicitly before starting.";
    }

    private async Task DeleteAsync(CancellationToken token)
    {
        if (_selected is null)
        { Status = "Select a saved profile to delete."; return; }
        if (!DeleteConfirmed)
        { Status = "Confirm deletion: this removes the profile, its regions and scoped glossary. Global glossary/cache remain; history loses the profile link."; return; }
        if (_draftId == _selected.Id && !MayReplaceDraft())
        { return; }
        DeleteConfirmed = false;
        var target = _selected;
        await _repository.DeleteAsync(target.Id, token);
        Profiles.Remove(target);
        if (_draftId == target.Id)
        {
            _draftId = null;
            _draftJson = string.Empty;
            _secretReference = string.Empty;
            Form = null;
            HasUnsavedChanges = false;
            OnPropertyChanged(nameof(DraftJson));
            OnPropertyChanged(nameof(SecretReference));
        }
        _selected = null;
        OnPropertyChanged(nameof(SelectedProfile));
        Status = "Selected profile, regions and scoped glossary deleted. Cache/global glossary and unlinked history remain.";
    }

    private void ReplaceDraft(GameProfile profile, bool dirty)
    {
        var json = ProfileJson.Export(profile);
        _draftId = profile.Id;
        _draftJson = json;
        _secretReference = profile.Settings.SecretReference ?? string.Empty;
        IsReadOnly = false;
        Form = CreateForm(profile);
        HasUnsavedChanges = dirty;
        DeleteConfirmed = false;
        OnPropertyChanged(nameof(DraftJson));
        OnPropertyChanged(nameof(SecretReference));
    }

    private ProfileFormViewModel CreateForm(GameProfile profile) => new(profile, () => CanEdit, () =>
    {
        _draftJson = Form!.Json;
        HasUnsavedChanges = true;
        OnPropertyChanged(nameof(DraftJson));
        Status = Form.HasErrors ? "Correct the highlighted form values before saving." : "Unsaved form changes. Save validates all settings and regions.";
    });

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        foreach (var command in Commands())
        { command.NotifyCanExecuteChanged(); }
        foreach (var command in new[] { NewCommand, ImportCommand, ExportCommand, DuplicateCommand, LoadFormCommand, AddRegionCommand, RemoveRegionCommand,
            PlainPresetCommand, OutlinedPresetCommand, NoPreprocessingCommand })
        { command.NotifyCanExecuteChanged(); }
    }
    public async Task ShutdownAsync()
    {
        _closing = true;
        foreach (var command in Commands())
        { command.Cancel(); }
        NotifyCommands();
        await _drained.Task;
    }
    public void Dispose()
    { _closing = true; foreach (var command in Commands()) { command.Dispose(); } }
    private AsyncRelayCommand[] Commands() => [RefreshCommand, LoadCommand, SaveCommand, DeleteCommand];
    private static TaskCompletionSource Completed()
    { var value = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); value.SetResult(); return value; }
}

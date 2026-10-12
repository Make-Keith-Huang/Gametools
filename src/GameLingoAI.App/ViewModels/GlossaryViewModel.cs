using System.Collections.ObjectModel;
using System.Windows.Input;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.App.ViewModels;

/// <summary>Edits a draft; explicit save atomically persists it against its loaded baseline.</summary>
public sealed class GlossaryViewModel : ObservableObject, IDisposable
{
    private readonly IGlossaryRepository? _repository;
    private IReadOnlyList<GlossaryEntry> _baseline = Array.Empty<GlossaryEntry>();
    private bool _busy, _loaded, _dirty, _discardConfirmed;
    private GlossaryCatalog _catalog = new([]);
    private GlossaryEntry? _selectedEntry;
    private string _profileIdText = string.Empty;
    private string _sourceLanguage = "ja";
    private string _targetLanguage = "zh-Hans";
    private string _sourceTerm = string.Empty;
    private string _targetTerm = string.Empty;
    private string _sampleText = string.Empty;
    private string _jsonText = string.Empty;
    private string _preview = string.Empty;
    private string _status = string.Empty;
    private bool _caseSensitive;
    private bool _enabled = true;
    private bool _preserveLineBreaks = true;
    private bool _preserveTags = true;

    public GlossaryViewModel(IGlossaryRepository? repository = null, IErrorReporter? errors = null)
    {
        _repository = repository;
        AddCommand = new RelayCommand(Add, () => !_busy);
        UpdateCommand = new RelayCommand(Update, () => !_busy);
        DeleteCommand = new RelayCommand(Delete, () => !_busy);
        ImportCommand = new RelayCommand(Import, () => !_busy);
        ExportCommand = new RelayCommand(Export);
        PreviewCommand = new RelayCommand(BuildPreview);
        LoadCommand = PersistenceCommand(LoadAsync, errors ?? new ErrorReporter());
        SaveCommand = PersistenceCommand(SaveAsync, errors ?? new ErrorReporter());
    }

    public ObservableCollection<GlossaryEntry> Entries { get; } = new();
    public GlossaryCatalog Snapshot => _catalog;
    public ICommand AddCommand { get; }
    public ICommand UpdateCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand PreviewCommand { get; }
    public AsyncRelayCommand LoadCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public bool HasUnsavedChanges { get => _dirty; private set => SetProperty(ref _dirty, value); }
    public bool DiscardConfirmed { get => _discardConfirmed; set => SetProperty(ref _discardConfirmed, value); }

    private AsyncRelayCommand PersistenceCommand(Func<CancellationToken, Task> action, IErrorReporter errors) => new(async token =>
    {
        _busy = true;
        NotifyCommands();
        try
        { await action(token); }
        catch (DatabaseStorageException error) { Status = error.Message + " Draft and last saved glossary are unchanged."; }
        catch (InvalidOperationException) { Status = "Stored glossary changed. Export your draft, reload and merge before saving."; }
        finally { _busy = false; NotifyCommands(); }
    }, errors, () => !_busy && _repository is not null);

    private async Task LoadAsync(CancellationToken token)
    {
        if (HasUnsavedChanges && !DiscardConfirmed)
        { Status = "Export/save your draft or confirm discarding changes before loading."; return; }
        DiscardConfirmed = false;
        var entries = await _repository!.ListAsync(token);
        ReplaceCatalog(new GlossaryCatalog(entries));
        _baseline = _catalog.Entries;
        _loaded = true;
        HasUnsavedChanges = false;
        Status = "Saved glossary loaded from local SQLite.";
    }

    private async Task SaveAsync(CancellationToken token)
    {
        if (!_loaded)
        { Status = "Load the saved glossary before editing and saving; export your current draft first if needed."; return; }
        var snapshot = _catalog.Entries;
        await _repository!.ReplaceAsync(snapshot, _baseline, token);
        _baseline = snapshot;
        HasUnsavedChanges = false;
        Status = "Glossary saved transactionally to local SQLite.";
    }

    private void NotifyCommands()
    {
        foreach (var command in new[] { AddCommand, UpdateCommand, DeleteCommand, ImportCommand })
        { ((RelayCommand)command).NotifyCanExecuteChanged(); }
        LoadCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    public void Dispose() { LoadCommand.Dispose(); SaveCommand.Dispose(); }

    public GlossaryEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (!SetProperty(ref _selectedEntry, value) || value is null)
            {
                return;
            }

            ProfileIdText = value.ProfileId?.Value.ToString() ?? string.Empty;
            SourceLanguage = value.SourceLanguage;
            TargetLanguage = value.TargetLanguage;
            SourceTerm = value.SourceTerm;
            TargetTerm = value.TargetTerm;
            CaseSensitive = value.CaseSensitive;
            Enabled = value.Enabled;
        }
    }

    public string ProfileIdText { get => _profileIdText; set { if (SetProperty(ref _profileIdText, value)) { Preview = string.Empty; } } }
    public string SourceLanguage { get => _sourceLanguage; set { if (SetProperty(ref _sourceLanguage, value)) { Preview = string.Empty; } } }
    public string TargetLanguage { get => _targetLanguage; set { if (SetProperty(ref _targetLanguage, value)) { Preview = string.Empty; } } }
    public string SourceTerm { get => _sourceTerm; set => SetProperty(ref _sourceTerm, value); }
    public string TargetTerm { get => _targetTerm; set => SetProperty(ref _targetTerm, value); }
    public string SampleText { get => _sampleText; set { if (SetProperty(ref _sampleText, value)) { Preview = string.Empty; } } }
    public string JsonText { get => _jsonText; set => SetProperty(ref _jsonText, value); }
    public string Preview { get => _preview; private set => SetProperty(ref _preview, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool CaseSensitive { get => _caseSensitive; set => SetProperty(ref _caseSensitive, value); }
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool PreserveLineBreaks { get => _preserveLineBreaks; set { if (SetProperty(ref _preserveLineBreaks, value)) { Preview = string.Empty; } } }
    public bool PreserveTags { get => _preserveTags; set { if (SetProperty(ref _preserveTags, value)) { Preview = string.Empty; } } }

    private ProfileId? ParseProfileId()
    {
        if (string.IsNullOrWhiteSpace(ProfileIdText))
        {
            return null;
        }

        if (!Guid.TryParse(ProfileIdText, out var id) || id == Guid.Empty)
        {
            throw new FormatException("Profile ID must be a non-empty GUID, or blank for global terms.");
        }

        return new ProfileId(id);
    }

    private void Add()
    {
        try
        {
            var entry = new GlossaryEntry(Guid.NewGuid(), ParseProfileId(), SourceLanguage,
                TargetLanguage, SourceTerm, TargetTerm, CaseSensitive, Enabled);
            ReplaceCatalog(_catalog.WithEntry(entry));
            Status = "Term added to the draft. Save to retain it after closing.";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Status = exception.Message;
        }
    }

    private void Delete()
    {
        if (SelectedEntry is null)
        { Status = "Select a term to remove."; return; }
        ReplaceCatalog(_catalog.WithoutEntry(SelectedEntry.Id));
        Status = "Term removed.";
    }

    private void Update()
    {
        if (SelectedEntry is null)
        { Status = "Select a term to edit."; return; }
        try
        {
            var entry = new GlossaryEntry(SelectedEntry.Id, ParseProfileId(), SourceLanguage,
                TargetLanguage, SourceTerm, TargetTerm, CaseSensitive, Enabled);
            ReplaceCatalog(_catalog.WithEntry(entry));
            Status = "Selected term updated.";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Status = exception.Message;
        }
    }

    private void Import()
    {
        try
        {
            var imported = GlossaryCatalog.ImportJson(JsonText);
            ReplaceCatalog(imported);
            Status = $"Imported {Entries.Count} terms.";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Status = $"Import rejected; existing terms unchanged. {exception.Message}";
        }
    }

    private void Export()
    {
        JsonText = _catalog.ExportJson();
        Status = "Versioned glossary JSON is ready to copy.";
    }

    private void BuildPreview()
    {
        try
        {
            var profile = ParseProfileId();
            var terms = _catalog.Select(profile, SourceLanguage, TargetLanguage, SampleText);
            var metadata = new PipelineMetadata(SessionId.New(), new SequenceNumber(0),
                DateTimeOffset.UnixEpoch, CorrelationId.New());
            var request = new TranslationRequest(metadata, SourceLanguage, TargetLanguage,
                SampleText, [], terms, null,
                new TranslationPromptOptions(PreserveLineBreaks, PreserveTags));
            var plan = TranslationPromptBuilder.Build(request);
            Preview = plan.Preview;
            Status = $"Prompt v{plan.Version}; glossary revision {_catalog.Revision(profile, SourceLanguage, TargetLanguage)}";
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            Preview = string.Empty;
            Status = exception.Message;
        }
    }

    private void ReplaceCatalog(GlossaryCatalog catalog)
    {
        HasUnsavedChanges = true;
        _catalog = catalog;
        Preview = string.Empty;
        Entries.Clear();
        foreach (var entry in catalog.Entries.OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal))
        {
            Entries.Add(entry);
        }

        SelectedEntry = null;
    }
}

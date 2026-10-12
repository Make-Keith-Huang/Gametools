using System.Collections.ObjectModel;
using System.Windows.Input;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.ViewModels;

public enum PageId
{
    HomeSession,
    WindowRegion,
    Provider,
    ProfilesSettings,
    Diagnostics,
    About
}

public sealed class SessionStatusViewModel : ObservableObject
{
    private SessionState _state = SessionState.Stopped;

    public SessionState State
    {
        get => _state;
        set => SetProperty(ref _state, value);
    }

    public ObservableCollection<string> ValidationErrors { get; } = new();
}

public sealed class ShellViewModel : ObservableObject
{
    private PageId _currentPage = PageId.HomeSession;
    private string? _lastError;

    public ShellViewModel(
        SessionStatusViewModel sessionStatus,
        WindowSelectionViewModel windowSelection,
        RegionEditorViewModel regionEditor,
        OcrModelViewModel ocrModel,
        IErrorReporter errorReporter,
        ProviderConnectionViewModel? providerConnection = null,
        GlossaryViewModel? glossary = null,
        CacheViewModel? cache = null,
        SessionViewModel? session = null,
        DatabaseViewModel? database = null,
        ProfilesViewModel? profiles = null,
        HotkeysViewModel? hotkeys = null, DiagnosticsViewModel? diagnostics = null)
    {
        SessionStatus = sessionStatus ?? throw new ArgumentNullException(nameof(sessionStatus));
        WindowSelection = windowSelection ?? throw new ArgumentNullException(nameof(windowSelection));
        RegionEditor = regionEditor ?? throw new ArgumentNullException(nameof(regionEditor));
        OcrModel = ocrModel ?? throw new ArgumentNullException(nameof(ocrModel));
        ProviderConnection = providerConnection;
        Glossary = glossary;
        Cache = cache;
        Session = session;
        Database = database;
        Profiles = profiles;
        Hotkeys = hotkeys;
        Diagnostics = diagnostics;
        ArgumentNullException.ThrowIfNull(errorReporter);
        NavigateCommand = new RelayCommand<PageId>(Navigate);
        errorReporter.ErrorReported += (_, message) => LastError = message;
    }

    public ICommand NavigateCommand { get; }

    public SessionStatusViewModel SessionStatus { get; }

    public WindowSelectionViewModel WindowSelection { get; }

    public RegionEditorViewModel RegionEditor { get; }

    public OcrModelViewModel OcrModel { get; }

    public ProviderConnectionViewModel? ProviderConnection { get; }

    public GlossaryViewModel? Glossary { get; }

    public CacheViewModel? Cache { get; }
    public SessionViewModel? Session { get; }
    public DatabaseViewModel? Database { get; }
    public ProfilesViewModel? Profiles { get; }
    public HotkeysViewModel? Hotkeys { get; }
    public DiagnosticsViewModel? Diagnostics { get; }

    public PageId CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string? LastError
    {
        get => _lastError;
        private set => SetProperty(ref _lastError, value);
    }

    private void Navigate(PageId page)
    {
        if (CurrentPage == PageId.WindowRegion && page != PageId.WindowRegion)
        {
            RegionEditor.OnNavigatedFrom();
        }

        CurrentPage = page;
    }
}

public sealed class SessionContext : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();

    public SessionId SessionId { get; } = SessionId.New();

    public CancellationToken CancellationToken => _cancellation.Token;

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}

public sealed class PlaceholderPageViewModel
{
}

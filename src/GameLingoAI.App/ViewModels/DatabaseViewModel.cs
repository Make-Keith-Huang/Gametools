using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;

namespace GameLingoAI.App.ViewModels;

/// <summary>Non-secret database status, explicit recovery copy and confirmed private-data deletion.</summary>
public sealed class DatabaseViewModel : ObservableObject, IDisposable
{
    private readonly SqliteDatabase _database;
    private readonly IConfigurationStore _configuration;
    private readonly ITranslationCacheRepository _cache;
    private readonly ITranslationHistoryRepository _history;
    private readonly IStoppedSessionMaintenance _maintenance;
    private readonly CacheViewModel? _cacheView;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _initialization;
    private bool _busy, _closing, _confirmDelete, _initializing;
    private string _status = "Database has not been checked.";
    private string _recoveryPath = string.Empty;

    public DatabaseViewModel(SqliteDatabase database, IConfigurationStore configuration, ITranslationCacheRepository cache,
        ITranslationHistoryRepository history, IStoppedSessionMaintenance maintenance, IErrorReporter errors, CacheViewModel? cacheView = null)
    {
        _database = database;
        _configuration = configuration;
        _cache = cache;
        _history = history;
        _maintenance = maintenance;
        _cacheView = cacheView;
        RetryCommand = Command(CheckAsync, errors);
        ExportRecoveryCommand = Command(ExportAsync, errors);
        ClearPrivateDataCommand = Command(ClearAsync, errors);
    }

    public AsyncRelayCommand RetryCommand { get; }
    public AsyncRelayCommand ExportRecoveryCommand { get; }
    public AsyncRelayCommand ClearPrivateDataCommand { get; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string RecoveryPath { get => _recoveryPath; set => SetProperty(ref _recoveryPath, value); }
    public bool ConfirmDelete { get => _confirmDelete; set => SetProperty(ref _confirmDelete, value); }
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        _initializing = true;
        NotifyCommands();
        try
        { await CheckAsync(_lifetime.Token); }
        finally { _initializing = false; NotifyCommands(); }
    }

    private AsyncRelayCommand Command(Func<CancellationToken, Task> action, IErrorReporter errors) => new(async token =>
    {
        _busy = true;
        NotifyCommands();
        try
        { await action(token); }
        catch (DatabaseStorageException error) { Status = error.Message; }
        catch (CacheStorageException) { Status = "Cache storage is unavailable. Retry database initialization or export a recovery copy."; }
        finally { _busy = false; NotifyCommands(); }
    }, errors, () => !_busy && !_closing && !_initializing);

    private async Task CheckAsync(CancellationToken token)
    {
        try
        {
            var status = await _database.InitializeAsync(token);
            if (!status.IsWritable)
            { Status = new DatabaseStorageException(status.Failure ?? DatabaseFailureKind.Unavailable).Message; return; }
            var settings = await _configuration.LoadAsync(token);
            if (!settings.IsSuccess)
            { Status = "Database is ready, but configuration is invalid. Correct settings before enabling persistence policies."; return; }
            await _cache.ConfigureAsync(TranslationCachePolicy.FromOptions(settings.Value!.Cache), token);
            await _history.ConfigureAsync(new HistoryPolicy(settings.Value.Privacy.HistoryEnabled), token);
            Status = "SQLite is ready. Profiles and cached text persist locally; history is off unless explicitly enabled.";
        }
        catch (DatabaseStorageException error) { Status = error.Message; }
        catch (CacheStorageException) { Status = "Database policy initialization failed. Translation can continue without cache storage; retry when storage is available."; }
    }

    private async Task ExportAsync(CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(RecoveryPath))
        { Status = "Enter a new local destination file path for the recovery copy."; return; }
        await _database.ExportRecoveryCopyAsync(RecoveryPath, true, token);
        Status = "Recovery copy saved locally. It may contain private text; protect it and do not share it unintentionally.";
    }

    private async Task ClearAsync(CancellationToken token)
    {
        if (!ConfirmDelete)
        { Status = "Confirm deletion first. Profiles, glossary and separate backups will be retained."; return; }
        ConfirmDelete = false;
        try
        {
            await _maintenance.RunAsync(async maintenanceToken =>
            {
                var result = await _database.ClearPrivateDataAsync(true, true, maintenanceToken);
                _cacheView?.ClearDisplayedExport();
                Status = result.SpaceReclaimed
                    ? "Cache/history deleted and database compacted. Profiles, glossary and separate backups remain."
                    : "Cache/history deleted. Compaction/checkpoint could not finish; close other instances and retry. Separate backups remain.";
            }, token);
        }
        catch (InvalidOperationException) { Status = "Stop the session before clearing private data, then confirm deletion again."; }
    }

    public async Task ShutdownAsync()
    {
        _closing = true;
        _lifetime.Cancel();
        foreach (var command in Commands())
        { command.Cancel(); }
        if (_initialization is not null)
        { try { await _initialization; } catch (OperationCanceledException) { } }
        await _database.StopAsync(CancellationToken.None);
    }
    private AsyncRelayCommand[] Commands() => [RetryCommand, ExportRecoveryCommand, ClearPrivateDataCommand];
    private void NotifyCommands() { foreach (var command in Commands()) { command.NotifyCanExecuteChanged(); } }
    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        foreach (var command in Commands())
        { command.Dispose(); }
    }
}

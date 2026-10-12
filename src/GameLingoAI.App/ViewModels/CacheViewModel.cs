using System.IO;
using System.Text.Json;
using System.Windows.Input;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.ViewModels;

public sealed class CacheViewModel : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };
    private readonly ITranslationCacheRepository _repository;
    private readonly IConfigurationStore _configuration;
    private int _maximumRows = 100_000;
    private int _maximumMegabytes = 500;
    private int _maximumAgeDays = 30;
    private string _status = "Cache is independent of history. Load settings to inspect current retention.";
    private string _exportJson = string.Empty;
    private bool _isBusy;
    private long _exportRevision;

    public CacheViewModel(ITranslationCacheRepository repository, IConfigurationStore configuration,
        IErrorReporter errorReporter)
    {
        _repository = repository;
        _configuration = configuration;
        LoadCommand = CreateCommand(LoadAsync, errorReporter);
        ApplyCommand = CreateCommand(ApplyAsync, errorReporter);
        ClearCommand = CreateCommand(ClearAsync, errorReporter);
        ExportStatisticsCommand = CreateCommand(ExportStatisticsAsync, errorReporter);
        ExportContentCommand = CreateCommand(ExportContentAsync, errorReporter);
    }

    public ICommand LoadCommand { get; }
    public ICommand ApplyCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ExportStatisticsCommand { get; }
    public ICommand ExportContentCommand { get; }
    public int MaximumRows { get => _maximumRows; set => SetProperty(ref _maximumRows, value); }
    public int MaximumMegabytes { get => _maximumMegabytes; set => SetProperty(ref _maximumMegabytes, value); }
    public int MaximumAgeDays { get => _maximumAgeDays; set => SetProperty(ref _maximumAgeDays, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ExportJson { get => _exportJson; private set => SetProperty(ref _exportJson, value); }

    private AsyncRelayCommand CreateCommand(Func<CancellationToken, Task> action, IErrorReporter reporter) =>
        new(async cancellationToken =>
        {
            _isBusy = true;
            NotifyCommands();
            try
            { await action(cancellationToken).ConfigureAwait(true); }
            finally { _isBusy = false; NotifyCommands(); }
        }, reporter, () => !_isBusy);

    private void NotifyCommands()
    {
        foreach (var command in new[] { LoadCommand, ApplyCommand, ClearCommand,
            ExportStatisticsCommand, ExportContentCommand })
        {
            ((AsyncRelayCommand)command).NotifyCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        foreach (var command in new[] { LoadCommand, ApplyCommand, ClearCommand,
            ExportStatisticsCommand, ExportContentCommand })
        {
            ((AsyncRelayCommand)command).Dispose();
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var loaded = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (!loaded.IsSuccess)
        { Status = "Cache settings could not be loaded. Check configuration and retry."; return; }
        var options = loaded.Value!.Cache;
        MaximumRows = options.MaximumRows;
        MaximumMegabytes = options.MaximumMegabytes;
        MaximumAgeDays = options.MaximumAgeDays;
        await RunStorageAsync(async () =>
        {
            await _repository.ConfigureAsync(TranslationCachePolicy.FromOptions(options), cancellationToken).ConfigureAwait(false);
            return await _repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(true);
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var loaded = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (!loaded.IsSuccess)
        { Status = "Cache settings could not be loaded; no changes were saved."; return; }
        var options = loaded.Value!.Cache with
        {
            MaximumRows = MaximumRows,
            MaximumMegabytes = MaximumMegabytes,
            MaximumAgeDays = MaximumAgeDays
        };
        TranslationCachePolicy policy;
        try
        { policy = TranslationCachePolicy.FromOptions(options); }
        catch (ArgumentException) { Status = "Use 0–1,000,000 rows, 1–4,096 MiB, and 1–3,650 days."; return; }
        await _configuration.SaveAsync(loaded.Value with { Cache = options }, cancellationToken).ConfigureAwait(true);
        await RunStorageAsync(async () =>
        {
            await _repository.ConfigureAsync(policy, cancellationToken).ConfigureAwait(false);
            return await _repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(true);
    }

    private async Task ClearAsync(CancellationToken cancellationToken)
    {
        ClearDisplayedExport();
        await RunStorageAsync(async () =>
        {
            await _repository.ClearAsync(cancellationToken).ConfigureAwait(false);
            return await _repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(true);
    }

    private async Task ExportStatisticsAsync(CancellationToken cancellationToken)
    {
        var stats = await RunStorageAsync(() => _repository.GetStatisticsAsync(cancellationToken), cancellationToken)
            .ConfigureAwait(true);
        if (stats is not null)
        {
            ExportJson = JsonSerializer.Serialize(stats, ExportOptions);
        }
    }

    private async Task ExportContentAsync(CancellationToken cancellationToken)
    {
        var revision = _exportRevision;
        try
        {
            var json = await Task.Run(async () =>
            {
                var entries = await _repository.ExportContentAsync(100, userRequested: true, cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.Serialize(new { Version = 1, Entries = entries }, ExportOptions);
            }, cancellationToken).ConfigureAwait(true);
            if (revision != _exportRevision)
            { return; }
            ExportJson = json;
            Status = "Explicit text export: up to 100 recent entries. The JSON contains source and translated text.";
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            ExportJson = string.Empty;
            Status = "Cache storage is unavailable. Translation can continue without persistence.";
        }
    }

    public void ClearDisplayedExport()
    {
        _exportRevision++;
        ExportJson = string.Empty;
    }

    private async Task<TranslationCacheStatistics?> RunStorageAsync(Func<Task<TranslationCacheStatistics>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            var stats = await Task.Run(action, cancellationToken).ConfigureAwait(true);
            Status = $"Cache: {stats.Rows} rows, {stats.Bytes} bytes; hits {stats.Hits}, misses {stats.Misses}, evictions {stats.Evictions}.";
            return stats;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            Status = "Cache storage is unavailable. Translation can continue without persistence.";
            return null;
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is CacheStorageException or IOException or UnauthorizedAccessException;
}

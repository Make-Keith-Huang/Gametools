using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;

internal static class DatabaseAdminTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-AdminTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new SqliteDatabase(Path.Combine(directory, "test.db"));
            var clock = new Clock();
            var cache = new SqliteTranslationCacheRepository(database, clock);
            var delayed = new DelayedExport(cache);
            var configuration = new Configuration();
            var reporter = new Reporter();
            var maintenance = new Maintenance();
            using var cacheView = new CacheViewModel(delayed, configuration, reporter);
            using var admin = new DatabaseViewModel(database, configuration, cache, new SqliteHistoryRepository(database, clock), maintenance, reporter, cacheView);
            var notifications = 0;
            admin.RetryCommand.CanExecuteChanged += (_, _) => notifications++;
            await admin.InitializeAsync();
            Assert(admin.Status.Contains("ready", StringComparison.Ordinal) && admin.RetryCommand.CanExecute(null) && notifications > 0,
                "Initialization status/command notification failed.");
            var key = new TranslationCacheKey(new string('a', 64), "fake", "model", "en", "ja", "prompt", "glossary", new string('b', 64), "private fixture");
            var generation = (await cache.FindAsync(key.Value, CancellationToken.None)).Generation;
            await cache.UpsertBatchAsync(new[] { new TranslationCacheEntry(key, "private translation", clock.UtcNow, clock.UtcNow) }, generation, CancellationToken.None);
            await admin.ClearPrivateDataCommand.ExecuteAsync();
            Assert((await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 1, "Unconfirmed UI clear deleted data.");
            maintenance.Running = true;
            admin.ConfirmDelete = true;
            await admin.ClearPrivateDataCommand.ExecuteAsync();
            Assert(admin.Status.Contains("Stop", StringComparison.Ordinal) && !admin.ConfirmDelete &&
                (await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 1, "Running-session UI guard failed.");
            maintenance.Running = false;
            var export = ((AsyncRelayCommand)cacheView.ExportContentCommand).ExecuteAsync();
            await delayed.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            admin.ConfirmDelete = true;
            await admin.ClearPrivateDataCommand.ExecuteAsync();
            delayed.Release.TrySetResult();
            await export;
            Assert(cacheView.ExportJson.Length == 0 && (await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 0,
                "Delayed export restored private text after clear.");
            admin.RecoveryPath = Path.Combine(directory, "recovery.db");
            await admin.ExportRecoveryCommand.ExecuteAsync();
            var bytes = await File.ReadAllBytesAsync(admin.RecoveryPath);
            Assert(bytes.Length > 0, "UI recovery export failed.");
            await admin.ExportRecoveryCommand.ExecuteAsync();
            Assert(bytes.SequenceEqual(await File.ReadAllBytesAsync(admin.RecoveryPath)), "Recovery export overwrote an existing file.");
            Assert(admin.Status.Contains("unavailable", StringComparison.Ordinal), "Recovery destination collision was not actionable.");
            await admin.ShutdownAsync();
            Assert(!admin.RetryCommand.CanExecute(null) && reporter.Errors == 0, "Admin shutdown or safe-error handling failed.");

            var corruptPath = Path.Combine(directory, "corrupt.db");
            await File.WriteAllTextAsync(corruptPath, "private malformed database");
            using var corrupt = new SqliteDatabase(corruptPath);
            using var recovery = new DatabaseViewModel(corrupt, configuration, new SqliteTranslationCacheRepository(corrupt, clock),
                new SqliteHistoryRepository(corrupt, clock), maintenance, reporter);
            await recovery.InitializeAsync();
            Assert(recovery.Status.Contains("integrity", StringComparison.Ordinal) &&
                !recovery.Status.Contains("private malformed", StringComparison.Ordinal), "Corruption status leaked data or was not actionable.");
            await recovery.ShutdownAsync();
            Console.WriteLine("Database admin UI: initialization, confirmation/running guards, delayed-export privacy, recovery no-overwrite, corruption and shutdown passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow => new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero); }
    private sealed class Configuration : IConfigurationStore
    {
        public Task<OperationResult<AppSettings>> LoadAsync(CancellationToken token) => Task.FromResult(OperationResult.Success(new AppSettings()));
        public Task SaveAsync(AppSettings settings, CancellationToken token) => Task.CompletedTask;
        public Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken token) => Task.FromResult("{}");
    }
    private sealed class Reporter : IErrorReporter
    {
        public int Errors;
        public event EventHandler<string>? ErrorReported;
        public void Report(Exception error) { Errors++; ErrorReported?.Invoke(this, "Safe test failure."); }
    }
    private sealed class Maintenance : IStoppedSessionMaintenance
    {
        public bool Running;
        public Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token) => Running
            ? throw new InvalidOperationException("Session is running.") : action(token);
    }
    private sealed class DelayedExport(ITranslationCacheRepository inner) : ITranslationCacheRepository
    {
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TranslationCacheRead> FindAsync(string key, CancellationToken token) => inner.FindAsync(key, token);
        public Task<int> UpsertBatchAsync(IReadOnlyList<TranslationCacheEntry> entries, long generation, CancellationToken token) => inner.UpsertBatchAsync(entries, generation, token);
        public Task<int> PruneAsync(CancellationToken token) => inner.PruneAsync(token);
        public Task ConfigureAsync(TranslationCachePolicy policy, CancellationToken token) => inner.ConfigureAsync(policy, token);
        public Task ClearAsync(CancellationToken token) => inner.ClearAsync(token);
        public Task<TranslationCacheStatistics> GetStatisticsAsync(CancellationToken token) => inner.GetStatisticsAsync(token);
        public async Task<IReadOnlyList<TranslationCacheEntry>> ExportContentAsync(int count, bool requested, CancellationToken token)
        {
            var entries = await inner.ExportContentAsync(count, requested, token);
            Ready.TrySetResult();
            await Release.Task.WaitAsync(token);
            return entries;
        }
    }
}

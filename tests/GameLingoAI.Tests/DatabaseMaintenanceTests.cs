using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

internal static class DatabaseMaintenanceTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-MaintenanceTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "maintenance.db");
            using var database = new SqliteDatabase(path);
            var clock = new Clock();
            var profiles = new SqliteProfileRepository(database, clock);
            var glossary = new SqliteGlossaryRepository(database, clock);
            var cache = new SqliteTranslationCacheRepository(database, clock);
            var history = new SqliteHistoryRepository(database, clock);
            var profile = new GameProfile(ProfileId.New(), "Retained", "en", "ja", 8,
                new[] { new CaptureRegion(Guid.NewGuid(), "Dialogue", new NormalizedRegion(0, 0, 1, 1), 0) }, false);
            await profiles.SaveAsync(profile, CancellationToken.None);
            await glossary.SaveAsync(new GlossaryEntry(Guid.NewGuid(), profile.Id, "en", "ja", "hero", "勇者"), CancellationToken.None);
            var key = new TranslationCacheKey(new string('a', 64), "fake", "model", "en", "ja", "prompt", "glossary", new string('b', 64), "private source");
            var entry = new TranslationCacheEntry(key, "private translation", clock.UtcNow, clock.UtcNow);
            var generation = (await cache.FindAsync(key.Value, CancellationToken.None)).Generation;
            await cache.UpsertBatchAsync(new[] { entry }, generation, CancellationToken.None);
            await history.ConfigureAsync(new HistoryPolicy(true), CancellationToken.None);
            await history.AppendAsync(new TranslationHistoryEntry(SessionId.New(), profile.Id, "private source", "private translation", clock.UtcNow, 1), CancellationToken.None);
            await ThrowsAsync<InvalidOperationException>(() => database.ClearPrivateDataAsync(false, true, CancellationToken.None));
            await ThrowsAsync<InvalidOperationException>(() => database.ClearPrivateDataAsync(true, false, CancellationToken.None));
            await ThrowsAsync<OperationCanceledException>(() => database.ClearPrivateDataAsync(true, true, new CancellationToken(true)));
            Assert((await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 1, "Rejected clear changed data.");

            var result = await database.ClearPrivateDataAsync(true, true, CancellationToken.None);
            Assert(result.SpaceReclaimed, "Uncontended clear did not compact/checkpoint.");
            Assert((await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 0 &&
                (await history.ListAsync(100, true, CancellationToken.None)).Count == 0, "Clear retained cache/history text.");
            Assert((await profiles.ListAsync(CancellationToken.None)).Count == 1 &&
                (await glossary.ListAsync(CancellationToken.None)).Count == 1, "Clear deleted profile/glossary configuration.");
            Assert(await cache.UpsertBatchAsync(new[] { entry }, generation, CancellationToken.None) == 0,
                "Private-data clear allowed a late cache write.");
            var bytes = await File.ReadAllBytesAsync(path);
            Assert(!System.Text.Encoding.UTF8.GetString(bytes).Contains("private source", StringComparison.Ordinal),
                "Compacted main database retained the private text fixture.");

            using var blocker = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            blocker.Open();
            using var transaction = blocker.BeginTransaction();
            var pending = profiles.SaveAsync(profile, CancellationToken.None);
            var stopped = database.StopAsync(CancellationToken.None);
            Assert(!stopped.IsCompleted, "Shutdown did not wait for an admitted write.");
            await ThrowsAsync<ObjectDisposedException>(() => database.InitializeAsync(CancellationToken.None));
            transaction.Rollback();
            await pending;
            await stopped;
            await database.StopAsync(CancellationToken.None);
            Console.WriteLine("SQLite maintenance: confirmation/stopped guards, cancel, clear/generation, retained configuration, vacuum and admitted-operation shutdown drain passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow => new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero); }
}

using System.Security.Cryptography;
using System.Text;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using GameLingoAI.Infrastructure.Cache;
using GameLingoAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

internal static class SqliteCacheTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-CacheTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "cache.db");
            using var database = new SqliteDatabase(path);
            using var secondDatabase = new SqliteDatabase(path);
            var clock = new Clock();
            var cache = new SqliteTranslationCacheRepository(database, clock);
            var second = new SqliteTranslationCacheRepository(secondDatabase, clock);
            var a = Entry("a '); DROP TABLE translation_cache; --", clock.UtcNow);
            var b = Entry("日本語", clock.UtcNow);
            var c = Entry("c", clock.UtcNow);
            await cache.ConfigureAsync(new TranslationCachePolicy(2, 20000, TimeSpan.FromDays(1), 1), CancellationToken.None);
            var generation = (await cache.FindAsync(a.Key.Value, CancellationToken.None)).Generation;
            Assert(await cache.UpsertBatchAsync(new[] { a, b }, generation, CancellationToken.None) == 2, "Batch was not stored.");
            var hit = await second.FindAsync(a.Key.Value, CancellationToken.None);
            Assert(hit.Entry?.Key == a.Key && hit.Entry.HitCount == 1, "Persistent lookup/touch failed.");
            Assert(await second.UpsertBatchAsync(new[] { c }, generation, CancellationToken.None) == 1, "Second instance could not write.");
            Assert((await cache.FindAsync(b.Key.Value, CancellationToken.None)).Entry is null, "LRU did not evict the oldest at an identical timestamp.");
            Assert((await cache.FindAsync(a.Key.Value, CancellationToken.None)).Entry is not null, "LRU evicted a recently touched entry.");
            var replacement = new TranslationCacheEntry(a.Key, "replacement", clock.UtcNow, clock.UtcNow);
            await cache.UpsertBatchAsync(new[] { replacement }, generation, CancellationToken.None);
            Assert((await second.FindAsync(a.Key.Value, CancellationToken.None)).Entry?.TranslatedText == "replacement", "Upsert replacement failed.");
            await ThrowsAsync<ArgumentException>(() => cache.UpsertBatchAsync(Enumerable.Repeat(a, 129).ToArray(), generation, CancellationToken.None));
            await ThrowsAsync<OperationCanceledException>(() => cache.ClearAsync(new CancellationToken(true)));
            Assert((await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 2, "Invalid/canceled operation changed cache.");

            // Trigger abort occurs after earlier batch statements: both rows and trigger-maintained
            // accounting must roll back together, not leave the first new row committed.
            using (var connection = Open(path))
            {
                Execute(connection, "CREATE TRIGGER fail_cache_insert BEFORE INSERT ON translation_cache WHEN new.translated_text='fail' BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;");
            }
            var failed = new TranslationCacheEntry(c.Key, "fail", clock.UtcNow, clock.UtcNow);
            var before = await cache.ExportContentAsync(100, true, CancellationToken.None);
            await ThrowsAsync<CacheStorageException>(() => cache.UpsertBatchAsync(new[] { b, failed }, generation, CancellationToken.None));
            var after = await cache.ExportContentAsync(100, true, CancellationToken.None);
            Assert(before.SequenceEqual(after), "Failed batch did not roll back rows/touches.");
            var rolledBack = await cache.GetStatisticsAsync(CancellationToken.None);
            Assert(rolledBack.Rows == after.Count && rolledBack.Bytes == after.Sum(entry => entry.SizeBytes),
                "Failed batch did not roll back trigger accounting.");
            using (var connection = Open(path))
            { Execute(connection, "DROP TRIGGER fail_cache_insert;"); }

            await second.ClearAsync(CancellationToken.None);
            Assert(await cache.UpsertBatchAsync(new[] { a }, generation, CancellationToken.None) == 0, "Late write repopulated cleared cache.");
            generation = (await cache.FindAsync(a.Key.Value, CancellationToken.None)).Generation;
            Assert(generation > hit.Generation, "Clear generation was not persisted.");
            await cache.ConfigureAsync(new TranslationCachePolicy(100, a.SizeBytes + b.SizeBytes - 1), CancellationToken.None);
            generation = (await cache.FindAsync(a.Key.Value, CancellationToken.None)).Generation;
            await second.UpsertBatchAsync(new[] { a, b }, generation, CancellationToken.None);
            var stats = await second.GetStatisticsAsync(CancellationToken.None);
            Assert(stats.Rows == 1 && stats.Bytes <= a.SizeBytes + b.SizeBytes - 1, "Shared byte policy was not enforced.");
            await second.ConfigureAsync(new TranslationCachePolicy(0), CancellationToken.None);
            Assert((await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 0, "Disabled policy did not clear storage.");
            Assert(await cache.UpsertBatchAsync(new[] { c }, generation, CancellationToken.None) == 0, "Old instance ignored disabled policy.");
            var disabledGeneration = (await cache.FindAsync(c.Key.Value, CancellationToken.None)).Generation;
            Assert(await cache.UpsertBatchAsync(new[] { c }, disabledGeneration, CancellationToken.None) == 0,
                "A current-generation write ignored the persisted disabled policy.");

            await cache.ConfigureAsync(new TranslationCachePolicy(100, 100000, TimeSpan.FromDays(1), 2), CancellationToken.None);
            generation = (await cache.FindAsync(a.Key.Value, CancellationToken.None)).Generation;
            await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
                (index % 2 == 0 ? cache : second).UpsertBatchAsync(new[] { Entry("parallel-" + index, clock.UtcNow) }, generation, CancellationToken.None)));
            Assert((await cache.GetStatisticsAsync(CancellationToken.None)).Rows == 12, "Parallel writes were lost.");
            await ThrowsAsync<InvalidOperationException>(() => cache.ExportContentAsync(10, false, CancellationToken.None));
            Assert((await cache.ExportContentAsync(3, true, CancellationToken.None)).Count == 3, "Export bound ignored.");
            clock.UtcNow = clock.UtcNow.AddDays(2);
            Assert((await cache.ExportContentAsync(100, true, CancellationToken.None)).Count == 0, "Expired cache text was exported.");
            Assert(await cache.PruneAsync(CancellationToken.None) == 2, "Expiry pruning was not bounded.");
            Assert((await cache.FindAsync(Entry("parallel-11", clock.UtcNow).Key.Value, CancellationToken.None)).Entry is null,
                "Expired cache entry was returned.");
            await cache.ClearAsync(CancellationToken.None);
            using (var connection = Open(path))
            {
                Assert(Scalar(connection, "SELECT row_count FROM cache_state;") == 0 && Scalar(connection, "SELECT total_bytes FROM cache_state;") == 0,
                    "Trigger accounting did not return to zero.");
                using var transaction = connection.BeginTransaction();
                await ThrowsAsync<CacheStorageException>(() => cache.FindAsync(a.Key.Value, CancellationToken.None));
                using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                await ThrowsAsync<OperationCanceledException>(() => cache.FindAsync(a.Key.Value, cancellation.Token));
            }
            var corruptPath = Path.Combine(directory, "corrupt.db");
            await cache.ConfigureAsync(new TranslationCachePolicy(10_000, 100_000_000), CancellationToken.None);
            generation = (await cache.FindAsync(a.Key.Value, CancellationToken.None)).Generation;
            for (var batch = 0; batch < 8; batch++)
            {
                await cache.UpsertBatchAsync(Enumerable.Range(batch * 128, 128).Select(index => Entry("benchmark-" + index, clock.UtcNow)).ToArray(),
                    generation, CancellationToken.None);
            }
            var benchmarkKey = Entry("benchmark-500", clock.UtcNow).Key.Value;
            var samples = new double[200];
            for (var index = -20; index < samples.Length; index++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                Assert((await cache.FindAsync(benchmarkKey, CancellationToken.None)).Entry is not null, "SQLite benchmark lookup missed.");
                if (index >= 0)
                { samples[index] = watch.Elapsed.TotalMilliseconds; }
            }
            Array.Sort(samples);
            Console.WriteLine($"SQLite lookup/touch benchmark: 1,024 rows, 200 hits, p50={samples[100]:F2} ms, p95={samples[190]:F2} ms (includes connection/transaction).");
            await File.WriteAllTextAsync(corruptPath, "corrupt database");
            using var corrupt = new SqliteDatabase(corruptPath);
            await ThrowsAsync<CacheStorageException>(() => new SqliteTranslationCacheRepository(corrupt, clock).FindAsync(a.Key.Value, CancellationToken.None));
            await TestGatewayAsync(cache, new SqliteTranslationCacheRepository(corrupt, clock), clock, path);
            Console.WriteLine("SQLite cache: persistence, shared policy, exact lookup/touch, LRU/bytes/age, rollback, generation clear, concurrency, busy/corrupt and export bounds passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static async Task TestGatewayAsync(SqliteTranslationCacheRepository healthy, ITranslationCacheRepository corrupt,
        IClock clock, string path)
    {
        const string reference = "synthetic-credential-reference-not-for-storage";
        var identity = TranslationCacheIdentity.FromOptions(new ProviderOptions
        { Id = "fake", Model = "model", SecretReference = reference });
        var request = new TranslationRequest(new PipelineMetadata(SessionId.New(), new SequenceNumber(1),
            clock.UtcNow, CorrelationId.New()), "ja", "en", "gateway persistence fixture", null, null, "model");
        var provider = new Provider();
        await using (var gateway = new CachedTranslationProvider(provider, healthy, clock, identity))
        {
            var first = await gateway.TranslateAsync(request, CancellationToken.None);
            var second = await gateway.TranslateAsync(request, CancellationToken.None);
            Assert(first.IsSuccess && second.IsSuccess && second.Value!.FromCache && provider.Calls == 1,
                "Real SQLite gateway did not persist and reuse a successful translation.");
            var exported = System.Text.Json.JsonSerializer.Serialize(await healthy.ExportContentAsync(1000, true, CancellationToken.None));
            Assert(exported.Contains(request.Text, StringComparison.Ordinal) &&
                !exported.Contains(reference, StringComparison.Ordinal), "Cache export leaked a credential reference.");
            foreach (var file in new[] { path, path + "-wal" })
            {
                if (File.Exists(file))
                {
                    var bytes = await File.ReadAllBytesAsync(file);
                    Assert(!Encoding.UTF8.GetString(bytes).Contains(reference, StringComparison.Ordinal),
                        "Database/WAL contains an unhashed credential reference.");
                }
            }
        }
        await using var degraded = new CachedTranslationProvider(provider, corrupt, clock, identity);
        var result = await degraded.TranslateAsync(request, CancellationToken.None);
        Assert(result.IsSuccess && !result.Value!.FromCache && degraded.PersistenceFailures == 1 && provider.Calls == 2,
            "Actual corrupt SQLite storage prevented the provider from delivering subtitles.");
        await ThrowsAsync<OperationCanceledException>(() => degraded.TranslateAsync(request, new CancellationToken(true)));
        Console.WriteLine("SQLite gateway: real persisted hit, credential-reference absence in export/database/WAL, corrupt-storage fallback and cancellation passed.");
    }
    private sealed class Provider : ITranslationProvider
    {
        public string Id => "fake";
        public int Calls { get; private set; }
        public Task<OperationResult<TranslationResult>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(OperationResult.Success(new TranslationResult(request.Metadata, request.Text,
                "gateway translated fixture", Id, request.Model, TimeSpan.Zero, false, true)));
        }
    }
    private static TranslationCacheEntry Entry(string text, DateTimeOffset now)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new TranslationCacheEntry(new TranslationCacheKey(hash, "fake", "model", "ja", "en", "prompt", "glossary", hash, text),
            "translated " + text, now, now);
    }
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static void Execute(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static long Scalar(SqliteConnection connection, string sql)
    { using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!; }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero); }
}

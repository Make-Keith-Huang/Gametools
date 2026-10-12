using System.Globalization;
using System.Text.Json;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameLingoAI.Infrastructure.Persistence;

public sealed class SqliteTranslationCacheRepository(SqliteDatabase database, IClock clock) : ITranslationCacheRepository
{
    public const int MaximumWriteBatch = 128;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };
    private long _hits, _misses, _writes, _evictions;

    public Task<TranslationCacheRead> FindAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        return RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var state = ReadState(connection, transaction);
            TranslationCacheEntry? entry = null;
            if (state.Policy.MaximumRows > 0)
            {
                using var command = SqliteDatabase.Command(connection, transaction,
                    "SELECT * FROM translation_cache WHERE cache_key=$key;", ("$key", key));
                using var reader = command.ExecuteReader();
                if (reader.Read())
                { entry = ReadEntry(reader); }
            }
            var removed = 0;
            if (entry is not null && IsExpired(entry, state.Policy))
            { removed = Delete(connection, transaction, key); entry = null; }
            if (entry is not null)
            {
                entry = entry.Touch(clock.UtcNow.ToUniversalTime());
                SqliteDatabase.Execute(connection, transaction, """
                    UPDATE translation_cache SET last_used_utc=$utc,hit_count=$hits,access_order=$order WHERE cache_key=$key;
                    """, ("$utc", Utc(entry.LastUsedUtc)), ("$hits", entry.HitCount),
                    ("$order", NextAccess(connection, transaction)), ("$key", key));
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            if (entry is null)
            { _misses++; }
            else
            { _hits++; }
            _evictions += removed;
            return new TranslationCacheRead(entry, state.Generation);
        }, cancellationToken);
    }

    public Task<int> UpsertBatchAsync(IReadOnlyList<TranslationCacheEntry> entries, long expectedGeneration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = entries.Take(MaximumWriteBatch + 1).ToArray();
        if (snapshot.Length > MaximumWriteBatch || snapshot.Any(entry => entry is null))
        { throw new ArgumentException("Cache batch is invalid or exceeds 128 entries.", nameof(entries)); }
        foreach (var entry in snapshot)
        { ValidateEntry(entry); }
        return RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var state = ReadState(connection, transaction);
            if (state.Generation != expectedGeneration || state.Policy.MaximumRows == 0)
            { return 0; }
            var removed = Prune(connection, transaction, state.Policy, token);
            var written = 0;
            foreach (var entry in snapshot)
            {
                token.ThrowIfCancellationRequested();
                if (entry.SizeBytes > state.Policy.MaximumBytes || IsExpired(entry, state.Policy))
                { continue; }
                Delete(connection, transaction, entry.Key.Value);
                for (var count = 0; count < state.Policy.PruneBatchSize; count++)
                {
                    var totals = Totals(connection, transaction);
                    if (totals.Rows < state.Policy.MaximumRows && totals.Bytes + entry.SizeBytes <= state.Policy.MaximumBytes)
                    { break; }
                    removed += DeleteOldest(connection, transaction);
                }
                var current = Totals(connection, transaction);
                if (current.Rows >= state.Policy.MaximumRows || current.Bytes + entry.SizeBytes > state.Policy.MaximumBytes)
                { continue; }
                SqliteDatabase.Execute(connection, transaction, """
                    INSERT INTO translation_cache(cache_key,provider_id,model,source_language,target_language,glossary_revision,
                      prompt_revision,source_hash,source_text,translated_text,created_utc,last_used_utc,hit_count,size_bytes,access_order)
                    VALUES($key,$provider,$model,$sourceLanguage,$targetLanguage,$glossary,$prompt,$hash,$source,$translated,$created,$used,$hits,$bytes,$order);
                    """, ("$key", entry.Key.Value), ("$provider", entry.Key.ProviderId), ("$model", entry.Key.Model),
                    ("$sourceLanguage", entry.Key.SourceLanguage), ("$targetLanguage", entry.Key.TargetLanguage),
                    ("$glossary", entry.Key.GlossaryRevision), ("$prompt", entry.Key.PromptRevision), ("$hash", entry.Key.SourceHash),
                    ("$source", entry.Key.NormalizedSource), ("$translated", entry.TranslatedText), ("$created", Utc(entry.CreatedUtc)),
                    ("$used", Utc(entry.LastUsedUtc)), ("$hits", entry.HitCount), ("$bytes", entry.SizeBytes),
                    ("$order", NextAccess(connection, transaction)));
                written++;
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            _writes += written;
            _evictions += removed;
            return written;
        }, cancellationToken);
    }

    public Task<int> PruneAsync(CancellationToken cancellationToken) => RunAsync(true, (connection, token) =>
    {
        using var transaction = connection.BeginTransaction();
        var count = Prune(connection, transaction, ReadState(connection, transaction).Policy, token);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        _evictions += count;
        return count;
    }, cancellationToken);

    public async Task ConfigureAsync(TranslationCachePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var changed = await RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            if (ReadState(connection, transaction).Policy == policy)
            { return false; }
            SqliteDatabase.Execute(connection, transaction, """
                UPDATE cache_state SET policy_json=$policy,generation=generation+1 WHERE id=1;
                """, ("$policy", JsonSerializer.Serialize(new StoredPolicy(policy.MaximumRows, policy.MaximumBytes,
                    policy.MaximumAge, policy.PruneBatchSize, policy.MaximumInFlight, policy.MaximumWaitersPerKey), JsonOptions)));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken).ConfigureAwait(false);
        // Once policy commits, finish cleanup in yielding batches. Each batch rereads the
        // persisted policy so another instance cannot accidentally restore an older limit.
        if (changed)
        {
            while (await PruneAsync(CancellationToken.None).ConfigureAwait(false) > 0)
            { await Task.Yield(); }
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken) => RunAsync(true, (connection, token) =>
    {
        using var transaction = connection.BeginTransaction();
        SqliteDatabase.Execute(connection, transaction, "DELETE FROM translation_cache;");
        SqliteDatabase.Execute(connection, transaction, "UPDATE cache_state SET generation=generation+1 WHERE id=1;");
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }, cancellationToken);

    public Task<TranslationCacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken) => RunAsync(false, (connection, token) =>
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        token.ThrowIfCancellationRequested();
        var totals = Totals(connection, transaction);
        return new TranslationCacheStatistics(totals.Rows, totals.Bytes, _hits, _misses, _writes, _evictions, ReadState(connection, transaction).Generation);
    }, cancellationToken);

    public Task<IReadOnlyList<TranslationCacheEntry>> ExportContentAsync(int maximumEntries, bool userRequested, CancellationToken cancellationToken)
    {
        if (!userRequested)
        { throw new InvalidOperationException("Cache text export requires an explicit user action."); }
        if (maximumEntries is < 1 or > 1000)
        { throw new ArgumentOutOfRangeException(nameof(maximumEntries)); }
        return RunAsync<IReadOnlyList<TranslationCacheEntry>>(false, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var policy = ReadState(connection, transaction).Policy;
            if (policy.MaximumRows == 0)
            { return Array.Empty<TranslationCacheEntry>(); }
            using var command = SqliteDatabase.Command(connection, transaction, """
                SELECT * FROM translation_cache WHERE created_utc > $cutoff ORDER BY access_order DESC LIMIT $limit;
                """, ("$cutoff", Cutoff(policy)), ("$limit", maximumEntries));
            using var reader = command.ExecuteReader();
            var entries = new List<TranslationCacheEntry>();
            while (reader.Read())
            { token.ThrowIfCancellationRequested(); entries.Add(ReadEntry(reader)); }
            return entries.AsReadOnly();
        }, cancellationToken);
    }

    private int Prune(SqliteConnection connection, SqliteTransaction transaction, TranslationCachePolicy policy, CancellationToken token)
    {
        var removed = SqliteDatabase.Execute(connection, transaction, """
            DELETE FROM translation_cache WHERE cache_key IN (
              SELECT cache_key FROM translation_cache WHERE created_utc <= $cutoff ORDER BY created_utc LIMIT $limit);
            """, ("$cutoff", Cutoff(policy)), ("$limit", policy.PruneBatchSize));
        while (removed < policy.PruneBatchSize)
        {
            token.ThrowIfCancellationRequested();
            var totals = Totals(connection, transaction);
            if (totals.Rows <= policy.MaximumRows && totals.Bytes <= policy.MaximumBytes)
            { break; }
            var count = DeleteOldest(connection, transaction);
            if (count == 0)
            { throw new CacheStorageException(CacheStorageFailureKind.Corrupt); }
            removed += count;
        }
        return removed;
    }

    private static int DeleteOldest(SqliteConnection connection, SqliteTransaction transaction) => SqliteDatabase.Execute(connection, transaction,
        "DELETE FROM translation_cache WHERE cache_key=(SELECT cache_key FROM translation_cache ORDER BY access_order LIMIT 1);");
    private static int Delete(SqliteConnection connection, SqliteTransaction transaction, string key) => SqliteDatabase.Execute(connection, transaction,
        "DELETE FROM translation_cache WHERE cache_key=$key;", ("$key", key));
    private static long NextAccess(SqliteConnection connection, SqliteTransaction transaction)
    {
        SqliteDatabase.Execute(connection, transaction, "UPDATE cache_state SET access_sequence=access_sequence+1 WHERE id=1;");
        return Convert.ToInt64(SqliteDatabase.Scalar(connection, transaction, "SELECT access_sequence FROM cache_state WHERE id=1;"), CultureInfo.InvariantCulture);
    }
    private static (int Rows, long Bytes) Totals(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = SqliteDatabase.Command(connection, transaction, "SELECT row_count,total_bytes FROM cache_state WHERE id=1;");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        { throw new CacheStorageException(CacheStorageFailureKind.Corrupt); }
        return (reader.GetInt32(0), reader.GetInt64(1));
    }
    private static (long Generation, TranslationCachePolicy Policy) ReadState(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = SqliteDatabase.Command(connection, transaction, "SELECT generation,policy_json FROM cache_state WHERE id=1;");
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        { throw new CacheStorageException(CacheStorageFailureKind.Corrupt); }
        var json = reader.GetString(1);
        if (json.Length > 16_384)
        { throw new CacheStorageException(CacheStorageFailureKind.Corrupt); }
        var stored = JsonSerializer.Deserialize<StoredPolicy>(json, JsonOptions) ?? throw new JsonException();
        return (reader.GetInt64(0), new TranslationCachePolicy(stored.MaximumRows, stored.MaximumBytes, stored.MaximumAge,
            stored.PruneBatchSize, stored.MaximumInFlight, stored.MaximumWaitersPerKey));
    }
    private static TranslationCacheEntry ReadEntry(SqliteDataReader reader)
    {
        string Text(string name) => reader.GetString(reader.GetOrdinal(name));
        var entry = new TranslationCacheEntry(new TranslationCacheKey(Text("cache_key"), Text("provider_id"), Text("model"),
            Text("source_language"), Text("target_language"), Text("prompt_revision"), Text("glossary_revision"), Text("source_hash"), Text("source_text")),
            Text("translated_text"), DateTimeOffset.ParseExact(Text("created_utc"), "O", CultureInfo.InvariantCulture),
            DateTimeOffset.ParseExact(Text("last_used_utc"), "O", CultureInfo.InvariantCulture), reader.GetInt64(reader.GetOrdinal("hit_count")));
        ValidateEntry(entry);
        if (entry.SizeBytes != reader.GetInt64(reader.GetOrdinal("size_bytes")))
        { throw new CacheStorageException(CacheStorageFailureKind.Corrupt); }
        return entry;
    }
    private static void ValidateEntry(TranslationCacheEntry entry)
    {
        ValidateKey(entry.Key.Value);
        if (entry.Key.ProviderId.Length > 200 || entry.Key.Model.Length > 512 || entry.Key.SourceLanguage.Length > 32 ||
            entry.Key.TargetLanguage.Length > 32 || entry.Key.PromptRevision.Length > 512 || entry.Key.GlossaryRevision.Length > 512 ||
            entry.Key.SourceHash.Length > 128)
        { throw new ArgumentException("Cache metadata exceeds supported bounds.", nameof(entry)); }
    }
    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length != 64 || key.Any(character => !char.IsAsciiHexDigit(character)))
        { throw new ArgumentException("Cache key must be a SHA-256 hexadecimal value.", nameof(key)); }
    }
    private bool IsExpired(TranslationCacheEntry entry, TranslationCachePolicy policy) => clock.UtcNow - entry.CreatedUtc >= policy.MaximumAge;
    private string Cutoff(TranslationCachePolicy policy) => Utc(clock.UtcNow.ToUniversalTime() - policy.MaximumAge);
    private static string Utc(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private sealed record StoredPolicy(int MaximumRows = 100_000, long MaximumBytes = 500L * 1024 * 1024,
        TimeSpan? MaximumAge = null, int PruneBatchSize = 64, int MaximumInFlight = 32, int MaximumWaitersPerKey = 64);
    private async Task<T> RunAsync<T>(bool write, Func<SqliteConnection, CancellationToken, T> operation, CancellationToken cancellationToken)
    {
        try
        { return await database.RunAsync(write, operation, cancellationToken).ConfigureAwait(false); }
        catch (DatabaseStorageException error)
        {
            throw new CacheStorageException(error.Kind switch
            {
                DatabaseFailureKind.Busy => CacheStorageFailureKind.Busy,
                DatabaseFailureKind.Corrupt or DatabaseFailureKind.Incompatible => CacheStorageFailureKind.Corrupt,
                _ => CacheStorageFailureKind.Unavailable
            });
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or OverflowException)
        { throw new CacheStorageException(CacheStorageFailureKind.Corrupt); }
    }
}

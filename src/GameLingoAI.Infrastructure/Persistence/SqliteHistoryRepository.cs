using System.Globalization;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameLingoAI.Infrastructure.Persistence;

/// <summary>History is off on construction and requires explicit runtime configuration to write.</summary>
public sealed class SqliteHistoryRepository(SqliteDatabase database, IClock clock) : ITranslationHistoryRepository
{
    private HistoryPolicy _policy = new();

    public Task ConfigureAsync(HistoryPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return database.RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var removed = Prune(connection, transaction, policy);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            _policy = policy;
            return removed;
        }, cancellationToken);
    }

    public Task<bool> AppendAsync(TranslationHistoryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return database.RunAsync(true, (connection, token) =>
        {
            if (!_policy.Enabled || entry.ObservedUtc <= clock.UtcNow.AddDays(-_policy.RetentionDays))
            { return false; }
            using var transaction = connection.BeginTransaction();
            Prune(connection, transaction);
            SqliteDatabase.Execute(connection, transaction, """
                INSERT INTO translation_history(session_id,profile_id,source_text,translated_text,observed_utc,latency_ms)
                VALUES($session,$profile,$source,$translated,$utc,$latency);
                """, ("$session", entry.SessionId.Value.ToString()), ("$profile", entry.ProfileId?.Value.ToString()),
                ("$source", entry.SourceText), ("$translated", entry.TranslatedText),
                ("$utc", entry.ObservedUtc.ToString("O", CultureInfo.InvariantCulture)), ("$latency", entry.LatencyMilliseconds));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task<int> PruneAsync(CancellationToken cancellationToken) =>
        database.RunAsync(true, (connection, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Prune(connection, null);
        }, cancellationToken);

    public Task<IReadOnlyList<TranslationHistoryEntry>> ListAsync(int maximumEntries, bool userRequested, CancellationToken cancellationToken)
    {
        if (!userRequested)
        { throw new InvalidOperationException("History access requires an explicit user action."); }
        if (maximumEntries is < 1 or > 1000)
        { throw new ArgumentOutOfRangeException(nameof(maximumEntries)); }
        return database.RunAsync<IReadOnlyList<TranslationHistoryEntry>>(false, (connection, token) =>
        {
            var entries = new List<TranslationHistoryEntry>();
            using var command = SqliteDatabase.Command(connection, null, """
                SELECT session_id,profile_id,source_text,translated_text,observed_utc,latency_ms
                FROM translation_history WHERE observed_utc > $cutoff ORDER BY observed_utc DESC,id DESC LIMIT $limit;
                """, ("$cutoff", Cutoff()), ("$limit", maximumEntries));
            using var reader = command.ExecuteReader();
            try
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    entries.Add(new TranslationHistoryEntry(new SessionId(Guid.Parse(reader.GetString(0))),
                        reader.IsDBNull(1) ? null : new ProfileId(Guid.Parse(reader.GetString(1))), reader.GetString(2), reader.GetString(3),
                        DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture), reader.GetInt64(5)));
                }
                return entries.AsReadOnly();
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or OverflowException)
            { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); }
        }, cancellationToken);
    }

    private string Cutoff(HistoryPolicy? policy = null) => clock.UtcNow.ToUniversalTime().AddDays(-(policy ?? _policy).RetentionDays).ToString("O", CultureInfo.InvariantCulture);
    private int Prune(SqliteConnection connection, SqliteTransaction? transaction, HistoryPolicy? policy = null) =>
        SqliteDatabase.Execute(connection, transaction, """
            DELETE FROM translation_history WHERE id IN (
              SELECT id FROM translation_history WHERE observed_utc <= $cutoff ORDER BY observed_utc,id LIMIT $batch);
            """, ("$cutoff", Cutoff(policy)), ("$batch", (policy ?? _policy).PruneBatchSize));
}

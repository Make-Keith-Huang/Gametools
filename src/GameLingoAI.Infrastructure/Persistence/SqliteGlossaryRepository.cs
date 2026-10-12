using System.Globalization;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using Microsoft.Data.Sqlite;

namespace GameLingoAI.Infrastructure.Persistence;

public sealed class SqliteGlossaryRepository(SqliteDatabase database, IClock clock) : IGlossaryRepository
{
    public Task ReplaceAsync(IReadOnlyList<GlossaryEntry> entries, IReadOnlyList<GlossaryEntry> expectedEntries, CancellationToken cancellationToken)
    {
        var snapshot = new GlossaryCatalog(entries).Entries;
        var expected = new GlossaryCatalog(expectedEntries).Entries.OrderBy(entry => entry.Id).ToArray();
        return database.RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var current = new List<GlossaryEntry>();
            using (var command = SqliteDatabase.Command(connection, transaction, """
                SELECT id,profile_id,source_language,target_language,source_term,target_term,case_sensitive,enabled
                FROM glossary_entries LIMIT $limit;
                """, ("$limit", GlossaryCatalog.MaximumEntries + 1)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    current.Add(new GlossaryEntry(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : new ProfileId(Guid.Parse(reader.GetString(1))),
                        reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7)));
                }
            }
            if (!current.OrderBy(entry => entry.Id).SequenceEqual(expected))
            { throw new InvalidOperationException("Stored glossary changed. Export your draft, then reload and merge before saving."); }
            SqliteDatabase.Execute(connection, transaction, "DELETE FROM glossary_entries;");
            foreach (var entry in snapshot)
            {
                token.ThrowIfCancellationRequested();
                SaveEntry(connection, transaction, entry);
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }
    public Task<IReadOnlyList<GlossaryEntry>> ListAsync(CancellationToken cancellationToken) =>
        database.RunAsync<IReadOnlyList<GlossaryEntry>>(false, (connection, token) =>
        {
            var entries = new List<GlossaryEntry>();
            using var command = SqliteDatabase.Command(connection, null, """
                SELECT id,profile_id,source_language,target_language,source_term,target_term,case_sensitive,enabled
                FROM glossary_entries ORDER BY id LIMIT $limit;
                """, ("$limit", GlossaryCatalog.MaximumEntries + 1));
            using var reader = command.ExecuteReader();
            try
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    entries.Add(new GlossaryEntry(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : new ProfileId(Guid.Parse(reader.GetString(1))),
                        reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetBoolean(6), reader.GetBoolean(7)));
                }
                return new GlossaryCatalog(entries).Entries;
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or OverflowException)
            { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); }
        }, cancellationToken);

    public Task SaveAsync(GlossaryEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return database.RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var exists = Convert.ToInt64(SqliteDatabase.Scalar(connection, transaction,
                "SELECT COUNT(*) FROM glossary_entries WHERE id=$id;", ("$id", entry.Id.ToString())), CultureInfo.InvariantCulture) != 0;
            if (!exists && Convert.ToInt64(SqliteDatabase.Scalar(connection, transaction,
                "SELECT COUNT(*) FROM glossary_entries;"), CultureInfo.InvariantCulture) >= GlossaryCatalog.MaximumEntries)
            { throw new InvalidOperationException("The glossary entry limit has been reached."); }
            SaveEntry(connection, transaction, entry);
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    private void SaveEntry(SqliteConnection connection, SqliteTransaction transaction, GlossaryEntry entry) =>
        SqliteDatabase.Execute(connection, transaction, """
                INSERT INTO glossary_entries(id,profile_id,source_language,target_language,source_term,target_term,case_sensitive,enabled,updated_utc)
                VALUES($id,$profile,$sourceLanguage,$targetLanguage,$source,$target,$caseSensitive,$enabled,$utc)
                ON CONFLICT(id) DO UPDATE SET profile_id=excluded.profile_id,source_language=excluded.source_language,
                  target_language=excluded.target_language,source_term=excluded.source_term,target_term=excluded.target_term,
                  case_sensitive=excluded.case_sensitive,enabled=excluded.enabled,updated_utc=excluded.updated_utc;
                """, ("$id", entry.Id.ToString()), ("$profile", entry.ProfileId?.Value.ToString()),
                ("$sourceLanguage", entry.SourceLanguage), ("$targetLanguage", entry.TargetLanguage),
                ("$source", entry.SourceTerm), ("$target", entry.TargetTerm), ("$caseSensitive", entry.CaseSensitive),
                ("$enabled", entry.Enabled), ("$utc", clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        return database.RunAsync(true, (connection, token) =>
        {
            token.ThrowIfCancellationRequested();
            return SqliteDatabase.Execute(connection, null, "DELETE FROM glossary_entries WHERE id=$id;", ("$id", id.ToString()));
        }, cancellationToken);
    }
}

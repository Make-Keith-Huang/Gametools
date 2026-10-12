using System.Globalization;
using GameLingoAI.Core.Models;
using Microsoft.Data.Sqlite;

namespace GameLingoAI.Infrastructure.Persistence;

/// <summary>
/// One bounded, off-UI-thread operation at a time per database instance. Operation connections and
/// transactions never escape an operation. An idle read-only connection retains WAL state.
/// Owners must drain operations before disposal.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetime = new();
    private readonly string _path;
    private readonly DatabaseMigration[] _migrations;
    private int _pending;
    private bool _disposed;
    private bool _stopping;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DatabaseStatus? _status;
    private SqliteConnection? _keepAlive;

    public SqliteDatabase(string? path = null, IReadOnlyList<DatabaseMigration>? migrations = null)
    {
        _path = Path.GetFullPath(path ?? Configuration.AppDataPaths.Database);
        _migrations = (migrations ?? DatabaseMigrations.All).ToArray();
        if (_migrations.Length == 0 || _migrations.Where((migration, index) =>
            migration.Version != index + 1 || string.IsNullOrWhiteSpace(migration.Name) ||
            string.IsNullOrWhiteSpace(migration.UpSql) || string.IsNullOrWhiteSpace(migration.CompatibilitySql)).Any())
        { throw new ArgumentException("Migrations must be contiguous, named and include compatibility checks.", nameof(migrations)); }
    }

    public Task<DatabaseStatus> InitializeAsync(CancellationToken cancellationToken) => ScheduleAsync(() =>
    {
        if (_status?.IsWritable != true)
        { _status = InitializeCore(cancellationToken); }
        return _status;
    }, cancellationToken);

    /// <summary>Reject new operations and await every admitted operation before the owner disposes services.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifetime)
        {
            _stopping = true;
            if (_pending == 0)
            { _drained.TrySetResult(); }
            return _drained.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Deletes only cache/history after the caller has obtained exclusive session-maintenance
    /// admission and verified a stopped pipeline. Recovery/backup copies are not deleted.
    /// </summary>
    public Task<PrivateDataClearResult> ClearPrivateDataAsync(bool userConfirmed, bool pipelineStopped, CancellationToken cancellationToken)
    {
        if (!userConfirmed || !pipelineStopped)
        { throw new InvalidOperationException("Stop the pipeline and explicitly confirm private-data deletion first."); }
        return RunAsync(true, (connection, token) =>
        {
            Execute(connection, null, "PRAGMA secure_delete=ON;");
            using (var transaction = connection.BeginTransaction())
            {
                Execute(connection, transaction, "DELETE FROM translation_history;");
                Execute(connection, transaction, "DELETE FROM translation_cache;");
                Execute(connection, transaction, "UPDATE cache_state SET generation=generation+1 WHERE id=1;");
                token.ThrowIfCancellationRequested();
                transaction.Commit();
            }
            // Deletion has committed. Do not claim cancellation/rollback now; report any
            // space-reclamation failure separately, and keep the logical clear successful.
            try
            {
                Execute(connection, null, "VACUUM;");
                using var checkpoint = Command(connection, null, "PRAGMA wal_checkpoint(TRUNCATE);");
                using var reader = checkpoint.ExecuteReader();
                var reclaimed = reader.Read() && reader.GetInt32(0) == 0;
                return new PrivateDataClearResult(reclaimed);
            }
            catch (SqliteException) { return new PrivateDataClearResult(false); }
        }, cancellationToken);
    }

    /// <summary>Explicit local recovery copy; does not migrate, reset or write the source database.</summary>
    public Task ExportRecoveryCopyAsync(string destination, bool userRequested, CancellationToken cancellationToken)
    {
        if (!userRequested)
        { throw new InvalidOperationException("Recovery export requires an explicit user action."); }
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var fullDestination = Path.GetFullPath(destination);
        if (string.Equals(fullDestination, _path, StringComparison.OrdinalIgnoreCase))
        { throw new ArgumentException("Recovery destination must differ from the source.", nameof(destination)); }
        return ScheduleAsync(() =>
        {
            using var source = Open(readOnly: true);
            cancellationToken.ThrowIfCancellationRequested();
            // Reserve without replacing any existing user file. Failed partial copies remain
            // available for inspection; the original database is never deleted or reset.
            using (new FileStream(fullDestination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { }
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fullDestination, Pooling = false }.ToString());
            target.Open();
            source.BackupDatabase(target);
            return true;
        }, cancellationToken);
    }

    internal Task<T> RunAsync<T>(bool write, Func<SqliteConnection, CancellationToken, T> operation,
        CancellationToken cancellationToken) => ScheduleAsync(() =>
    {
        _status ??= InitializeCore(cancellationToken);
        if (write && !_status.IsWritable)
        { throw new DatabaseStorageException(_status.Failure ?? DatabaseFailureKind.Unavailable); }
        // Keep WAL shared state alive between short-lived operations. This connection
        // executes no transactions, so it does not pin a read snapshot or block pruning.
        _keepAlive ??= Open(readOnly: true);
        using var connection = Open(readOnly: !write);
        return operation(connection, cancellationToken);
    }, cancellationToken);

    private async Task<T> ScheduleAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed || _stopping, this);
            if (_pending >= 32)
            { throw new DatabaseStorageException(DatabaseFailureKind.Busy); }
            _pending++;
        }
        var entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                { return action(); }
                catch (SqliteException error) { cancellationToken.ThrowIfCancellationRequested(); throw Classify(error); }
                catch (IOException) { throw new DatabaseStorageException(DatabaseFailureKind.Unavailable); }
                catch (UnauthorizedAccessException) { throw new DatabaseStorageException(DatabaseFailureKind.Unavailable); }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entered)
            { _gate.Release(); }
            lock (_lifetime)
            {
                _pending--;
                if (_stopping && _pending == 0)
                { _drained.TrySetResult(); }
            }
        }
    }

    private DatabaseStatus InitializeCore(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var connection = Open(readOnly: false);
            if (!string.Equals(Convert.ToString(Scalar(connection, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture),
                "ok", StringComparison.Ordinal))
            { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); }

            // Inspect before WAL or schema mutation: a newer database must remain untouched.
            InspectVersions(connection, null);
            if (!string.Equals(Convert.ToString(Scalar(connection, null, "PRAGMA journal_mode=WAL;"), CultureInfo.InvariantCulture),
                "wal", StringComparison.OrdinalIgnoreCase))
            { throw new DatabaseStorageException(DatabaseFailureKind.Unavailable); }

            using var transaction = connection.BeginTransaction();
            var applied = InspectVersions(connection, transaction);
            if (_migrations.Skip(applied).Any(migration => migration.RequiresBackup))
            {
                // A separate read connection sees the pre-migration committed snapshot while
                // the immediate transaction prevents another writer from changing it.
                var backupPath = _path + ".before-migration-" + Guid.NewGuid().ToString("N") + ".db";
                using var source = Open(readOnly: true);
                using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = backupPath, Pooling = false }.ToString());
                backup.Open();
                source.BackupDatabase(backup);
                cancellationToken.ThrowIfCancellationRequested();
            }
            Execute(connection, transaction, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                  version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);
                """);
            foreach (var migration in _migrations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (migration.Version > applied)
                {
                    Execute(connection, transaction, migration.UpSql);
                    Execute(connection, transaction,
                        "INSERT INTO schema_migrations(version,name,applied_utc) VALUES($version,$name,$utc);",
                        ("$version", migration.Version), ("$name", migration.Name),
                        ("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)));
                }
                if (Convert.ToInt64(Scalar(connection, transaction, migration.CompatibilitySql), CultureInfo.InvariantCulture) != 1)
                { throw new DatabaseStorageException(DatabaseFailureKind.Incompatible); }
            }
            using (var check = Command(connection, transaction, "PRAGMA foreign_key_check;"))
            using (var reader = check.ExecuteReader())
            { if (reader.Read()) { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); } }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new DatabaseStatus(true);
        }
        catch (SqliteException error) { cancellationToken.ThrowIfCancellationRequested(); return new DatabaseStatus(false, Classify(error).Kind); }
        catch (DatabaseStorageException error) { return new DatabaseStatus(false, error.Kind); }
        catch (IOException) { return new DatabaseStatus(false, DatabaseFailureKind.Unavailable); }
        catch (UnauthorizedAccessException) { return new DatabaseStatus(false, DatabaseFailureKind.Unavailable); }
    }

    private int InspectVersions(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (Convert.ToInt64(Scalar(connection, transaction,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_migrations';"), CultureInfo.InvariantCulture) == 0)
        {
            var tables = Convert.ToInt64(Scalar(connection, transaction,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';"), CultureInfo.InvariantCulture);
            if (tables != 0)
            { throw new DatabaseStorageException(DatabaseFailureKind.Incompatible); }
            return 0;
        }
        using var command = Command(connection, transaction, "SELECT version,name FROM schema_migrations ORDER BY version;");
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            var version = reader.GetInt32(0);
            if (version > _migrations.Length)
            { throw new DatabaseStorageException(DatabaseFailureKind.SchemaNewer); }
            if (version != count + 1 || reader.GetString(1) != _migrations[count].Name)
            { throw new DatabaseStorageException(DatabaseFailureKind.Incompatible); }
            count++;
        }
        return count;
    }

    private SqliteConnection Open(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 1
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, null, "PRAGMA busy_timeout=1000;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        { command.Parameters.AddWithValue(name, value ?? DBNull.Value); }
        return command;
    }

    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return command.ExecuteScalar();
    }

    public static DatabaseStorageException Classify(SqliteException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new DatabaseStorageException(error.SqliteErrorCode switch
        {
            5 or 6 => DatabaseFailureKind.Busy,
            11 or 26 => DatabaseFailureKind.Corrupt,
            13 => DatabaseFailureKind.DiskFull,
            1 or 19 => DatabaseFailureKind.Incompatible,
            _ => DatabaseFailureKind.Unavailable
        });
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed)
            { return; }
            if (_pending != 0)
            { throw new InvalidOperationException("Drain database operations before disposing the database."); }
            _disposed = true;
            _keepAlive?.Dispose();
            _gate.Dispose();
        }
    }
}

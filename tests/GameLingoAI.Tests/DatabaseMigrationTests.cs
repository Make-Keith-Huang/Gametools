using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

internal static class DatabaseMigrationTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-DatabaseTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "fresh.db");
            using (var database = new SqliteDatabase(path))
            {
                Assert((await database.InitializeAsync(CancellationToken.None)).IsWritable, "Fresh migration failed.");
                Assert((await database.InitializeAsync(CancellationToken.None)).IsWritable, "Migration was not idempotent.");
            }
            using (var connection = Open(path))
            {
                Assert(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations;") == DatabaseMigrations.All.Count, "Missing migration records.");
                Assert(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'ix_%';") == 8,
                    "Documented indexes are missing.");
                Assert(Scalar(connection, "PRAGMA foreign_keys;") == 1, "Foreign keys not enabled.");
                using var journal = connection.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode;";
                Assert((string?)journal.ExecuteScalar() == "wal", "Database is not WAL.");
            }

            var upgradePath = Path.Combine(directory, "upgrade.db");
            using (var old = new SqliteDatabase(upgradePath, DatabaseMigrations.All.Take(1).ToArray()))
            { Assert((await old.InitializeAsync(CancellationToken.None)).IsWritable, "V1 creation failed."); }
            using (var connection = Open(upgradePath))
            { Execute(connection, """
                INSERT INTO game_profiles(id,name,source_language,target_language,provider_id,overlay_json,created_utc,updated_utc)
                VALUES('retained','Keep this','ja','en','ollama','{}','2026-01-01','2026-01-01');
                """); }
            using (var upgraded = new SqliteDatabase(upgradePath))
            { Assert((await upgraded.InitializeAsync(CancellationToken.None)).IsWritable, "V1 upgrade failed."); }
            using (var connection = Open(upgradePath))
            {
                Assert(Scalar(connection, "SELECT COUNT(*) FROM game_profiles WHERE id='retained' AND history_enabled=0;") == 1,
                    "Upgrade lost profile or enabled history.");
            }

            var brokenMigrations = DatabaseMigrations.All.Concat(new[]
            {
                new DatabaseMigration(DatabaseMigrations.All.Count + 1, "deliberate-failure", "DELETE FROM game_profiles; SELECT * FROM missing_table;", "SELECT 1;", true)
            }).ToArray();
            using (var failing = new SqliteDatabase(upgradePath, brokenMigrations))
            {
                var status = await failing.InitializeAsync(CancellationToken.None);
                Assert(!status.IsWritable && status.Failure == DatabaseFailureKind.Incompatible, "Failed migration should enter recovery.");
            }
            using (var connection = Open(upgradePath))
            {
                Assert(Scalar(connection, "SELECT COUNT(*) FROM game_profiles WHERE id='retained';") == 1, "Failed migration lost data.");
                Assert(Scalar(connection, "SELECT MAX(version) FROM schema_migrations;") == DatabaseMigrations.All.Count, "Failed migration was committed.");
            }
            var backups = Directory.GetFiles(directory, "upgrade.db.before-migration-*.db");
            Assert(backups.Length == 1, "Destructive migration did not produce a backup.");
            using (var backup = Open(backups[0]))
            { Assert(Scalar(backup, "SELECT COUNT(*) FROM game_profiles WHERE id='retained';") == 1, "Backup did not preserve committed data."); }

            for (var version = 1; version < DatabaseMigrations.All.Count; version++)
            {
                var versionPath = Path.Combine(directory, $"version-{version}.db");
                using (var old = new SqliteDatabase(versionPath, DatabaseMigrations.All.Take(version).ToArray()))
                { Assert((await old.InitializeAsync(CancellationToken.None)).IsWritable, "Supported old schema could not be created."); }
                using (var upgraded = new SqliteDatabase(versionPath))
                { Assert((await upgraded.InitializeAsync(CancellationToken.None)).IsWritable, "A supported schema could not be upgraded."); }
                using var connection = Open(versionPath);
                Assert(Scalar(connection, "SELECT MAX(version) FROM schema_migrations;") == DatabaseMigrations.All.Count,
                    "Supported upgrade did not reach the current schema.");
            }

            using (var connection = Open(path))
            { Execute(connection, "INSERT INTO schema_migrations VALUES(999,'future','2026-01-01');"); }
            var before = await File.ReadAllBytesAsync(path);
            using (var newer = new SqliteDatabase(path))
            {
                var status = await newer.InitializeAsync(CancellationToken.None);
                Assert(status.Failure == DatabaseFailureKind.SchemaNewer && !status.IsWritable, "Future schema should be read-only.");
                var recoveryPath = Path.Combine(directory, "recovery.db");
                await newer.ExportRecoveryCopyAsync(recoveryPath, true, CancellationToken.None);
                using var recovered = Open(recoveryPath);
                Assert(Scalar(recovered, "SELECT MAX(version) FROM schema_migrations;") == 999,
                    "Recovery export must preserve the newer schema without migration.");
                try
                {
                    await newer.ExportRecoveryCopyAsync(Path.Combine(directory, "unrequested.db"), false, CancellationToken.None);
                    throw new InvalidOperationException("Unrequested recovery export succeeded.");
                }
                catch (InvalidOperationException error) when (error.Message.Contains("explicit user action", StringComparison.Ordinal)) { }
            }
            Assert(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "Newer database was rewritten.");

            var corruptPath = Path.Combine(directory, "corrupt.db");
            await File.WriteAllTextAsync(corruptPath, "not a database; private test content");
            using (var corrupt = new SqliteDatabase(corruptPath))
            { Assert((await corrupt.InitializeAsync(CancellationToken.None)).Failure == DatabaseFailureKind.Corrupt, "Corrupt database classification failed."); }
            Assert(await File.ReadAllTextAsync(corruptPath) == "not a database; private test content", "Corrupt file must not be silently reset.");

            var canceledPath = Path.Combine(directory, "canceled.db");
            using (var canceled = new SqliteDatabase(canceledPath))
            {
                try
                {
                    await canceled.InitializeAsync(new CancellationToken(true));
                    throw new InvalidOperationException("Canceled initialization succeeded.");
                }
                catch (OperationCanceledException) { }
            }
            Assert(!File.Exists(canceledPath), "Pre-canceled operation created a database.");

            foreach (var (code, expected) in new[]
            {
                (5, DatabaseFailureKind.Busy), (6, DatabaseFailureKind.Busy), (11, DatabaseFailureKind.Corrupt),
                (26, DatabaseFailureKind.Corrupt), (13, DatabaseFailureKind.DiskFull), (14, DatabaseFailureKind.Unavailable)
            })
            {
                var classified = SqliteDatabase.Classify(new SqliteException("private SQL and path", code));
                Assert(classified.Kind == expected && !classified.ToString().Contains("private SQL", StringComparison.Ordinal),
                    "Database errors must be classified and redact the original message.");
            }

            using (var locked = Open(upgradePath))
            using (var transaction = locked.BeginTransaction())
            using (var busy = new SqliteDatabase(upgradePath))
            { Assert((await busy.InitializeAsync(CancellationToken.None)).Failure == DatabaseFailureKind.Busy, "Real writer contention was not classified."); }
            Console.WriteLine("SQLite migrations: fresh, upgrade, idempotence, rollback/backup, future/corrupt preservation, cancellation and real busy lock passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, ForeignKeys = true, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

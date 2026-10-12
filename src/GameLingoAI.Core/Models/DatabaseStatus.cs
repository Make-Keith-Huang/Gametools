namespace GameLingoAI.Core.Models;

public enum DatabaseFailureKind { Busy, Corrupt, DiskFull, SchemaNewer, Incompatible, Unavailable }

/// <summary>Safe storage failure without SQL, paths, inner exceptions or stored text.</summary>
public sealed class DatabaseStorageException(DatabaseFailureKind kind) : Exception(MessageFor(kind))
{
    public DatabaseFailureKind Kind { get; } = kind;

    private static string MessageFor(DatabaseFailureKind kind) => kind switch
    {
        DatabaseFailureKind.Busy => "Database is busy. Close other instances and retry.",
        DatabaseFailureKind.Corrupt => "Database integrity check failed. Preserve the file and use read-only recovery.",
        DatabaseFailureKind.DiskFull => "Database storage is full. Free disk space and retry.",
        DatabaseFailureKind.SchemaNewer => "Database was created by a newer application. Update the app; do not reset the database.",
        DatabaseFailureKind.Incompatible => "Database migration failed. Existing data was preserved; use read-only recovery.",
        _ => "Database is unavailable. Check local storage permissions and retry."
    };
}

public sealed record DatabaseStatus(bool IsWritable, DatabaseFailureKind? Failure = null);

/// <summary>Logical deletion committed; false means physical compaction/checkpoint still needs retry.</summary>
public sealed record PrivateDataClearResult(bool SpaceReclaimed);

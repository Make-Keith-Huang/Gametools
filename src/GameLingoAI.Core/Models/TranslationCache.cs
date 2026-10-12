using System.Text;
using GameLingoAI.Core.Configuration;

namespace GameLingoAI.Core.Models;

/// <summary>Logical storage limits and bounded maintenance/admission limits, independent of history.</summary>
public sealed record TranslationCachePolicy
{
    public TranslationCachePolicy(int maximumRows = 100_000, long maximumBytes = 500L * 1024 * 1024,
        TimeSpan? maximumAge = null, int pruneBatchSize = 64, int maximumInFlight = 32,
        int maximumWaitersPerKey = 64)
    {
        var age = maximumAge ?? TimeSpan.FromDays(30);
        if (maximumRows is < 0 or > 1_000_000 || maximumBytes is < 1 or > 4_294_967_296 ||
            age <= TimeSpan.Zero || age > TimeSpan.FromDays(3_650) || pruneBatchSize is < 1 or > 512 ||
            maximumInFlight is < 1 or > 128 || maximumWaitersPerKey is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRows), "Cache policy is outside supported limits.");
        }

        MaximumRows = maximumRows;
        MaximumBytes = maximumBytes;
        MaximumAge = age;
        PruneBatchSize = pruneBatchSize;
        MaximumInFlight = maximumInFlight;
        MaximumWaitersPerKey = maximumWaitersPerKey;
    }

    public int MaximumRows { get; }
    public long MaximumBytes { get; }
    public TimeSpan MaximumAge { get; }
    public int PruneBatchSize { get; }
    public int MaximumInFlight { get; }
    public int MaximumWaitersPerKey { get; }

    public static TranslationCachePolicy FromOptions(CacheOptions options) => new(options.MaximumRows,
        options.MaximumMegabytes * 1024L * 1024, TimeSpan.FromDays(options.MaximumAgeDays),
        options.PruneBatchSize, options.MaximumInFlightRequests, options.MaximumWaitersPerKey);
}

/// <summary>A fully specified cache address; no process-local hash or request/session ID is used.</summary>
public sealed record TranslationCacheKey(string Value, string ProviderId, string Model,
    string SourceLanguage, string TargetLanguage, string PromptRevision, string GlossaryRevision,
    string SourceHash, string NormalizedSource);

/// <summary>Stored translation text and retention metadata. Correlation/session state is never reused.</summary>
public sealed record TranslationCacheEntry
{
    public TranslationCacheEntry(TranslationCacheKey key, string translatedText, DateTimeOffset createdUtc,
        DateTimeOffset lastUsedUtc, long hitCount = 0)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);
        if (key.Value.Length != 64 || key.Value.Any(character => !char.IsAsciiHexDigit(character)) ||
            key.NormalizedSource.Length is < 1 or > 4_000 || translatedText.Length > 4_000 ||
            createdUtc.Offset != TimeSpan.Zero || lastUsedUtc.Offset != TimeSpan.Zero || hitCount < 0)
        {
            throw new ArgumentException("Cache entry metadata or text is invalid.");
        }

        Key = key;
        TranslatedText = translatedText;
        CreatedUtc = createdUtc;
        LastUsedUtc = lastUsedUtc;
        HitCount = hitCount;
        SizeBytes = 512L + Encoding.UTF8.GetByteCount(key.Value) + Encoding.UTF8.GetByteCount(key.ProviderId) +
            Encoding.UTF8.GetByteCount(key.Model) + Encoding.UTF8.GetByteCount(key.SourceLanguage) +
            Encoding.UTF8.GetByteCount(key.TargetLanguage) + Encoding.UTF8.GetByteCount(key.PromptRevision) +
            Encoding.UTF8.GetByteCount(key.GlossaryRevision) + Encoding.UTF8.GetByteCount(key.SourceHash) +
            Encoding.UTF8.GetByteCount(key.NormalizedSource) + Encoding.UTF8.GetByteCount(translatedText);
    }

    public TranslationCacheKey Key { get; }
    public string TranslatedText { get; }
    public DateTimeOffset CreatedUtc { get; }
    public DateTimeOffset LastUsedUtc { get; }
    public long HitCount { get; }
    public long SizeBytes { get; }

    public TranslationCacheEntry Touch(DateTimeOffset now) => new(Key, TranslatedText, CreatedUtc,
        now > LastUsedUtc ? now : LastUsedUtc, HitCount == long.MaxValue ? HitCount : HitCount + 1);
}

/// <summary>Generation changes on clear/reconfiguration, preventing older in-flight writes from repopulating cleared data.</summary>
public sealed record TranslationCacheRead(TranslationCacheEntry? Entry, long Generation);

public sealed record TranslationCacheStatistics(int Rows, long Bytes, long Hits, long Misses,
    long Writes, long Evictions, long Generation);

public enum CacheStorageFailureKind { Busy, Corrupt, Unavailable }

/// <summary>Storage adapters classify failures without propagating paths, SQL, credentials, or text.</summary>
public sealed class CacheStorageException(CacheStorageFailureKind kind)
    : Exception("Translation cache storage is unavailable.")
{
    public CacheStorageFailureKind Kind { get; } = kind;
}

using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Cache;

/// <summary>Bounded repository used until SQLite is introduced; operations are atomic and history-independent.</summary>
public sealed class InMemoryTranslationCacheRepository : ITranslationCacheRepository
{
    public const int MaximumWriteBatch = 128;
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Dictionary<string, LinkedListNode<TranslationCacheEntry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<TranslationCacheEntry> _lru = new();
    private readonly SortedSet<Expiry> _expiry = new(Comparer<Expiry>.Create((left, right) =>
    {
        var time = left.CreatedUtc.CompareTo(right.CreatedUtc);
        return time != 0 ? time : string.CompareOrdinal(left.Key, right.Key);
    }));
    private TranslationCachePolicy _policy;
    private long _bytes;
    private long _generation;
    private long _hits;
    private long _misses;
    private long _writes;
    private long _evictions;

    public InMemoryTranslationCacheRepository(IClock clock, TranslationCachePolicy? policy = null)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policy = policy ?? new TranslationCachePolicy();
    }

    public Task<TranslationCacheRead> FindAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_policy.MaximumRows > 0 && _entries.TryGetValue(key, out var node))
            {
                if (IsExpired(node.Value, _clock.UtcNow))
                {
                    Remove(node);
                }
                else
                {
                    node.Value = node.Value.Touch(_clock.UtcNow);
                    _lru.Remove(node);
                    _lru.AddLast(node);
                    _hits++;
                    return Task.FromResult(new TranslationCacheRead(node.Value, _generation));
                }
            }

            _misses++;
            return Task.FromResult(new TranslationCacheRead(null, _generation));
        }
    }

    public Task<int> UpsertBatchAsync(IReadOnlyList<TranslationCacheEntry> entries,
        long expectedGeneration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var snapshot = entries.Take(MaximumWriteBatch + 1).ToArray();
        if (snapshot.Length > MaximumWriteBatch || snapshot.Any(entry => entry is null))
        {
            throw new ArgumentException("Cache write batch is invalid or exceeds 128 entries.", nameof(entries));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_generation != expectedGeneration || _policy.MaximumRows == 0)
            {
                return Task.FromResult(0);
            }

            PruneCore(_policy.PruneBatchSize);
            var written = 0;
            foreach (var entry in snapshot)
            {
                if (entry.SizeBytes > _policy.MaximumBytes || IsExpired(entry, _clock.UtcNow))
                {
                    continue;
                }

                if (_entries.TryGetValue(entry.Key.Value, out var existing))
                {
                    Remove(existing, eviction: false);
                }

                var removed = 0;
                while (_lru.First is { } oldest && removed < _policy.PruneBatchSize &&
                    (_entries.Count >= _policy.MaximumRows || _bytes + entry.SizeBytes > _policy.MaximumBytes))
                {
                    Remove(oldest);
                    removed++;
                }

                // A large admission cannot force an unbounded eviction loop; retry on a later request.
                if (_entries.Count >= _policy.MaximumRows || _bytes + entry.SizeBytes > _policy.MaximumBytes)
                {
                    continue;
                }

                var node = _lru.AddLast(entry);
                _entries.Add(entry.Key.Value, node);
                _expiry.Add(new Expiry(entry.CreatedUtc, entry.Key.Value));
                _bytes += entry.SizeBytes;
                _writes++;
                written++;
            }

            return Task.FromResult(written);
        }
    }

    public Task<int> PruneAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(PruneCore(_policy.PruneBatchSize));
        }
    }

    public async Task ConfigureAsync(TranslationCachePolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_policy == policy)
            {
                return;
            }

            _policy = policy;
            _generation++;
        }

        while (true)
        {
            // The policy change has committed; finish its bounded cleanup before returning.
            lock (_gate)
            {
                if (PruneCore(_policy.PruneBatchSize) == 0)
                {
                    return;
                }
            }

            await Task.Yield();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _entries.Clear();
            _lru.Clear();
            _expiry.Clear();
            _bytes = 0;
            _generation++;
        }

        return Task.CompletedTask;
    }

    public Task<TranslationCacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(new TranslationCacheStatistics(_entries.Count, _bytes, _hits, _misses,
                _writes, _evictions, _generation));
        }
    }

    public Task<IReadOnlyList<TranslationCacheEntry>> ExportContentAsync(int maximumEntries,
        bool userRequested, CancellationToken cancellationToken)
    {
        if (!userRequested)
        {
            throw new InvalidOperationException("Cache text export requires an explicit user action.");
        }

        if (maximumEntries is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var result = _lru.Reverse().Take(maximumEntries).Where(entry => !IsExpired(entry, _clock.UtcNow)).ToArray();
            return Task.FromResult<IReadOnlyList<TranslationCacheEntry>>(Array.AsReadOnly(result));
        }
    }

    private int PruneCore(int maximumRemovals)
    {
        var removed = 0;
        var now = _clock.UtcNow;
        while (removed < maximumRemovals && _expiry.Min is { } oldest &&
            now - oldest.CreatedUtc >= _policy.MaximumAge)
        {
            Remove(_entries[oldest.Key]);
            removed++;
        }

        while (removed < maximumRemovals && _lru.First is { } leastRecent &&
            (_entries.Count > _policy.MaximumRows || _bytes > _policy.MaximumBytes))
        {
            Remove(leastRecent);
            removed++;
        }

        return removed;
    }

    private bool IsExpired(TranslationCacheEntry entry, DateTimeOffset now) =>
        now - entry.CreatedUtc >= _policy.MaximumAge;

    private void Remove(LinkedListNode<TranslationCacheEntry> node, bool eviction = true)
    {
        _entries.Remove(node.Value.Key.Value);
        _expiry.Remove(new Expiry(node.Value.CreatedUtc, node.Value.Key.Value));
        _lru.Remove(node);
        _bytes -= node.Value.SizeBytes;
        if (eviction)
        {
            _evictions++;
        }
    }

    private sealed record Expiry(DateTimeOffset CreatedUtc, string Key);
}

using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Abstractions;

public interface ITranslationCacheRepository
{
    Task<TranslationCacheRead> FindAsync(string key, CancellationToken cancellationToken);

    /// <summary>Atomically accepts a bounded batch only if its pre-translation generation is still current.</summary>
    Task<int> UpsertBatchAsync(IReadOnlyList<TranslationCacheEntry> entries, long expectedGeneration,
        CancellationToken cancellationToken);

    Task<int> PruneAsync(CancellationToken cancellationToken);

    Task ConfigureAsync(TranslationCachePolicy policy, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);

    Task<TranslationCacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken);

    /// <summary>Text export is bounded and requires an explicit user action at the calling boundary.</summary>
    Task<IReadOnlyList<TranslationCacheEntry>> ExportContentAsync(int maximumEntries,
        bool userRequested, CancellationToken cancellationToken);
}

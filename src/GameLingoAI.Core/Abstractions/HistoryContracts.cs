using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Abstractions;

public interface ITranslationHistoryRepository
{
    Task ConfigureAsync(HistoryPolicy policy, CancellationToken cancellationToken);
    Task<bool> AppendAsync(TranslationHistoryEntry entry, CancellationToken cancellationToken);
    Task<int> PruneAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TranslationHistoryEntry>> ListAsync(int maximumEntries, bool userRequested, CancellationToken cancellationToken);
}

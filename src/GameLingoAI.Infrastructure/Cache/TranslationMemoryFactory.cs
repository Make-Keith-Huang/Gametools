using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.Infrastructure.Cache;

/// <summary>Session composition seam; session shutdown must await the returned gateway's disposal.</summary>
public sealed class TranslationMemoryFactory(ITranslationCacheRepository repository, IClock clock)
{
    public async Task<CachedTranslationProvider> CreateAsync(ITranslationProvider provider, ProviderOptions providerOptions,
        CacheOptions cacheOptions, CancellationToken cancellationToken)
    {
        var policy = TranslationCachePolicy.FromOptions(cacheOptions);
        try
        { await repository.ConfigureAsync(policy, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is CacheStorageException or IOException or UnauthorizedAccessException)
        {
            // Translation remains available; the gateway reports subsequent persistence failures without text.
        }

        return new CachedTranslationProvider(provider, repository, clock,
            TranslationCacheIdentity.FromOptions(providerOptions), policy);
    }
}

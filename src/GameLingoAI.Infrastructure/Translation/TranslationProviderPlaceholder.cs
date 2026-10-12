using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Translation;

public sealed class TranslationProviderPlaceholder : ITranslationProvider
{
    public string Id => "bootstrap-placeholder";

    public Task<OperationResult<TranslationResult>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = new TranslationResult(
            request.SessionId,
            request.Sequence,
            request.Text,
            request.Text,
            Id,
            TimeSpan.Zero,
            false);
        return Task.FromResult(OperationResult.Success(result));
    }
}

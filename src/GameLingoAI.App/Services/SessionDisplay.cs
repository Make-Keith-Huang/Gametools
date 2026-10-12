using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.App.Services;

/// <summary>Atomic latest display slot. The UI polls it without queuing a dispatcher action per frame.</summary>
public sealed class SessionDisplay : IOverlayPresenter
{
    private TranslationResult? _current;
    public TranslationResult? Current => Volatile.Read(ref _current);
    public Task ShowAsync(TranslationResult result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _current, result);
        return Task.CompletedTask;
    }

    public Task HideAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _current, null);
        return Task.CompletedTask;
    }
}

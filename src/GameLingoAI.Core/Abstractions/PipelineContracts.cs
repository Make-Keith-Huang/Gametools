using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IIdGenerator
{
    SessionId NewSessionId();

    CorrelationId NewCorrelationId();

    ProfileId NewProfileId();
}

/// <summary>The caller owns and must dispose every yielded frame.</summary>
public interface ICaptureSource : IAsyncDisposable
{
    IAsyncEnumerable<CaptureFrame> CaptureAsync(CancellationToken cancellationToken);
}

public interface IFramePreprocessor
{
    Task<PreprocessedFrame> ProcessAsync(
        CaptureFrame frame,
        IReadOnlyList<CaptureRegion> regions,
        PreprocessingOptions options,
        CancellationToken cancellationToken);
}

/// <summary>The engine may inspect preprocessed pixels only for local OCR and must not retain them.</summary>
public interface IOcrEngine : IAsyncDisposable
{
    OcrEngineCapabilities Capabilities { get; }

    Task<OperationResult<OcrResult>> RecognizeAsync(
        PreprocessedFrame frame,
        OcrRecognitionOptions options,
        CancellationToken cancellationToken);
}

public interface ITextStabilizer
{
    ValueTask<StableTextEvent?> ObserveAsync(
        OcrResult result,
        StabilizationContext context,
        CancellationToken cancellationToken);

    ValueTask ResetAsync(CancellationToken cancellationToken);
}

public interface ITranslationProvider
{
    string Id { get; }

    TranslationProviderCapabilities Capabilities => TranslationProviderCapabilities.Default;

    ValueTask<OperationResult<bool>> ValidateConfigurationAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(OperationResult.Success(true));

    ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new ProviderHealth(ProviderHealthStatus.Unknown));

    /// <summary>Only normalized text may cross a remote provider boundary; pixels are never valid input.</summary>
    Task<OperationResult<TranslationResult>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken);
}

/// <summary>Reads current user consent before any remote translation request is sent.</summary>
public interface IRemoteTextConsent
{
    ValueTask<bool> IsGrantedAsync(CancellationToken cancellationToken);
}

public interface IOverlayPresenter
{
    Task ShowAsync(TranslationResult result, CancellationToken cancellationToken);

    Task HideAsync(CancellationToken cancellationToken);
}

public interface IProfileRepository
{
    Task<GameProfile?> GetAsync(ProfileId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(GameProfile profile, CancellationToken cancellationToken);

    Task DeleteAsync(ProfileId id, CancellationToken cancellationToken);
}

/// <summary>Persistence boundary for the global and per-profile terminology snapshot.</summary>
public interface IGlossaryRepository
{
    /// <summary>Atomically replaces a snapshot only if stored entries still match the loaded baseline.</summary>
    Task ReplaceAsync(IReadOnlyList<GlossaryEntry> entries, IReadOnlyList<GlossaryEntry> expectedEntries, CancellationToken cancellationToken);

    Task<IReadOnlyList<GlossaryEntry>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(GlossaryEntry entry, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>A translation gateway that owns exact lookup, miss coalescing and successful-result storage.</summary>
public interface ITranslationMemory : ITranslationProvider
{
}

public interface ISessionController
{
    SessionState State { get; }

    Task StartAsync(SessionStartRequest request, CancellationToken cancellationToken);

    Task PauseAsync(CancellationToken cancellationToken);

    Task ResumeAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface ISecretStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken);

    Task SetAsync(string key, string secret, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}

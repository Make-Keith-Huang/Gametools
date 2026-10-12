using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using GameLingoAI.Infrastructure.Cache;
using GameLingoAI.Infrastructure.Capture;
using GameLingoAI.Infrastructure.Ocr;
using GameLingoAI.Infrastructure.Translation;
using GameLingoAI.Infrastructure.Translation.CustomHttp;
using GameLingoAI.Infrastructure.Translation.Ollama;
using GameLingoAI.Infrastructure.Translation.OpenAICompatible;

namespace GameLingoAI.Infrastructure;

public sealed class SessionResourcesFactory(IWindowsGraphicsCaptureSourceFactory captureFactory,
    IFramePreprocessor preprocessor, OcrModelProvisioner models, IPaddleOcrRuntimeFactory runtimes,
    OcrModelPackage package, ISecretStore secrets, IRemoteTextConsent consent, IClock clock,
    ITranslationDelay delay, IRetryJitter jitter, TranslationMemoryFactory memory) : ISessionResourcesFactory
{
    public async Task<SessionResources> CreateAsync(SessionStartRequest request, SessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (!AppSettingsValidator.Validate(request.Settings with { Providers = [request.Provider] }).IsValid)
        {
            throw Failure(FailureCode.Validation, "Correct the session configuration before starting.");
        }

        var status = await models.GetStatusAsync(package, cancellationToken).ConfigureAwait(false);
        if (status.State != OcrModelState.Ready)
        {
            throw Failure(status.State == OcrModelState.Corrupt ? FailureCode.ModelCorrupt : FailureCode.ModelMissing,
                "Install or repair the local OCR model on the OCR settings page before starting.");
        }

        var owned = new List<IAsyncDisposable>();
        try
        {
            ITranslationProvider provider = request.Provider.Kind switch
            {
                ProviderKind.OpenAiCompatible => new OpenAiCompatibleProvider(request.Provider, secrets, consent, clock, delay, jitter),
                ProviderKind.Ollama => new OllamaProvider(request.Provider, consent, clock, delay, jitter),
                ProviderKind.CustomHttp => new CustomHttpProvider(request.Provider, secrets, consent, clock, delay, jitter),
                _ => throw Failure(FailureCode.Validation, "Choose a supported translation provider.")
            };
            owned.Add(new DisposableAdapter((IDisposable)provider));
            var validation = await provider.ValidateConfigurationAsync(cancellationToken).ConfigureAwait(false);
            if (!validation.IsSuccess)
            { throw new SessionStageException(validation.Failure!); }
            var gateway = await memory.CreateAsync(provider, request.Provider, request.Settings.Cache, cancellationToken).ConfigureAwait(false);
            owned.Insert(0, gateway);
            var ocr = new PaddleOcrEngine(models, runtimes, package);
            owned.Add(ocr);
            var capture = captureFactory.Create(request.Target, sessionId);
            if (!capture.IsSuccess)
            { throw new SessionStageException(capture.Failure!); }
            owned.Add(capture.Value!);
            cancellationToken.ThrowIfCancellationRequested();
            return new SessionResources(capture.Value!, preprocessor, ocr,
                new TemporalTextStabilizer(clock, request.Settings.Stabilization), gateway, owned.ToArray());
        }
        catch
        {
            // Each cleanup is attempted even if another adapter fails; original failure stays actionable.
            foreach (var resource in owned)
            {
                try
                { await resource.DisposeAsync().ConfigureAwait(false); }
                catch (Exception) { }
            }
            throw;
        }
    }

    private static SessionStageException Failure(FailureCode code, string message) =>
        new(new PipelineFailure(code, message, CorrelationId.New(), false));

    private sealed class DisposableAdapter(IDisposable value) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { value.Dispose(); return ValueTask.CompletedTask; }
    }
}

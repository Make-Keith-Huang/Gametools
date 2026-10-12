using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Services;

namespace GameLingoAI.Core.Models;

/// <summary>A user-confirmed, session-local snapshot. Changing editor settings never mutates a running session.</summary>
public sealed class SessionStartRequest
{
    public SessionStartRequest(ConfirmedCaptureWindow target, GameProfile profile, AppSettings settings,
        ProviderOptions provider, GlossaryCatalog glossary, TranslationPromptOptions? promptOptions = null)
    {
        Target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentNullException.ThrowIfNull(profile);
        Context = new StabilizationContext(profile.Id, profile.SourceLanguage,
            profile.Regions.OrderBy(region => region.SortOrder).Select(region => region.Id).ToArray());
        Profile = new GameProfile(profile.Id, profile.Name, profile.SourceLanguage, profile.TargetLanguage,
            profile.CaptureFramesPerSecond, Array.AsReadOnly(profile.Regions.ToArray()), profile.HistoryEnabled, profile.Preprocessing, profile.Settings);
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        ArgumentNullException.ThrowIfNull(provider);
        Provider = provider with
        {
            CustomHttp = provider.CustomHttp is { } custom
            ? custom with { Headers = Array.AsReadOnly(custom.Headers.ToArray()) } : null
        };
        Glossary = glossary ?? throw new ArgumentNullException(nameof(glossary));
        PromptOptions = promptOptions ?? new TranslationPromptOptions();
        OcrOptions = new OcrRecognitionOptions(settings.Ocr.LanguageModel.ToLowerInvariant() switch
        {
            "en" => OcrLanguage.English,
            "ja" => OcrLanguage.Japanese,
            "zh" or "zh-cn" or "zh-hans" => OcrLanguage.SimplifiedChinese,
            "zh-tw" or "zh-hant" => OcrLanguage.TraditionalChinese,
            _ => throw new ArgumentException("Select a supported local OCR language before starting.")
        }, settings.Ocr.MinimumConfidence);
    }

    public ConfirmedCaptureWindow Target { get; }
    public GameProfile Profile { get; }
    public AppSettings Settings { get; }
    public ProviderOptions Provider { get; }
    public GlossaryCatalog Glossary { get; }
    public TranslationPromptOptions PromptOptions { get; }
    public StabilizationContext Context { get; }
    public OcrRecognitionOptions OcrOptions { get; }
}

public interface ISessionResourcesFactory
{
    /// <summary>Must roll back partially acquired resources on failure or cancellation.</summary>
    Task<SessionResources> CreateAsync(SessionStartRequest request, SessionId sessionId, CancellationToken cancellationToken);
}

/// <summary>Owned adapters are disposed once, in the supplied order, even if another adapter fails disposal.</summary>
public sealed class SessionResources : IAsyncDisposable
{
    private readonly IAsyncDisposable[] _owned;
    private readonly object _sync = new();
    private Task? _disposal;

    public SessionResources(ICaptureSource capture, IFramePreprocessor preprocessor, IOcrEngine ocr,
        ITextStabilizer stabilizer, ITranslationProvider translation, params IAsyncDisposable[] owned)
    {
        Capture = capture;
        Preprocessor = preprocessor;
        Ocr = ocr;
        Stabilizer = stabilizer;
        Translation = translation;
        _owned = owned.Distinct().ToArray();
    }

    public ICaptureSource Capture { get; }
    public IFramePreprocessor Preprocessor { get; }
    public IOcrEngine Ocr { get; }
    public ITextStabilizer Stabilizer { get; }
    public ITranslationProvider Translation { get; }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        { return new ValueTask(_disposal ??= DisposeOwnedAsync()); }
    }

    private async Task DisposeOwnedAsync()
    {
        List<Exception>? errors = null;
        foreach (var item in _owned)
        {
            try
            { await item.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (errors ??= []).Add(error); }
        }

        if (errors is not null)
        { throw new AggregateException(errors); }
    }
}

/// <summary>Only safe failure metadata crosses the orchestration boundary; exception messages are never surfaced.</summary>
public sealed class SessionStageException(PipelineFailure failure) : Exception("A session stage failed.")
{
    public PipelineFailure Failure { get; } = failure;
}

public enum PipelineStage { Capture, Preprocessing, Ocr, Stabilization, Translation, Presentation }
public sealed record StageMeasurement(PipelineStage Stage, long Count, double P50Milliseconds, double P95Milliseconds);
public sealed record SessionMetrics(SessionId? SessionId, CorrelationId? LastCorrelationId,
    long DroppedFrames, long DroppedText, long DroppedResults, long StaleResults, long CacheHits,
    long Errors, IReadOnlyList<StageMeasurement> Stages);

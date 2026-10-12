using System.Text.Json.Serialization;

namespace GameLingoAI.Core.Models;

public readonly record struct NormalizedRegion
{
    [JsonConstructor]
    public NormalizedRegion(double x, double y, double width, double height)
    {
        if (!TryValidate(x, y, width, height, out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(width), error);
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }

    public double Y { get; }

    public double Width { get; }

    public double Height { get; }

    public static bool TryCreate(
        double x,
        double y,
        double width,
        double height,
        out NormalizedRegion region)
    {
        if (!TryValidate(x, y, width, height, out _))
        {
            region = default;
            return false;
        }

        region = new NormalizedRegion(x, y, width, height);
        return true;
    }

    private static bool TryValidate(
        double x,
        double y,
        double width,
        double height,
        out string? error)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height))
        {
            error = "Region coordinates must be finite.";
            return false;
        }

        if (x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > 1 || y + height > 1)
        {
            error = "Region must have positive size and be contained in normalized bounds [0,1].";
            return false;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// Optional lease owned by a <see cref="CaptureFrame"/>. Pixel memory remains valid until the
/// frame is disposed; disposing the frame disposes this lease exactly once.
/// </summary>
public interface IFrameLease : IDisposable
{
}

/// <summary>
/// A local captured frame. Pixels are for local processing only and must never be sent to a
/// translation provider. The consumer owns the frame and must dispose it after downstream use.
/// </summary>
public sealed class CaptureFrame : IDisposable
{
    private IFrameLease? _lease;

    public CaptureFrame(
        PipelineMetadata metadata,
        int width,
        int height,
        ReadOnlyMemory<byte> bgraPixels,
        IFrameLease? lease = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var expectedLength = checked(width * height * 4);
        if (bgraPixels.Length < expectedLength)
        {
            throw new ArgumentException("BGRA storage is smaller than the declared frame.", nameof(bgraPixels));
        }

        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        Width = width;
        Height = height;
        BgraPixels = bgraPixels;
        _lease = lease;
    }

    public CaptureFrame(
        Guid sessionId,
        long sequence,
        DateTimeOffset capturedAt,
        int width,
        int height,
        ReadOnlyMemory<byte> bgraPixels)
        : this(
            new PipelineMetadata(
                new SessionId(sessionId),
                new SequenceNumber(sequence),
                capturedAt.ToUniversalTime(),
                CorrelationId.New()),
            width,
            height,
            bgraPixels)
    {
    }

    public PipelineMetadata Metadata { get; }

    public Guid SessionId => Metadata.SessionId.Value;

    public long Sequence => Metadata.Sequence.Value;

    public DateTimeOffset CapturedAt => Metadata.CapturedAtUtc;

    public int Width { get; }

    public int Height { get; }

    public ReadOnlyMemory<byte> BgraPixels { get; }

    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}

public sealed record OcrTextSpan
{
    public OcrTextSpan(string text, double confidence, NormalizedRegion bounds, Guid? regionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!double.IsFinite(confidence) || confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        Text = text;
        Confidence = confidence;
        Bounds = bounds;
        RegionId = regionId ?? Guid.Empty;
    }

    public string Text { get; }

    public double Confidence { get; }

    public NormalizedRegion Bounds { get; }

    public Guid RegionId { get; }
}

public sealed record OcrResult
{
    public OcrResult(PipelineMetadata metadata, IReadOnlyList<OcrTextSpan> spans)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        Spans = spans ?? throw new ArgumentNullException(nameof(spans));
    }

    public OcrResult(Guid sessionId, long sequence, IReadOnlyList<OcrTextSpan> spans)
        : this(
            new PipelineMetadata(
                new SessionId(sessionId),
                new SequenceNumber(sequence),
                DateTimeOffset.UnixEpoch,
                CorrelationId.New()),
            spans)
    {
    }

    public PipelineMetadata Metadata { get; }

    public IReadOnlyList<OcrTextSpan> Spans { get; }

    public Guid SessionId => Metadata.SessionId.Value;

    public long Sequence => Metadata.Sequence.Value;
}

public enum OcrLanguage
{
    English,
    SimplifiedChinese,
    TraditionalChinese,
    Japanese
}

public enum OcrExecutionDevice
{
    Cpu
}

public sealed record OcrRecognitionOptions
{
    public OcrRecognitionOptions(OcrLanguage language, double minimumConfidence = 0.5)
    {
        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        }

        Language = language;
        MinimumConfidence = minimumConfidence;
    }

    public OcrLanguage Language { get; }

    public double MinimumConfidence { get; }
}

public sealed record OcrEngineCapabilities(
    IReadOnlySet<OcrLanguage> Languages,
    IReadOnlySet<OcrExecutionDevice> Devices,
    bool SerializesInference,
    bool RequiresExplicitModelProvisioning);

public sealed record StableText
{
    public StableText(
        PipelineMetadata metadata,
        string text,
        IReadOnlyList<OcrTextSpan> sourceSpans,
        int observationCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observationCount);
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        Text = text;
        SourceSpans = sourceSpans ?? throw new ArgumentNullException(nameof(sourceSpans));
        ObservationCount = observationCount;
    }

    public PipelineMetadata Metadata { get; }

    public string Text { get; }

    public IReadOnlyList<OcrTextSpan> SourceSpans { get; }

    public int ObservationCount { get; }
}

public sealed record OcrArtifactReplacement
{
    public OcrArtifactReplacement(string artifact, string replacement)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifact);
        ArgumentNullException.ThrowIfNull(replacement);
        if (artifact.Length > 32 || replacement.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(artifact), "OCR artifact rules are limited to 32 characters.");
        }

        Artifact = artifact;
        Replacement = replacement;
    }

    public string Artifact { get; }

    public string Replacement { get; }
}

/// <summary>Identity and ordering inputs whose change invalidates temporal OCR state.</summary>
public sealed record StabilizationContext
{
    public StabilizationContext(
        ProfileId profileId,
        string language,
        IReadOnlyList<Guid> regionOrder,
        IReadOnlyList<OcrArtifactReplacement>? artifactReplacements = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        ArgumentNullException.ThrowIfNull(regionOrder);
        if (regionOrder.Count == 0 || regionOrder.Count > 32 ||
            regionOrder.Any(id => id == Guid.Empty) || regionOrder.Distinct().Count() != regionOrder.Count)
        {
            throw new ArgumentException("Region order must contain 1-32 distinct non-empty IDs.", nameof(regionOrder));
        }

        if (artifactReplacements?.Count > 32)
        {
            throw new ArgumentException("At most 32 OCR artifact replacements are supported.", nameof(artifactReplacements));
        }

        ProfileId = profileId;
        Language = language.Trim();
        RegionOrder = Array.AsReadOnly(regionOrder.ToArray());
        ArtifactReplacements = Array.AsReadOnly((artifactReplacements ?? Array.Empty<OcrArtifactReplacement>()).ToArray());
    }

    public ProfileId ProfileId { get; }

    public string Language { get; }

    public IReadOnlyList<Guid> RegionOrder { get; }

    public IReadOnlyList<OcrArtifactReplacement> ArtifactReplacements { get; }
}

public enum StableTextEventKind
{
    New,
    Changed,
    Disappeared
}

public sealed record StableTextEvent
{
    public StableTextEvent(
        PipelineMetadata metadata,
        StableTextEventKind kind,
        string? text,
        IReadOnlyList<OcrTextSpan> sourceSpans,
        int observationCount)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(sourceSpans);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(observationCount);
        if (kind == StableTextEventKind.Disappeared)
        {
            if (text is not null || sourceSpans.Count != 0)
            {
                throw new ArgumentException("A disappeared event cannot carry text or source spans.");
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
        }

        Metadata = metadata;
        Kind = kind;
        Text = text;
        SourceSpans = sourceSpans;
        ObservationCount = observationCount;
    }

    public PipelineMetadata Metadata { get; }

    public StableTextEventKind Kind { get; }

    public string? Text { get; }

    public IReadOnlyList<OcrTextSpan> SourceSpans { get; }

    public int ObservationCount { get; }
}

public sealed record GlossaryTerm(string Source, string Target, bool CaseSensitive = false);

public sealed record TranslationRequest
{
    public TranslationRequest(
        PipelineMetadata metadata,
        string sourceLanguage,
        string targetLanguage,
        string text,
        IReadOnlyList<string>? context,
        IReadOnlyList<GlossaryTerm>? glossary,
        string? model,
        TranslationPromptOptions? promptOptions = null,
        string? glossaryRevision = null)
    {
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        SourceLanguage = ValidateRequired(sourceLanguage, nameof(sourceLanguage));
        TargetLanguage = ValidateRequired(targetLanguage, nameof(targetLanguage));
        Text = ValidateRequired(text, nameof(text));
        Context = Array.AsReadOnly((context ?? Array.Empty<string>()).ToArray());
        Glossary = Array.AsReadOnly((glossary ?? Array.Empty<GlossaryTerm>()).ToArray());
        Model = model;
        PromptOptions = promptOptions ?? new TranslationPromptOptions();
        if (glossaryRevision is not null && (glossaryRevision.Length != 64 ||
            glossaryRevision.Any(character => !char.IsAsciiHexDigit(character))))
        {
            throw new ArgumentException("Glossary revision must be a SHA-256 hex digest.", nameof(glossaryRevision));
        }

        GlossaryRevision = glossaryRevision?.ToLowerInvariant();
    }

    public TranslationRequest(
        Guid sessionId,
        long sequence,
        string sourceLanguage,
        string targetLanguage,
        string text,
        IReadOnlyDictionary<string, string> glossary)
        : this(
            new PipelineMetadata(
                new SessionId(sessionId),
                new SequenceNumber(sequence),
                DateTimeOffset.UnixEpoch,
                CorrelationId.New()),
            sourceLanguage,
            targetLanguage,
            text,
            null,
            glossary.Select(pair => new GlossaryTerm(pair.Key, pair.Value)).ToArray(),
            null)
    {
    }

    public PipelineMetadata Metadata { get; }

    public Guid SessionId => Metadata.SessionId.Value;

    public long Sequence => Metadata.Sequence.Value;

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    public string Text { get; }

    public IReadOnlyList<string> Context { get; }

    public IReadOnlyList<GlossaryTerm> Glossary { get; }

    public string? Model { get; }

    public TranslationPromptOptions PromptOptions { get; }

    public string? GlossaryRevision { get; }

    private static string ValidateRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}

public sealed record TranslationResult
{
    public TranslationResult(
        PipelineMetadata metadata,
        string sourceText,
        string translatedText,
        string providerId,
        string? model,
        TimeSpan duration,
        bool fromCache,
        bool cacheable,
        TranslationUsage? usage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        SourceText = sourceText;
        TranslatedText = translatedText;
        ProviderId = providerId;
        Model = model;
        Duration = duration;
        FromCache = fromCache;
        Cacheable = cacheable;
        Usage = usage;
    }

    public TranslationResult(
        Guid sessionId,
        long sequence,
        string sourceText,
        string translatedText,
        string providerId,
        TimeSpan duration,
        bool fromCache)
        : this(
            new PipelineMetadata(
                new SessionId(sessionId),
                new SequenceNumber(sequence),
                DateTimeOffset.UnixEpoch,
                CorrelationId.New()),
            sourceText,
            translatedText,
            providerId,
            null,
            duration,
            fromCache,
            true)
    {
    }

    public PipelineMetadata Metadata { get; }

    public Guid SessionId => Metadata.SessionId.Value;

    public long Sequence => Metadata.Sequence.Value;

    public string SourceText { get; }

    public string TranslatedText { get; }

    public string ProviderId { get; }

    public string? Model { get; }

    public TimeSpan Duration { get; }

    public bool FromCache { get; }

    public bool Cacheable { get; }

    public TranslationUsage? Usage { get; }
}

public sealed record TranslationUsage(int? InputTokens, int? OutputTokens)
{
    public bool IsValid => (InputTokens is null or >= 0) && (OutputTokens is null or >= 0);
}

public sealed record TranslationProviderCapabilities(
    bool RequiresNetwork,
    bool SupportsHealthCheck,
    int MaximumInputCharacters,
    bool ReportsUsage = false)
{
    public static TranslationProviderCapabilities Default { get; } = new(true, false, 4_000);
}

public enum ProviderHealthStatus
{
    Unknown,
    Available,
    Unavailable
}

public sealed record ProviderHealth(ProviderHealthStatus Status, PipelineFailure? Failure = null);

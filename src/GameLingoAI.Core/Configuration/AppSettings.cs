using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Configuration;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public CaptureOptions Capture { get; init; } = new();

    public OcrOptions Ocr { get; init; } = new();

    public StabilizationOptions Stabilization { get; init; } = new();

    public LanguageOptions Languages { get; init; } = new();

    public IReadOnlyList<ProviderOptions> Providers { get; init; } = Array.Empty<ProviderOptions>();

    public CacheOptions Cache { get; init; } = new();

    public OverlayOptions Overlay { get; init; } = new();

    public LoggingOptions Logging { get; init; } = new();

    public PrivacyOptions Privacy { get; init; } = new();

    public HotkeyOptions Hotkeys { get; init; } = new();
}

public sealed record CaptureOptions
{
    public int FramesPerSecond { get; init; } = 8;

    public int QueueCapacity { get; init; } = 2;
}

public sealed record OcrOptions
{
    public string LanguageModel { get; init; } = "en";

    public double MinimumConfidence { get; init; } = 0.65;

    public PreprocessingOptions Preprocessing { get; init; } = PreprocessingOptions.PlainSubtitle;
}

public sealed record StabilizationOptions
{
    public int RequiredObservations { get; init; } = 2;

    public int ObservationWindowMilliseconds { get; init; } = 600;

    public double MinimumAverageConfidence { get; init; } = 0.65;

    public double SimilarityThreshold { get; init; } = 0.88;

    public double SpatialOverlapThreshold { get; init; } = 0.2;

    public double ImmediateChangeConfidence { get; init; } = 0.97;

    public int DisappearanceObservations { get; init; } = 2;

    public int MaximumCandidates { get; init; } = 8;

    public int MaximumSpansPerObservation { get; init; } = 128;
}

public sealed record LanguageOptions
{
    public string Source { get; init; } = "auto";

    public string Target { get; init; } = "en";
}

public enum ProviderKind
{
    OpenAiCompatible,
    Ollama,
    CustomHttp
}

public enum OpenAiEndpointDialect
{
    ChatCompletions
}

public sealed record ProviderOptions
{
    public string Id { get; init; } = string.Empty;

    public ProviderKind Kind { get; init; }

    public Uri? BaseUri { get; init; }

    public string Model { get; init; } = string.Empty;

    /// <summary>Opaque key resolved through <c>ISecretStore</c>; never the secret itself.</summary>
    public string? SecretReference { get; init; }

    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>Required for OpenAI-compatible providers; no dialect is inferred from the URL.</summary>
    public OpenAiEndpointDialect? EndpointDialect { get; init; }

    public double Temperature { get; init; }

    public int MaximumOutputTokens { get; init; } = 512;

    public CustomHttpOptions? CustomHttp { get; init; }
}

public enum CustomHttpMethod
{
    Post,
    Put
}

/// <summary>Versioned, declarative text-only mapping; no expressions or executable templates.</summary>
public sealed record CustomHttpOptions
{
    public int Version { get; init; } = 1;

    public CustomHttpMethod Method { get; init; } = CustomHttpMethod.Post;

    public IReadOnlyList<CustomHttpHeader> Headers { get; init; } = Array.Empty<CustomHttpHeader>();

    public string BodyJsonTemplate { get; init; } = "{\"text\":\"{{text}}\"}";

    public string ResponsePath { get; init; } = "$.translation";

    /// <summary>When true, only explicit loopback addresses are accepted.</summary>
    public bool LocalOnly { get; init; }
}

public sealed record CustomHttpHeader(string Name, string? Value, string? SecretReference);

public sealed record CacheOptions
{
    public int MaximumRows { get; init; } = 100_000;

    public int MaximumMegabytes { get; init; } = 500;

    public int MaximumAgeDays { get; init; } = 30;

    public int PruneBatchSize { get; init; } = 64;

    public int MaximumInFlightRequests { get; init; } = 32;

    public int MaximumWaitersPerKey { get; init; } = 64;
}

public sealed record OverlayOptions
{
    public string FontFamily { get; init; } = "Segoe UI";

    public double FontSize { get; init; } = 30;

    public bool Bold { get; init; } = true;

    public string Foreground { get; init; } = "#FFFFFF";

    public string OutlineColor { get; init; } = "#000000";

    public double OutlineThickness { get; init; } = 1.5;

    public OverlayTextAlignment Alignment { get; init; } = OverlayTextAlignment.Center;

    public OverlayAnchor Anchor { get; init; } = OverlayAnchor.Bottom;

    public double MaximumWidthRatio { get; init; } = 0.9;

    public double HorizontalOffsetDips { get; init; }

    public double VerticalOffsetDips { get; init; } = -24;

    public int MaximumCharacters { get; init; } = 1_000;

    public int MaximumLines { get; init; } = 3;

    public double BackgroundOpacity { get; init; } = 0.55;
}

public sealed record LoggingOptions
{
    public int RetainedFileCount { get; init; } = 7;

    public bool IncludeRecognizedText { get; init; }
}

public sealed record PrivacyOptions
{
    public bool RemoteTextProcessingConsent { get; init; }

    public bool HistoryEnabled { get; init; }

    public bool DiagnosticScreenshotsEnabled { get; init; }
}

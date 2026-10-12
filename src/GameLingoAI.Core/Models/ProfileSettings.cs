using GameLingoAI.Core.Configuration;

namespace GameLingoAI.Core.Models;

/// <summary>Non-secret per-profile choices. Credentials and machine-wide consent remain external.</summary>
public sealed record ProfileSettings
{
    public string? ProcessHint { get; init; }
    public string? WindowTitlePattern { get; init; }
    public string ProviderId { get; init; } = "unconfigured";
    public string Model { get; init; } = string.Empty;
    /// <summary>Credential Manager lookup name only, never a credential value.</summary>
    public string? SecretReference { get; init; }
    public string OcrLanguageModel { get; init; } = "en";
    public double OcrMinimumConfidence { get; init; } = 0.65;
    public int CaptureQueueCapacity { get; init; } = 2;
    public OverlayOptions Overlay { get; init; } = new();
    public CacheOptions Cache { get; init; } = new();
    public StabilizationOptions Stabilization { get; init; } = new();
    public int RetainedLogFileCount { get; init; } = 7;
    /// <summary>Bounded numeric/boolean x-* portable extensions; arbitrary imported strings are not retained.</summary>
    public string SafeExtensionsJson { get; init; } = "{}";
}

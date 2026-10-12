using System.Text.RegularExpressions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

/// <summary>Shared save/start validation; error messages never echo profile text.</summary>
public static class GameProfileValidator
{
    public static void Validate(GameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var settings = profile.Settings;
        if (profile.Id.Value == Guid.Empty || !Text(profile.Name, 200) ||
            !Text(profile.SourceLanguage, 32) || !Text(profile.TargetLanguage, 32) ||
            profile.Regions.Count is < 1 or > 32 || profile.Regions.Any(region => region is null ||
                !Text(region.Name, 100) || !NormalizedRegion.TryCreate(region.Bounds.X, region.Bounds.Y,
                    region.Bounds.Width, region.Bounds.Height, out _)) ||
            profile.Regions.Select(region => region.Id).Distinct().Count() != profile.Regions.Count ||
            !Enum.IsDefined(profile.Preprocessing.Preset) || settings.Overlay is null || settings.Cache is null ||
            settings.Stabilization is null || !Text(settings.ProviderId, 64) || settings.Model is null ||
            settings.Model.Length > 128 || settings.Model.Any(char.IsControl) ||
            !OptionalText(settings.ProcessHint, 200) || !OptionalText(settings.WindowTitlePattern, 512) ||
            settings.ProcessHint is { } hint && hint.IndexOfAny(['/', '\\', ':']) >= 0 ||
            !OptionalText(settings.SecretReference, 128) || settings.SecretReference is { } reference &&
                reference.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        { throw new ArgumentException("Profile identity, fields or regions are invalid.", nameof(profile)); }

        _ = ProfileJson.ReadExtensions(settings.SafeExtensionsJson);

        if (settings.WindowTitlePattern is { } pattern)
        {
            // Compile only. Matching must separately honor this timeout and never authorizes capture.
            try
            { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { throw new ArgumentException("Window title pattern is invalid.", nameof(profile)); }
        }
        var result = AppSettingsValidator.Validate(ToSettings(profile, new AppSettings()));
        if (!result.IsValid)
        { throw new ArgumentException("Profile settings are invalid.", nameof(profile)); }
    }

    /// <summary>Apply non-secret choices without importing consent, credentials or diagnostic-image permissions.</summary>
    public static AppSettings ToSettings(GameProfile profile, AppSettings local) => local with
    {
        Capture = new CaptureOptions { FramesPerSecond = profile.CaptureFramesPerSecond, QueueCapacity = profile.Settings.CaptureQueueCapacity },
        Languages = new LanguageOptions { Source = profile.SourceLanguage, Target = profile.TargetLanguage },
        Ocr = new OcrOptions
        {
            LanguageModel = profile.Settings.OcrLanguageModel,
            MinimumConfidence = profile.Settings.OcrMinimumConfidence,
            Preprocessing = profile.Preprocessing
        },
        Overlay = profile.Settings.Overlay,
        Cache = profile.Settings.Cache,
        Stabilization = profile.Settings.Stabilization,
        Logging = new LoggingOptions { RetainedFileCount = profile.Settings.RetainedLogFileCount },
        Privacy = local.Privacy with { HistoryEnabled = profile.HistoryEnabled }
    };

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    private static bool OptionalText(string? value, int maximum) => value is null || Text(value, maximum);
}

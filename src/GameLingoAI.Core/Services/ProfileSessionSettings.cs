using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

/// <summary>Resolves a saved profile against local provider configuration without granting capture or remote consent.</summary>
public sealed record ProfileSessionSettings(GameProfile Profile, AppSettings Settings, ProviderOptions Provider)
{
    public static ProfileSessionSettings Resolve(GameProfile profile, AppSettings local)
    {
        GameProfileValidator.Validate(profile);
        ArgumentNullException.ThrowIfNull(local);
        if (string.IsNullOrWhiteSpace(profile.Settings.Model))
        { throw new ArgumentException("Select a model in the profile before starting."); }
        var candidates = local.Providers.Where(provider =>
            string.Equals(provider.Id, profile.Settings.ProviderId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length != 1)
        { throw new ArgumentException("Choose exactly one locally configured provider for this profile."); }
        var provider = candidates[0] with
        {
            Model = profile.Settings.Model,
            SecretReference = profile.Settings.SecretReference
        };
        // Custom HTTP credential headers belong to the separately configured local adapter;
        // portable profiles never provide headers, endpoints or credential values.
        if (provider.CustomHttp is { } custom)
        { provider = provider with { CustomHttp = custom with { Headers = Array.AsReadOnly(custom.Headers.ToArray()) } }; }
        var settings = GameProfileValidator.ToSettings(profile, local);
        settings = settings with
        {
            Ocr = settings.Ocr with { LanguageModel = NormalizeOcrLanguage(settings.Ocr.LanguageModel) },
            Providers = Array.AsReadOnly(new[] { provider })
        };
        if (!Enum.IsDefined(provider.Kind) || !AppSettingsValidator.Validate(settings).IsValid)
        { throw new ArgumentException("Correct the profile and local provider settings before starting."); }
        var snapshot = new GameProfile(profile.Id, profile.Name, profile.SourceLanguage, profile.TargetLanguage,
            profile.CaptureFramesPerSecond, Array.AsReadOnly(profile.Regions.ToArray()), profile.HistoryEnabled,
            profile.Preprocessing, profile.Settings);
        return new ProfileSessionSettings(snapshot, settings, provider);
    }

    public static string NormalizeOcrLanguage(string value) => value.ToLowerInvariant() switch
    {
        "en" or "english" => "en",
        "ja" or "japan" or "japanese" => "ja",
        "zh" or "ch" or "zh-cn" or "zh-hans" => "zh-hans",
        "zh-tw" or "zh-hant" => "zh-hant",
        _ => throw new ArgumentException("Select a supported local OCR language: en, ja, zh-Hans or zh-Hant.")
    };
}

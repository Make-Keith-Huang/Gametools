using System.Globalization;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Configuration;

public sealed record ConfigurationError(string Field, string Message);

public sealed record ConfigurationValidationResult(IReadOnlyList<ConfigurationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IReadOnlyList<ConfigurationError> errors)
        : base("Configuration is invalid: " + string.Join("; ", errors.Select(error => $"{error.Field}: {error.Message}")))
    {
        Errors = errors;
    }

    public IReadOnlyList<ConfigurationError> Errors { get; }
}

public static class AppSettingsValidator
{
    public static ConfigurationValidationResult Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<ConfigurationError>();
        errors.AddRange(HotkeyOptionsValidator.Validate(settings.Hotkeys));

        AddRangeError(errors, "capture.framesPerSecond", settings.Capture.FramesPerSecond, 1, 30);
        AddRangeError(errors, "capture.queueCapacity", settings.Capture.QueueCapacity, 1, 2);
        AddRangeError(errors, "ocr.minimumConfidence", settings.Ocr.MinimumConfidence, 0, 1);
        AddRequired(errors, "ocr.languageModel", settings.Ocr.LanguageModel, 32);
        ValidatePreprocessing(settings.Ocr.Preprocessing, settings.Privacy, errors);
        AddRangeError(errors, "stabilization.requiredObservations", settings.Stabilization.RequiredObservations, 1, 10);
        AddRangeError(
            errors,
            "stabilization.observationWindowMilliseconds",
            settings.Stabilization.ObservationWindowMilliseconds,
            50,
            5_000);
        AddRangeError(errors, "stabilization.minimumAverageConfidence", settings.Stabilization.MinimumAverageConfidence, 0, 1);
        AddRangeError(errors, "stabilization.similarityThreshold", settings.Stabilization.SimilarityThreshold, 0.5, 1);
        AddRangeError(errors, "stabilization.spatialOverlapThreshold", settings.Stabilization.SpatialOverlapThreshold, 0, 1);
        AddRangeError(errors, "stabilization.immediateChangeConfidence", settings.Stabilization.ImmediateChangeConfidence, 0.5, 1);
        AddRangeError(errors, "stabilization.disappearanceObservations", settings.Stabilization.DisappearanceObservations, 1, 10);
        AddRangeError(errors, "stabilization.maximumCandidates", settings.Stabilization.MaximumCandidates, 1, 32);
        AddRangeError(errors, "stabilization.maximumSpansPerObservation", settings.Stabilization.MaximumSpansPerObservation, 1, 512);
        AddLanguageError(errors, "languages.source", settings.Languages.Source);
        AddLanguageError(errors, "languages.target", settings.Languages.Target);
        AddRangeError(errors, "cache.maximumRows", settings.Cache.MaximumRows, 0, 1_000_000);
        AddRangeError(errors, "cache.maximumMegabytes", settings.Cache.MaximumMegabytes, 1, 4_096);
        AddRangeError(errors, "cache.maximumAgeDays", settings.Cache.MaximumAgeDays, 1, 3_650);
        AddRangeError(errors, "cache.pruneBatchSize", settings.Cache.PruneBatchSize, 1, 512);
        AddRangeError(errors, "cache.maximumInFlightRequests", settings.Cache.MaximumInFlightRequests, 1, 128);
        AddRangeError(errors, "cache.maximumWaitersPerKey", settings.Cache.MaximumWaitersPerKey, 1, 256);
        errors.AddRange(OverlayLayout.Validate(settings.Overlay));
        AddRangeError(errors, "logging.retainedFileCount", settings.Logging.RetainedFileCount, 1, 30);

        if (settings.Logging.IncludeRecognizedText)
        {
            errors.Add(new ConfigurationError(
                "logging.includeRecognizedText",
                "Recognized text logging is disabled in V1 to prevent sensitive-text disclosure."));
        }

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            errors.Add(new ConfigurationError(
                "schemaVersion",
                $"Expected schema version {AppSettings.CurrentSchemaVersion}."));
        }

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < settings.Providers.Count; index++)
        {
            ValidateProvider(settings.Providers[index], index, providerIds, errors);
        }

        return new ConfigurationValidationResult(errors);
    }

    public static bool IsLanguageTag(string value)
    {
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(value);
            return !string.IsNullOrWhiteSpace(culture.Name);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static void ValidateProvider(
        ProviderOptions provider,
        int index,
        HashSet<string> providerIds,
        List<ConfigurationError> errors)
    {
        var prefix = $"providers[{index}]";
        AddRequired(errors, $"{prefix}.id", provider.Id, 64);
        AddRequired(errors, $"{prefix}.model", provider.Model, 128);
        AddRangeError(errors, $"{prefix}.timeoutSeconds", provider.TimeoutSeconds, 1, 120);

        if (provider.Kind == ProviderKind.OpenAiCompatible)
        {
            if (provider.EndpointDialect is not OpenAiEndpointDialect.ChatCompletions)
            {
                errors.Add(new ConfigurationError($"{prefix}.endpointDialect",
                    "Select the supported chatCompletions endpoint dialect explicitly."));
            }

            AddRangeError(errors, $"{prefix}.temperature", provider.Temperature, 0, 2);
            AddRangeError(errors, $"{prefix}.maximumOutputTokens", provider.MaximumOutputTokens, 1, 4_096);
            AddRequired(errors, $"{prefix}.secretReference", provider.SecretReference, 128);
            if (provider.SecretReference is { Length: > 0 } reference &&
                reference.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            {
                errors.Add(new ConfigurationError($"{prefix}.secretReference",
                    "Use only letters, digits, dots, dashes, or underscores in a secret reference."));
            }

            if (provider.BaseUri is { IsAbsoluteUri: true } uri &&
                (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 ||
                 uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                 !uri.AbsolutePath.EndsWith('/')))
            {
                errors.Add(new ConfigurationError($"{prefix}.baseUri",
                    "Use an HTTPS prefix ending in '/' without credentials, query, or fragment."));
            }
        }

        if (provider.Kind == ProviderKind.CustomHttp)
        {
            errors.AddRange(CustomHttpConfigurationValidator.Validate(provider.CustomHttp, $"{prefix}.customHttp"));
            if (provider.BaseUri is { IsAbsoluteUri: true } customUri)
            {
                var local = provider.CustomHttp?.LocalOnly == true;
                if (customUri.UserInfo.Length != 0 || customUri.Fragment.Length != 0 ||
                    customUri.Query.Length != 0 ||
                    local && (!customUri.IsLoopback ||
                              customUri.Scheme != Uri.UriSchemeHttp &&
                              customUri.Scheme != Uri.UriSchemeHttps) ||
                    !local && customUri.Scheme != Uri.UriSchemeHttps)
                {
                    errors.Add(new ConfigurationError($"{prefix}.baseUri",
                        "Custom HTTP needs HTTPS, or explicit local-only loopback mode, without URL credentials/query/fragment."));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(provider.Id) && !providerIds.Add(provider.Id))
        {
            errors.Add(new ConfigurationError($"{prefix}.id", "Provider IDs must be unique."));
        }

        if (provider.BaseUri is null && provider.Kind != ProviderKind.Ollama ||
            provider.BaseUri is { IsAbsoluteUri: false })
        {
            errors.Add(new ConfigurationError($"{prefix}.baseUri", "An absolute provider URL is required."));
        }
        else if (provider.BaseUri is { } providerUri)
        {
            var isSecure = providerUri.Scheme == Uri.UriSchemeHttps;
            var isLoopbackOllama = provider.Kind == ProviderKind.Ollama &&
                providerUri.Scheme == Uri.UriSchemeHttp && providerUri.IsLoopback;
            var isLoopbackCustom = provider.Kind == ProviderKind.CustomHttp &&
                provider.CustomHttp?.LocalOnly == true && providerUri.IsLoopback &&
                providerUri.Scheme == Uri.UriSchemeHttp;
            if (!isSecure && !isLoopbackOllama && !isLoopbackCustom)
            {
                errors.Add(new ConfigurationError(
                    $"{prefix}.baseUri",
                    "HTTPS is required; HTTP is allowed only for an explicit loopback Ollama endpoint."));
            }
        }

        if (provider.SecretReference is { Length: > 128 })
        {
            errors.Add(new ConfigurationError(
                $"{prefix}.secretReference",
                "Secret reference must not exceed 128 characters."));
        }
    }

    private static void ValidatePreprocessing(
        PreprocessingOptions? options,
        PrivacyOptions privacy,
        List<ConfigurationError> errors)
    {
        if (options is null)
        {
            errors.Add(new ConfigurationError("ocr.preprocessing", "Preprocessing options are required."));
            return;
        }

        AddRangeError(errors, "ocr.preprocessing.contrast", options.Contrast, 0.25, 4);
        AddRangeError(errors, "ocr.preprocessing.scale", options.Scale, 0.5, 4);
        AddRangeError(errors, "ocr.preprocessing.paddingPixels", options.PaddingPixels, 0, 64);
        AddRangeError(errors, "ocr.preprocessing.maximumOutputWidth", options.MaximumOutputWidth, 64, 8192);
        AddRangeError(errors, "ocr.preprocessing.maximumOutputHeight", options.MaximumOutputHeight, 64, 8192);
        AddRangeError(errors, "ocr.preprocessing.maximumOutputPixels", options.MaximumOutputPixels, 4096, 33_554_432);
        if (!options.Grayscale &&
            (options.Contrast != 1 || options.Threshold is not null || options.Invert || options.Sharpen))
        {
            errors.Add(new ConfigurationError(
                "ocr.preprocessing.grayscale",
                "Contrast, threshold, inversion, and sharpening require grayscale output."));
        }

        if (options.EnableDiagnosticImages && !privacy.DiagnosticScreenshotsEnabled)
        {
            errors.Add(new ConfigurationError(
                "ocr.preprocessing.enableDiagnosticImages",
                "Diagnostic image output requires explicit diagnostic screenshot consent."));
        }
    }

    private static void AddRequired(
        List<ConfigurationError> errors,
        string field,
        string? value,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add(new ConfigurationError(field, "A value is required."));
        }
        else if (value.Length > maximumLength)
        {
            errors.Add(new ConfigurationError(field, $"Value must not exceed {maximumLength} characters."));
        }
    }

    private static void AddLanguageError(
        List<ConfigurationError> errors,
        string field,
        string value)
    {
        if (!IsLanguageTag(value))
        {
            errors.Add(new ConfigurationError(field, "Use a recognized BCP 47 language tag or 'auto'."));
        }
    }

    private static void AddRangeError(
        List<ConfigurationError> errors,
        string field,
        int value,
        int minimum,
        int maximum)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add(new ConfigurationError(field, $"Value must be between {minimum} and {maximum}."));
        }
    }

    private static void AddRangeError(
        List<ConfigurationError> errors,
        string field,
        double value,
        double minimum,
        double maximum)
    {
        if (!double.IsFinite(value) || value < minimum || value > maximum)
        {
            errors.Add(new ConfigurationError(field, $"Value must be between {minimum} and {maximum}."));
        }
    }
}

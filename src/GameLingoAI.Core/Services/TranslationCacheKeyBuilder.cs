using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

public sealed record TranslationCacheIdentity
{
    public TranslationCacheIdentity(string providerId, string model, string configurationRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationRevision);
        if (providerId.Length > 64 || model.Length > 128 || configurationRevision.Length > 128)
        {
            throw new ArgumentException("Cache identity is too long.");
        }

        ProviderId = providerId;
        Model = model;
        ConfigurationRevision = configurationRevision;
    }

    public string ProviderId { get; }
    public string Model { get; }
    public string ConfigurationRevision { get; }

    public static TranslationCacheIdentity FromOptions(ProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Credential references identify a configuration; secret values are never resolved or serialized.
        var configuration = new
        {
            options.Kind,
            Endpoint = options.BaseUri?.AbsoluteUri,
            options.EndpointDialect,
            options.Temperature,
            options.MaximumOutputTokens,
            options.SecretReference,
            options.CustomHttp
        };
        // Older Ollama requests ignored Temperature; their cached output must not survive the wire fix.
        var wireRevision = options.Kind == ProviderKind.Ollama ? "ollama-generate-v2:" : string.Empty;
        return new TranslationCacheIdentity(options.Id, options.Model,
            TranslationCacheKeyBuilder.Hash(wireRevision + JsonSerializer.Serialize(configuration)));
    }
}

public static class TranslationCacheKeyBuilder
{
    public const int SchemaVersion = 1;

    public static TranslationCacheKey Build(TranslationRequest request, TranslationCacheIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        var plan = TranslationPromptBuilder.Build(request);
        var sourceLanguage = NormalizeText(request.SourceLanguage.Trim()).ToLowerInvariant();
        var targetLanguage = NormalizeText(request.TargetLanguage.Trim()).ToLowerInvariant();
        var source = NormalizeText(request.Text);
        var selectedTerms = Hash(JsonSerializer.Serialize(plan.SelectedTerms));
        var glossaryRevision = request.GlossaryRevision ?? selectedTerms;
        var promptRevision = $"v{plan.Version}:{Hash(plan.SystemMessage)}";
        var canonical = JsonSerializer.Serialize(new
        {
            schemaVersion = SchemaVersion,
            provider = identity.ProviderId,
            model = identity.Model,
            configurationRevision = identity.ConfigurationRevision,
            sourceLanguage,
            targetLanguage,
            promptRevision,
            glossaryRevision,
            selectedTerms,
            text = source,
            context = plan.Data.Context.Select(NormalizeText).ToArray()
        });
        return new TranslationCacheKey(Hash(canonical), identity.ProviderId, identity.Model,
            sourceLanguage, targetLanguage, promptRevision, glossaryRevision, Hash(source), source);
    }

    /// <summary>Only canonical Unicode and newline representation are folded; case, spacing and tags keep their meaning.</summary>
    public static string NormalizeText(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n').Normalize(NormalizationForm.FormC);

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

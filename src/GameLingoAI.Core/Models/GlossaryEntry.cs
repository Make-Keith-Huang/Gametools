using System.Text.Json.Serialization;

namespace GameLingoAI.Core.Models;

/// <summary>A language-pair term, global when <see cref="ProfileId"/> is null.</summary>
public sealed record GlossaryEntry
{
    [JsonConstructor]
    public GlossaryEntry(Guid id, ProfileId? profileId, string sourceLanguage, string targetLanguage,
        string sourceTerm, string targetTerm, bool caseSensitive = false, bool enabled = true)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        if (profileId is { Value: var value } && value == Guid.Empty)
        {
            throw new ArgumentException("Profile ID must be non-empty when supplied.", nameof(profileId));
        }
        Id = id;
        ProfileId = profileId;
        SourceLanguage = Clean(sourceLanguage, 32, nameof(sourceLanguage)).ToLowerInvariant();
        TargetLanguage = Clean(targetLanguage, 32, nameof(targetLanguage)).ToLowerInvariant();
        SourceTerm = Clean(sourceTerm, 100, nameof(sourceTerm));
        TargetTerm = Clean(targetTerm, 100, nameof(targetTerm));
        CaseSensitive = caseSensitive;
        Enabled = enabled;
    }

    public Guid Id { get; }
    public ProfileId? ProfileId { get; }
    public string SourceLanguage { get; }
    public string TargetLanguage { get; }
    public string SourceTerm { get; }
    public string TargetTerm { get; }
    public bool CaseSensitive { get; }
    public bool Enabled { get; }

    internal static string Clean(string value, int maximumLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var cleaned = value.Trim().Normalize(System.Text.NormalizationForm.FormC);
        if (cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} must be at most {maximumLength} visible characters.", name);
        }

        return cleaned;
    }
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

/// <summary>Immutable, bounded glossary snapshot; import either succeeds completely or changes nothing.</summary>
public sealed class GlossaryCatalog
{
    public const int MaximumEntries = 2_000;
    // Accommodates the escaped JSON export of all 2,000 maximum-length Unicode entries.
    public const int MaximumImportBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions FileOptions = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly GlossaryEntry[] _entries;

    public GlossaryCatalog(IEnumerable<GlossaryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.Take(MaximumEntries + 1).ToArray();
        if (_entries.Length > MaximumEntries || _entries.Any(entry => entry is null) ||
            _entries.Select(entry => entry.Id).Distinct().Count() != _entries.Length)
        {
            throw new ArgumentException("Glossary entries exceed the limit or contain duplicate IDs.", nameof(entries));
        }
    }

    public IReadOnlyList<GlossaryEntry> Entries => Array.AsReadOnly(_entries);

    public GlossaryCatalog WithEntry(GlossaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new GlossaryCatalog(_entries.Where(existing => existing.Id != entry.Id).Append(entry));
    }

    public GlossaryCatalog WithoutEntry(Guid id) => new(_entries.Where(entry => entry.Id != id));

    public string ExportJson() => JsonSerializer.Serialize(new GlossaryFile(1,
        _entries.OrderBy(entry => entry.Id).ToArray()));

    public static GlossaryCatalog ImportJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumImportBytes || Encoding.UTF8.GetByteCount(json) > MaximumImportBytes)
        {
            throw new FormatException("Glossary import exceeds 4 MiB.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            RejectDuplicateProperties(document.RootElement);
            var file = document.RootElement.Deserialize<GlossaryFile>(FileOptions);
            if (file is null || file.Version != 1 || file.Entries is null)
            {
                throw new FormatException("Unsupported glossary file version or missing entries.");
            }

            return new GlossaryCatalog(file.Entries);
        }
        catch (JsonException exception)
        {
            throw new FormatException("Invalid glossary JSON.", exception);
        }
    }

    /// <summary>Returns a stable semantic hash for the active language pair and profile; IDs and input order do not matter.</summary>
    public string Revision(ProfileId? profileId, string sourceLanguage, string targetLanguage)
    {
        sourceLanguage = GlossaryEntry.Clean(sourceLanguage, 32, nameof(sourceLanguage)).ToLowerInvariant();
        targetLanguage = GlossaryEntry.Clean(targetLanguage, 32, nameof(targetLanguage)).ToLowerInvariant();
        var canonical = Relevant(profileId, sourceLanguage, targetLanguage)
            .OrderBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ThenBy(entry => entry.ProfileId is null ? 0 : 1)
            .ThenBy(entry => entry.CaseSensitive)
            .ThenBy(entry => entry.TargetTerm, StringComparer.Ordinal)
            .Select(entry => new
            {
                Scope = entry.ProfileId is null ? "global" : "profile",
                entry.SourceTerm,
                entry.TargetTerm,
                entry.CaseSensitive
            }).Distinct();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, sourceLanguage, targetLanguage, canonical });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>Profile scope wins; then exact-case terms; ties use ordinal source/target order.</summary>
    public IReadOnlyList<GlossaryTerm> Select(ProfileId? profileId, string sourceLanguage,
        string targetLanguage, string text, GlossaryBudget? budget = null)
    {
        sourceLanguage = GlossaryEntry.Clean(sourceLanguage, 32, nameof(sourceLanguage)).ToLowerInvariant();
        targetLanguage = GlossaryEntry.Clean(targetLanguage, 32, nameof(targetLanguage)).ToLowerInvariant();
        return SelectCandidates(Relevant(profileId, sourceLanguage, targetLanguage)
            .Select(entry => new Candidate(entry.SourceTerm, entry.TargetTerm, entry.CaseSensitive,
                entry.ProfileId is not null)), text, budget);
    }

    /// <summary>Bounds and selects request-level terms using the same rules as catalog selection.</summary>
    public static IReadOnlyList<GlossaryTerm> SelectTerms(IReadOnlyList<GlossaryTerm> terms,
        string text, GlossaryBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(terms);
        if (terms.Count > MaximumEntries || terms.Any(term => term is null))
        {
            throw new ArgumentException("Invalid glossary term collection.", nameof(terms));
        }

        return SelectCandidates(terms.Select(term => new Candidate(
            GlossaryEntry.Clean(term.Source, 100, nameof(term.Source)),
            GlossaryEntry.Clean(term.Target, 100, nameof(term.Target)), term.CaseSensitive, false)), text, budget);
    }

    private static ReadOnlyCollection<GlossaryTerm> SelectCandidates(IEnumerable<Candidate> candidates,
        string text, GlossaryBudget? budget)
    {
        ArgumentNullException.ThrowIfNull(text);
        budget ??= new GlossaryBudget();
        var normalized = text.Normalize(NormalizationForm.FormC);
        var winners = candidates
            .Where(entry => normalized.Contains(entry.SourceTerm, entry.CaseSensitive
                ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.ProfileScoped)
            .ThenByDescending(entry => entry.CaseSensitive)
            .ThenBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ThenBy(entry => entry.TargetTerm, StringComparer.Ordinal)
            .GroupBy(entry => entry.SourceTerm, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(entry => entry.SourceTerm.Length)
            .ThenBy(entry => entry.SourceTerm, StringComparer.Ordinal)
            .ToArray();

        var result = new List<GlossaryTerm>();
        var characters = 0;
        var tokens = 2; // JSON array brackets; UTF-8 byte count is a conservative provider-neutral estimate.
        foreach (var entry in winners)
        {
            var length = entry.SourceTerm.Length + entry.TargetTerm.Length;
            var term = new GlossaryTerm(entry.SourceTerm, entry.TargetTerm, entry.CaseSensitive);
            var estimatedTokens = JsonSerializer.SerializeToUtf8Bytes(term).Length + (result.Count == 0 ? 0 : 1);
            if (result.Count == budget.MaximumTerms)
            {
                break;
            }

            if (characters + length > budget.MaximumCharacters ||
                tokens + estimatedTokens > budget.MaximumEstimatedTokens)
            {
                continue;
            }

            result.Add(term);
            characters += length;
            tokens += estimatedTokens;
        }

        return result.AsReadOnly();
    }

    private IEnumerable<GlossaryEntry> Relevant(ProfileId? profileId, string sourceLanguage, string targetLanguage) =>
        _entries.Where(entry => entry.Enabled &&
            (entry.ProfileId is null || entry.ProfileId == profileId) &&
            string.Equals(entry.SourceLanguage, sourceLanguage, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.TargetLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase));

    private sealed record GlossaryFile(int Version, GlossaryEntry[] Entries);

    private sealed record Candidate(string SourceTerm, string TargetTerm, bool CaseSensitive, bool ProfileScoped);

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException("Duplicate glossary JSON property.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}

public sealed record GlossaryBudget
{
    public GlossaryBudget(int maximumTerms = 32, int maximumCharacters = 1_024,
        int maximumEstimatedTokens = 1_024)
    {
        if (maximumTerms is < 1 or > 64 || maximumCharacters is < 1 or > 4_096 ||
            maximumEstimatedTokens is < 2 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTerms), "Glossary budget is out of range.");
        }

        MaximumTerms = maximumTerms;
        MaximumCharacters = maximumCharacters;
        MaximumEstimatedTokens = maximumEstimatedTokens;
    }

    public int MaximumTerms { get; }
    public int MaximumCharacters { get; }
    public int MaximumEstimatedTokens { get; }
}

using System.Text.Json.Serialization;

namespace GameLingoAI.Core.Models;

public sealed record TranslationPromptOptions(bool PreserveLineBreaks = true, bool PreserveTags = true);

/// <summary>Only text data is carried here; model configuration and credentials remain outside the prompt.</summary>
public sealed record TranslationPromptData(
    [property: JsonPropertyName("sourceLanguage")] string SourceLanguage,
    [property: JsonPropertyName("targetLanguage")] string TargetLanguage,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("context")] IReadOnlyList<string> Context,
    [property: JsonPropertyName("glossary")] IReadOnlyList<GlossaryTerm> Glossary);

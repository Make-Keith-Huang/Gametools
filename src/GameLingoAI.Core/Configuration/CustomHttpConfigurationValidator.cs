using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameLingoAI.Core.Configuration;

/// <summary>Validates the deliberately small custom-HTTP JSON mapping language.</summary>
public static partial class CustomHttpConfigurationValidator
{
    private static readonly HashSet<string> Tokens = new(StringComparer.Ordinal)
    {
        "{{text}}", "{{sourceLanguage}}", "{{targetLanguage}}", "{{model}}",
        "{{context}}", "{{glossary}}", "{{systemPrompt}}", "{{promptData}}"
    };

    private static readonly HashSet<string> ForbiddenMediaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "images", "image_url", "input_image", "audio", "input_audio", "file", "files"
    };

    public static IReadOnlyList<ConfigurationError> Validate(CustomHttpOptions? options, string prefix)
    {
        var errors = new List<ConfigurationError>();
        if (options is null)
        {
            errors.Add(new ConfigurationError(prefix, "Custom HTTP mapping is required."));
            return errors;
        }

        if (options.Version != 1)
        {
            errors.Add(new ConfigurationError($"{prefix}.version", "Only custom mapping version 1 is supported."));
        }

        if (options.Method is not CustomHttpMethod.Post and not CustomHttpMethod.Put)
        {
            errors.Add(new ConfigurationError($"{prefix}.method", "Only POST or PUT is allowed."));
        }

        if (options.Headers is null)
        {
            errors.Add(new ConfigurationError($"{prefix}.headers", "Header collection cannot be null."));
        }
        else if (options.Headers.Count > 8)
        {
            errors.Add(new ConfigurationError($"{prefix}.headers", "At most eight fixed headers are allowed."));
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (header, index) in (options.Headers ?? Array.Empty<CustomHttpHeader>())
                     .Select((header, index) => (header, index)))
        {
            var path = $"{prefix}.headers[{index}]";
            if (header is null)
            {
                errors.Add(new ConfigurationError(path, "Header entry cannot be null."));
                continue;
            }

            if (!names.Add(header.Name) ||
                header.Name is not "Authorization" and not "X-Api-Key" and not "X-Client-Id")
            {
                errors.Add(new ConfigurationError($"{path}.name", "Header name is unsupported or duplicated."));
            }

            var hasValue = !string.IsNullOrEmpty(header.Value);
            var hasSecret = !string.IsNullOrEmpty(header.SecretReference);
            if (hasValue == hasSecret ||
                header.Name == "X-Client-Id" && hasSecret ||
                header.Name is "Authorization" or "X-Api-Key" && hasValue)
            {
                errors.Add(new ConfigurationError(path,
                    "Credential headers require secret references; X-Client-Id requires a fixed value."));
            }

            if (header.Value is { } value && (value.Length > 128 || value.Any(char.IsControl)))
            {
                errors.Add(new ConfigurationError($"{path}.value", "Header value is too long or contains controls."));
            }

            if (header.SecretReference is { } reference &&
                (reference.Length > 128 || reference.Any(character =>
                    !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_')))
            {
                errors.Add(new ConfigurationError($"{path}.secretReference", "Invalid secret reference."));
            }
        }

        if (string.IsNullOrWhiteSpace(options.ResponsePath) || options.ResponsePath.Length > 256 ||
            !ResponsePathPattern().IsMatch(options.ResponsePath))
        {
            errors.Add(new ConfigurationError($"{prefix}.responsePath",
                "Use a bounded path such as $.choices[0].message.content."));
        }

        if (string.IsNullOrWhiteSpace(options.BodyJsonTemplate) || options.BodyJsonTemplate.Length > 8_192)
        {
            errors.Add(new ConfigurationError($"{prefix}.bodyJsonTemplate", "JSON body template must be 1-8192 characters."));
            return errors;
        }

        try
        {
            using var json = JsonDocument.Parse(options.BodyJsonTemplate, new JsonDocumentOptions
            {
                MaxDepth = 8,
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            var hasText = false;
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !ValidateElement(json.RootElement, ref hasText, 0) || !hasText)
            {
                errors.Add(new ConfigurationError($"{prefix}.bodyJsonTemplate",
                    "Template must be a bounded JSON object with an exact {{text}} or {{promptData}} value token."));
            }
        }
        catch (JsonException)
        {
            errors.Add(new ConfigurationError($"{prefix}.bodyJsonTemplate", "Template is not valid bounded JSON."));
        }

        return errors;
    }

    private static bool ValidateElement(JsonElement element, ref bool hasText, int depth)
    {
        if (depth > 8)
        {
            return false;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var count = 0;
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (++count > 64 || property.Name.Length > 64 ||
                        !names.Add(property.Name) ||
                        ForbiddenMediaKeys.Contains(property.Name) ||
                        (property.Name.Equals("role", StringComparison.OrdinalIgnoreCase) &&
                         property.Value.ValueKind == JsonValueKind.String &&
                         property.Value.GetString()!.Contains("{{", StringComparison.Ordinal)) ||
                        property.Name.Contains("{{", StringComparison.Ordinal) ||
                        !ValidateElement(property.Value, ref hasText, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                var items = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (++items > 64 || !ValidateElement(item, ref hasText, depth + 1))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.String:
                var value = element.GetString()!;
                if (value is "{{text}}" or "{{promptData}}")
                {
                    hasText = true;
                }

                return value.Length <= 2_048 &&
                    (!value.Contains("{{", StringComparison.Ordinal) || Tokens.Contains(value)) &&
                    (!value.Contains("}}", StringComparison.Ordinal) || Tokens.Contains(value));
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            default:
                return false;
        }
    }

    [GeneratedRegex("^\\$(?:\\.[A-Za-z_][A-Za-z0-9_]{0,63}|\\[(?:0|[1-9][0-9]?)\\]){1,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex ResponsePathPattern();
}

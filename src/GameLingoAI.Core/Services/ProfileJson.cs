using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Core.Services;

public sealed record ProfileImportResult(int SchemaVersion, GameProfile? Profile, bool IsReadOnly);

/// <summary>Portable allowlisted profile document. Parsing has no persistence or capture side effects.</summary>
public static class ProfileJson
{
    public const int CurrentVersion = 1;
    public const int MaximumBytes = 131_072;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static ProfileImportResult Import(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumBytes || Encoding.UTF8.GetByteCount(json) > MaximumBytes)
        { throw new ArgumentException("Profile JSON exceeds 128 KiB.", nameof(json)); }
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            CheckDuplicates(parsed.RootElement);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object ||
                !parsed.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out var version) || version < 1)
            { throw new JsonException(); }
            if (version > CurrentVersion)
            { return new ProfileImportResult(version, null, true); }
            var document = parsed.RootElement.Deserialize<Document>(Options) ?? throw new JsonException();
            if (document.WindowMatch is null || document.Languages is null || document.Capture is null || document.Ocr is null ||
                document.Translation is null || document.Privacy is null || document.Capture.Regions is null ||
                document.Capture.Regions.Count is < 1 or > 32 || document.Ocr.Preprocessing is null)
            { throw new JsonException(); }
            var regions = document.Capture.Regions.Select(region => region is null ? throw new JsonException() :
                new CaptureRegion(region.Id, region.Name, new NormalizedRegion(region.X, region.Y, region.Width, region.Height), region.SortOrder)).ToArray();
            var extensions = document.Extensions?.Where(item => IsSafeExtension(item.Key, item.Value))
                .ToDictionary(item => item.Key, item => item.Value) ?? new Dictionary<string, JsonElement>();
            var profile = new GameProfile(new ProfileId(document.Id), document.Name, document.Languages.Source, document.Languages.Target,
                document.Capture.Fps, Array.AsReadOnly(regions), document.Privacy.HistoryEnabled, document.Ocr.Preprocessing,
                new ProfileSettings
                {
                    ProcessHint = document.WindowMatch.ProcessHint,
                    WindowTitlePattern = document.WindowMatch.TitlePattern,
                    ProviderId = document.Translation.ProviderId,
                    Model = document.Translation.Model,
                    // Imported files cannot choose a credential on this machine.
                    SecretReference = null,
                    OcrLanguageModel = document.Ocr.LanguageModel,
                    OcrMinimumConfidence = document.Ocr.Confidence,
                    CaptureQueueCapacity = document.Capture.QueueCapacity,
                    Overlay = document.Overlay,
                    Cache = document.Cache,
                    Stabilization = document.Stabilization,
                    RetainedLogFileCount = document.RetainedLogFileCount,
                    SafeExtensionsJson = JsonSerializer.Serialize(extensions)
                });
            GameProfileValidator.Validate(profile);
            return new ProfileImportResult(version, profile, false);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or OverflowException)
        { throw new ArgumentException("Profile JSON is malformed or contains invalid settings.", nameof(json)); }
    }

    public static string Export(GameProfile profile)
    {
        GameProfileValidator.Validate(profile);
        var settings = profile.Settings;
        var extensions = ReadExtensions(settings.SafeExtensionsJson);
        var document = new Document
        {
            Id = profile.Id.Value,
            Name = profile.Name,
            WindowMatch = new WindowMatch { ProcessHint = settings.ProcessHint, TitlePattern = settings.WindowTitlePattern },
            Languages = new LanguageOptions { Source = profile.SourceLanguage, Target = profile.TargetLanguage },
            Capture = new Capture
            {
                Fps = profile.CaptureFramesPerSecond,
                QueueCapacity = settings.CaptureQueueCapacity,
                Regions = profile.Regions.Select(region => new Region
                {
                    Id = region.Id,
                    Name = region.Name,
                    X = region.Bounds.X,
                    Y = region.Bounds.Y,
                    Width = region.Bounds.Width,
                    Height = region.Bounds.Height,
                    SortOrder = region.SortOrder
                }).ToList()
            },
            Ocr = new Ocr
            {
                LanguageModel = settings.OcrLanguageModel,
                Confidence = settings.OcrMinimumConfidence,
                Preprocessing = profile.Preprocessing
            },
            Translation = new Translation { ProviderId = settings.ProviderId, Model = settings.Model },
            Overlay = settings.Overlay,
            Cache = settings.Cache,
            Stabilization = settings.Stabilization,
            Privacy = new Privacy { HistoryEnabled = profile.HistoryEnabled },
            RetainedLogFileCount = settings.RetainedLogFileCount,
            Extensions = extensions
        };
        var json = JsonSerializer.Serialize(document, Options);
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes)
        { throw new ArgumentException("Profile export exceeds 128 KiB.", nameof(profile)); }
        return json;
    }

    public static GameProfile Duplicate(GameProfile profile, string name)
    {
        GameProfileValidator.Validate(profile);
        var duplicate = new GameProfile(ProfileId.New(), name, profile.SourceLanguage, profile.TargetLanguage,
            profile.CaptureFramesPerSecond, Array.AsReadOnly(profile.Regions.Select(region =>
                new CaptureRegion(Guid.NewGuid(), region.Name, region.Bounds, region.SortOrder)).ToArray()),
            profile.HistoryEnabled, profile.Preprocessing, profile.Settings);
        GameProfileValidator.Validate(duplicate);
        return duplicate;
    }

    internal static Dictionary<string, JsonElement> ReadExtensions(string json)
    {
        if (json is null || json.Length > 4096)
        { throw new ArgumentException("Profile extensions exceed their bound."); }
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 2 });
            CheckDuplicates(parsed.RootElement);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
            { throw new JsonException(); }
            var values = parsed.RootElement.EnumerateObject().ToArray();
            if (values.Length > 32 || values.Any(item => !IsSafeExtension(item.Name, item.Value)))
            { throw new JsonException(); }
            return values.ToDictionary(item => item.Name, item => item.Value.Clone());
        }
        catch (JsonException) { throw new ArgumentException("Profile extensions must be bounded numeric or boolean x-* fields."); }
    }

    private static bool IsSafeExtension(string name, JsonElement value) => name.Length is > 2 and <= 64 &&
        name.StartsWith("x-", StringComparison.Ordinal) && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-') &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False ||
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number));

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) { throw new JsonException(); } CheckDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { foreach (var item in value.EnumerateArray()) { CheckDuplicates(item); } }
    }

    private sealed class Document
    {
        public int SchemaVersion { get; set; } = CurrentVersion;
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public WindowMatch WindowMatch { get; set; } = new();
        public LanguageOptions Languages { get; set; } = new();
        public Capture Capture { get; set; } = new();
        public Ocr Ocr { get; set; } = new();
        public Translation Translation { get; set; } = new();
        public OverlayOptions Overlay { get; set; } = new();
        public CacheOptions Cache { get; set; } = new();
        public StabilizationOptions Stabilization { get; set; } = new();
        public Privacy Privacy { get; set; } = new();
        public int RetainedLogFileCount { get; set; } = 7;
        [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; set; }
    }
    private sealed class WindowMatch
    { public string? ProcessHint { get; set; } public string? TitlePattern { get; set; } }
    private sealed class Capture
    { public int Fps { get; set; } = 8; public int QueueCapacity { get; set; } = 2; public List<Region> Regions { get; set; } = new(); }
    private sealed class Region
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int SortOrder { get; set; }
    }
    private sealed class Ocr
    {
        public string LanguageModel { get; set; } = "en";
        public double Confidence { get; set; } = .65;
        public PreprocessingOptions Preprocessing { get; set; } = PreprocessingOptions.PlainSubtitle;
    }
    private sealed class Translation
    { public string ProviderId { get; set; } = "unconfigured"; public string Model { get; set; } = string.Empty; }
    private sealed class Privacy { public bool HistoryEnabled { get; set; } }
}

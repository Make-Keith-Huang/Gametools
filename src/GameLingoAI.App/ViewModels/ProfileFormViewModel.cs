using System.Text.Json;
using System.Text.Json.Nodes;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.App.ViewModels;

/// <summary>Typed scalar controls over the allowlisted portable profile shape, never an arbitrary imported object.</summary>
public sealed class ProfileFormViewModel : ObservableObject
{
    private readonly JsonObject _document;
    private readonly Action _changed;
    private readonly Func<bool> _canEdit;
    public ProfileFormViewModel(GameProfile profile, Func<bool> canEdit, Action changed)
    {
        _document = JsonNode.Parse(ProfileJson.Export(profile))!.AsObject();
        _document["windowMatch"]!["processHint"] ??= null;
        _document["windowMatch"]!["titlePattern"] ??= null;
        _document["ocr"]!["preprocessing"]!["threshold"] ??= null;
        _changed = changed;
        _canEdit = canEdit;
        RegionNumbers = Enumerable.Range(1, profile.Regions.Count).ToArray();
        var groups = new List<ProfileFormGroup>();
        foreach (var property in _document)
        {
            if (property.Key is "schemaVersion" or "id" || property.Key.StartsWith("x-", StringComparison.Ordinal))
            { continue; }
            var fields = new List<ProfileFormField>();
            AddFields(_document, property.Key, property.Key, fields);
            groups.Add(new ProfileFormGroup(Label(property.Key), fields));
        }
        Groups = groups;
    }
    public IReadOnlyList<ProfileFormGroup> Groups { get; }
    public IReadOnlyList<int> RegionNumbers { get; }
    public bool HasErrors => Groups.SelectMany(group => group.Fields).Any(field => field.Error.Length != 0);
    public string Json => _document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private void AddFields(JsonObject parent, string key, string path, List<ProfileFormField> fields)
    {
        var node = parent[key];
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            { if (property.Key is not ("id" or "preset")) { AddFields(obj, property.Key, path + "." + property.Key, fields); } }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                var region = array[index]!.AsObject();
                foreach (var property in region)
                { if (property.Key != "id") { AddFields(region, property.Key, $"{path}[{index + 1}].{property.Key}", fields); } }
            }
        }
        else
        {
            var kind = node?.GetValueKind() ?? (key == "threshold" ? JsonValueKind.Number : JsonValueKind.String);
            fields.Add(new ProfileFormField(path, Label(path), node?.ToString() ?? string.Empty, kind,
                node is null || key is "threshold" or "processHint" or "titlePattern", _canEdit, replacement =>
                {
                    parent[key] = replacement;
                    if (path.StartsWith("ocr.preprocessing.", StringComparison.Ordinal))
                    { _document["ocr"]!["preprocessing"]!["preset"] = "custom"; }
                    OnPropertyChanged(nameof(HasErrors));
                    _changed();
                }));
        }
    }

    private static string Label(string path)
    {
        var words = new System.Text.StringBuilder();
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (character == '.')
            { words.Append(" / "); }
            else
            { if (char.IsUpper(character) && index > 0) { words.Append(' '); } words.Append(character); }
        }
        var label = words.ToString();
        return char.ToUpperInvariant(label[0]) + label[1..];
    }
}

public sealed record ProfileFormGroup(string Name, IReadOnlyList<ProfileFormField> Fields);

public sealed class ProfileFormField : ObservableObject
{
    private readonly JsonValueKind _kind;
    private readonly bool _nullable;
    private readonly Func<bool> _canEdit;
    private readonly Action<JsonNode?> _changed;
    private string _text, _error = string.Empty;
    private JsonNode? _lastValid;
    public ProfileFormField(string path, string label, string text, JsonValueKind kind, bool nullable,
        Func<bool> canEdit, Action<JsonNode?> changed)
    {
        Path = path;
        Label = label;
        _text = text;
        _kind = kind;
        _nullable = nullable;
        _canEdit = canEdit;
        _changed = changed;
        _lastValid = kind == JsonValueKind.String ? JsonValue.Create(text) : text.Length == 0 ? null : JsonNode.Parse(text.ToLowerInvariant());
    }
    public string Path { get; }
    public string Label { get; }
    public bool IsBoolean => _kind is JsonValueKind.True or JsonValueKind.False;
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public bool BooleanValue { get => string.Equals(_text, "true", StringComparison.OrdinalIgnoreCase); set => Text = value ? "true" : "false"; }
    public string Text
    {
        get => _text;
        set
        {
            if (!_canEdit() || !SetProperty(ref _text, value))
            { return; }
            try
            {
                JsonNode? parsed = _nullable && string.IsNullOrWhiteSpace(value) ? null :
                    _kind == JsonValueKind.String ? JsonValue.Create(value) : JsonNode.Parse(value);
                if (parsed is not null && (_kind == JsonValueKind.Number &&
                    (parsed.GetValueKind() != JsonValueKind.Number || !double.IsFinite(parsed.GetValue<double>())) ||
                    IsBoolean && parsed.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)))
                { throw new JsonException(); }
                if (parsed is null && !_nullable)
                { throw new JsonException(); }
                Error = string.Empty;
                _lastValid = parsed?.DeepClone();
                _changed(parsed);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                Error = "Enter a finite number using '.' as decimal separator, or a valid value. Save is blocked.";
                // Preserve unfinished input visibly, but never let Save silently use the old numeric value.
                _changed(_lastValid?.DeepClone());
            }
            OnPropertyChanged(nameof(BooleanValue));
        }
    }
}

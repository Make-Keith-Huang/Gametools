namespace GameLingoAI.Core.Configuration;

public enum HotkeyAction
{
    Start,
    PauseResume,
    Stop,
    ToggleOverlay,
    EmergencyStop
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4
}

/// <summary>Declarative shortcut only; no arbitrary keyboard-event history is stored.</summary>
public sealed record HotkeyBinding(HotkeyAction Action, HotkeyModifiers Modifiers, string Key);

public sealed record HotkeyOptions
{
    public bool Enabled { get; init; }

    public IReadOnlyList<HotkeyBinding> Bindings { get; init; } = Array.Empty<HotkeyBinding>();
}

public static class HotkeyOptionsValidator
{
    public static IReadOnlyList<ConfigurationError> Validate(HotkeyOptions? options)
    {
        var errors = new List<ConfigurationError>();
        if (options?.Bindings is null || options.Bindings.Count > 5)
        {
            errors.Add(new("hotkeys.bindings", "Provide at most five shortcut definitions."));
            return errors;
        }
        if (options.Enabled && options.Bindings.Count == 0)
        { errors.Add(new("hotkeys.bindings", "Choose at least one shortcut before enabling hotkeys.")); }
        var actions = new HashSet<HotkeyAction>();
        var chords = new HashSet<(HotkeyModifiers, string)>();
        for (var index = 0; index < options.Bindings.Count; index++)
        {
            var binding = options.Bindings[index];
            var field = $"hotkeys.bindings[{index}]";
            if (binding is null)
            { errors.Add(new(field, "A shortcut definition is required.")); continue; }
            if (!Enum.IsDefined(binding.Action) || !actions.Add(binding.Action))
            { errors.Add(new(field + ".action", "Choose a supported action only once.")); }
            const HotkeyModifiers allowed = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift;
            if ((binding.Modifiers & ~allowed) != 0 ||
                (binding.Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt)) == 0)
            { errors.Add(new(field + ".modifiers", "Use Control and/or Alt, optionally Shift; Windows-key shortcuts are not supported.")); }
            var key = NormalizeKey(binding.Key);
            if (key is null || binding.Modifiers == HotkeyModifiers.Alt && key == "F4")
            { errors.Add(new(field + ".key", "Choose A-Z, 0-9 or F1-F11. Alt+F4 and debugger-reserved F12 are unavailable.")); }
            else if (!chords.Add((binding.Modifiers, key)))
            { errors.Add(new(field + ".key", "Each shortcut combination must be unique.")); }
        }
        return errors;
    }

    public static string? NormalizeKey(string? key)
    {
        if (key is null || key.Length is < 1 or > 3)
        { return null; }
        var normalized = key.ToUpperInvariant();
        if (normalized.Length == 1 && (normalized[0] is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        { return normalized; }
        return normalized is "F1" or "F2" or "F3" or "F4" or "F5" or "F6" or "F7" or "F8" or "F9" or "F10" or "F11"
            ? normalized : null;
    }
}

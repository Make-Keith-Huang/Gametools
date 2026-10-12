using System.Text.Json.Nodes;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Infrastructure.Configuration;

internal static class HotkeyConfigurationTests
{
    public static async Task RunAsync()
    {
        var binding = new HotkeyBinding(HotkeyAction.EmergencyStop, HotkeyModifiers.Control | HotkeyModifiers.Alt, "F8");
        var options = new HotkeyOptions { Enabled = true, Bindings = new[] { binding } };
        Assert(HotkeyOptionsValidator.Validate(options).Count == 0, "Valid shortcut was rejected.");
        Assert(!new HotkeyOptions().Enabled && new HotkeyOptions().Bindings.Count == 0, "Shortcuts must default to inactive.");
        foreach (var invalid in new HotkeyOptions?[]
        {
            null,
            new() { Bindings = null! },
            new() { Enabled = true },
            new() { Bindings = Enumerable.Repeat(binding, 6).ToArray() },
            new() { Bindings = new HotkeyBinding[] { null! } },
            new() { Bindings = new[] { binding, binding with { Key = "F9" } } },
            new() { Bindings = new[] { binding, binding with { Action = HotkeyAction.Start, Key = "f8" } } },
            new() { Bindings = new[] { binding with { Action = (HotkeyAction)99 } } },
            new() { Bindings = new[] { binding with { Modifiers = HotkeyModifiers.None } } },
            new() { Bindings = new[] { binding with { Modifiers = HotkeyModifiers.Shift } } },
            new() { Bindings = new[] { binding with { Modifiers = (HotkeyModifiers)8 } } },
            new() { Bindings = new[] { binding with { Modifiers = HotkeyModifiers.Alt, Key = "F4" } } }
        })
        { Assert(HotkeyOptionsValidator.Validate(invalid).Count > 0, "Invalid hotkey configuration was accepted."); }
        foreach (var key in new string?[] { null, "", "F12", "Delete", "Tab", " a", "F01", "é", "🔥", "\n" })
        { Assert(HotkeyOptionsValidator.NormalizeKey(key) is null, "Unsupported key was accepted."); }
        foreach (var (key, expected) in new[] { ("a", "A"), ("Z", "Z"), ("0", "0"), ("9", "9"), ("f1", "F1"), ("F11", "F11") })
        { Assert(HotkeyOptionsValidator.NormalizeKey(key) == expected, "Key normalization failed."); }

        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-HotkeyConfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonConfigurationStore(path);
            var settings = new AppSettings { Hotkeys = options };
            await store.SaveAsync(settings, CancellationToken.None);
            var loaded = await store.LoadAsync(CancellationToken.None);
            Assert(loaded.IsSuccess && loaded.Value!.Hotkeys.Enabled && loaded.Value.Hotkeys.Bindings.SequenceEqual(options.Bindings),
                "Hotkey settings did not persist.");
            var exported = await store.ExportSanitizedAsync(settings, CancellationToken.None);
            Assert(JsonNode.Parse(exported)!["hotkeys"]!["enabled"]!.GetValue<bool>(), "Sanitized export lost shortcut configuration.");
            try
            {
                await store.SaveAsync(settings with { Hotkeys = new HotkeyOptions { Enabled = true } }, CancellationToken.None);
                throw new InvalidOperationException("Invalid shortcut settings were saved.");
            }
            catch (ConfigurationValidationException) { }
            Assert((await store.LoadAsync(CancellationToken.None)).Value!.Hotkeys.Enabled, "Invalid save changed valid settings.");
            for (var version = 1; version <= 3; version++)
            {
                var legacy = JsonNode.Parse(exported)!;
                legacy["schemaVersion"] = version;
                var legacyJson = legacy.ToJsonString();
                await File.WriteAllTextAsync(path, legacyJson);
                var migrated = await store.LoadAsync(CancellationToken.None);
                Assert(migrated.IsSuccess && migrated.Value!.SchemaVersion == 4 && !migrated.Value.Hotkeys.Enabled &&
                    migrated.Value.Hotkeys.Bindings.Count == 0, "Legacy unknown hotkey field was activated.");
                Assert(await File.ReadAllTextAsync(path) == legacyJson, "Loading a migration rewrote the source file.");
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("Hotkey configuration: safe defaults, bounded chords/actions, conflicts, persistence and non-activating migration passed.");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

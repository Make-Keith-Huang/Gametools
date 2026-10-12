using System.Text.Json.Nodes;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

internal static class ProfileJsonTests
{
    public static Task RunAsync()
    {
        var profile = new GameProfile(new ProfileId(Guid.Parse("11111111-1111-1111-1111-111111111111")), "Example", "ja", "zh-Hans", 12,
            new[] { new CaptureRegion(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Dialogue", new NormalizedRegion(.1, .7, .8, .2), 0) },
            true, PreprocessingOptions.OutlinedSubtitle, new ProfileSettings
            {
                ProviderId = "ollama",
                Model = "fixture-model",
                ProcessHint = "example",
                WindowTitlePattern = "Example.*",
                SecretReference = "private-credential-reference",
                OcrLanguageModel = "ja",
                OcrMinimumConfidence = .75,
                CaptureQueueCapacity = 1,
                RetainedLogFileCount = 3,
                Overlay = new OverlayOptions { FontSize = 40 },
                Cache = new CacheOptions { MaximumRows = 123 },
                Stabilization = new StabilizationOptions { RequiredObservations = 3 },
                SafeExtensionsJson = "{\"x-feature\":true,\"x-limit\":42}"
            });
        var json = ProfileJson.Export(profile);
        var result = ProfileJson.Import(json);
        Assert(!result.IsReadOnly && result.SchemaVersion == 1 && result.Profile is not null, "Current schema did not import.");
        var loaded = result.Profile!;
        Assert(loaded.Id == profile.Id && loaded.Name == profile.Name && loaded.HistoryEnabled && loaded.CaptureFramesPerSecond == 12 &&
            loaded.Preprocessing == profile.Preprocessing && loaded.Regions.SequenceEqual(profile.Regions) &&
            loaded.Settings == profile.Settings with { SecretReference = null }, "Portable settings did not round-trip.");
        Assert(ProfileJson.Export(loaded) == json, "Canonical portable document changed on round-trip.");
        Assert(!json.Contains("private-credential-reference", StringComparison.Ordinal) && !json.Contains("secretReference", StringComparison.Ordinal),
            "Portable export included a credential lookup reference.");
        var root = JsonNode.Parse(json)!.AsObject();
        Assert(root["schemaVersion"]!.GetValue<int>() == 1 && root["id"]!.GetValue<string>() == "11111111-1111-1111-1111-111111111111" &&
            root["capture"]!["regions"]![0]!["width"]!.GetValue<double>() == .8 && root["translation"]!["model"]!.GetValue<string>() == "fixture-model",
            "Version-1 golden document fields changed.");
        root["apiKey"] = "synthetic-secret-never-retain";
        root["privatePath"] = @"C:\Users\private\fixture";
        root["x-unsafe"] = "synthetic-secret-never-retain";
        root["translation"]!["secretRef"] = "synthetic-secret-never-retain";
        root["privacy"]!["remoteTextProcessingConsent"] = true;
        var sanitized = ProfileJson.Import(root.ToJsonString()).Profile!;
        var sanitizedJson = ProfileJson.Export(sanitized);
        Assert(!sanitizedJson.Contains("synthetic-secret", StringComparison.Ordinal) && !sanitizedJson.Contains("privatePath", StringComparison.Ordinal) &&
            sanitized.Settings.SecretReference is null && !GameProfileValidator.ToSettings(sanitized, new AppSettings()).Privacy.RemoteTextProcessingConsent,
            "Import propagated a credential/path/consent from an unknown field.");
        var duplicate = ProfileJson.Duplicate(profile, "Copy");
        Assert(duplicate.Id != profile.Id && duplicate.Regions[0].Id != profile.Regions[0].Id && duplicate.Settings == profile.Settings &&
            duplicate.Regions[0].Bounds == profile.Regions[0].Bounds, "Duplicate identity/settings semantics failed.");
        root["schemaVersion"] = 999;
        var future = ProfileJson.Import(root.ToJsonString());
        Assert(future.IsReadOnly && future.Profile is null && future.SchemaVersion == 999, "Future schema was editable.");
        Throws(() => ProfileJson.Import(new string('x', ProfileJson.MaximumBytes + 1)));
        Throws(() => ProfileJson.Import(new string('界', ProfileJson.MaximumBytes / 2)));
        Throws(() => ProfileJson.Import("{\"schemaVersion\":1,\"schemaVersion\":999}"));
        Throws(() => ProfileJson.Import("{\"schemaVersion\":1,\"SchemaVersion\":999}"));
        Throws(() => ProfileJson.Import("{\"schemaVersion\":0}"));
        Throws(() => ProfileJson.Import("{}"));
        Throws(() => ProfileJson.Import("{\"schemaVersion\":\"1\"}"));
        Throws(() => ProfileJson.Import("{\"schemaVersion\":1,\"nested\":" + new string('[', 20) + "0" + new string(']', 20) + "}"));
        foreach (var mutate in new Action<JsonObject>[]
        {
            value => value["overlay"] = null,
            value => value["capture"]!["regions"] = new JsonArray(),
            value => value["capture"]!["regions"]![0]!["width"] = 2,
            value => value["windowMatch"]!["processHint"] = @"C:\private\game.exe",
            value => value["windowMatch"]!["titlePattern"] = "[",
            value => value["ocr"]!["confidence"] = 2,
            value => value["capture"]!["fps"] = 31
        })
        {
            var invalid = JsonNode.Parse(json)!.AsObject();
            mutate(invalid);
            Throws(() => ProfileJson.Import(invalid.ToJsonString()));
        }
        Console.WriteLine("Profile JSON: bounded round-trip, golden fields, sanitized unknown data, future read-only, hostile input and duplicate IDs passed.");
        return Task.CompletedTask;
    }

    private static void Throws(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid profile JSON was accepted."); }
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

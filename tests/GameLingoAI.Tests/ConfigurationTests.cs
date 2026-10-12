using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Configuration;

internal static class ConfigurationTests
{
    public static async Task RunAsync()
    {
        TestDefaultsAndRangeEdges();
        TestInvalidSettingsAggregateFields();

        var root = Path.Combine(Path.GetTempPath(), "GameLingoAI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await AppDataPathTests.RunAsync(root).ConfigureAwait(false);
            await TestRoundTripAndRedactionAsync(root).ConfigureAwait(false);
            await TestMigrationAndCorruptionAsync(root).ConfigureAwait(false);
            await TestSecretStoreAndCancellationAsync(root).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static void TestDefaultsAndRangeEdges()
    {
        Assert(AppSettingsValidator.Validate(new AppSettings()).IsValid, "Default settings must be valid.");

        var minimum = CreateSettings() with
        {
            Capture = new CaptureOptions { FramesPerSecond = 1, QueueCapacity = 1 },
            Ocr = new OcrOptions
            {
                LanguageModel = "en",
                MinimumConfidence = 0,
                Preprocessing = PreprocessingOptions.NoOp with
                {
                    Grayscale = true,
                    Contrast = 0.25,
                    Scale = 0.5,
                    MaximumOutputWidth = 64,
                    MaximumOutputHeight = 64,
                    MaximumOutputPixels = 4096
                }
            },
            Stabilization = new StabilizationOptions
            {
                RequiredObservations = 1,
                ObservationWindowMilliseconds = 50,
                MinimumAverageConfidence = 0,
                SimilarityThreshold = 0.5,
                SpatialOverlapThreshold = 0,
                ImmediateChangeConfidence = 0.5,
                DisappearanceObservations = 1,
                MaximumCandidates = 1,
                MaximumSpansPerObservation = 1
            },
            Cache = new CacheOptions { MaximumRows = 0, MaximumMegabytes = 1 },
            Overlay = new OverlayOptions { FontSize = 8, MaximumLines = 1, BackgroundOpacity = 0 },
            Logging = new LoggingOptions { RetainedFileCount = 1 }
        };
        Assert(AppSettingsValidator.Validate(minimum).IsValid, "Minimum range edges must be valid.");

        var maximum = CreateSettings() with
        {
            Capture = new CaptureOptions { FramesPerSecond = 30, QueueCapacity = 2 },
            Ocr = new OcrOptions
            {
                LanguageModel = "en",
                MinimumConfidence = 1,
                Preprocessing = PreprocessingOptions.OutlinedSubtitle with
                {
                    Contrast = 4,
                    Scale = 4,
                    PaddingPixels = 64,
                    MaximumOutputWidth = 8192,
                    MaximumOutputHeight = 8192,
                    MaximumOutputPixels = 33_554_432
                }
            },
            Stabilization = new StabilizationOptions
            {
                RequiredObservations = 10,
                ObservationWindowMilliseconds = 5_000,
                MinimumAverageConfidence = 1,
                SimilarityThreshold = 1,
                SpatialOverlapThreshold = 1,
                ImmediateChangeConfidence = 1,
                DisappearanceObservations = 10,
                MaximumCandidates = 32,
                MaximumSpansPerObservation = 512
            },
            Cache = new CacheOptions { MaximumRows = 1_000_000, MaximumMegabytes = 4_096 },
            Overlay = new OverlayOptions { FontSize = 96, MaximumLines = 6, BackgroundOpacity = 1 },
            Logging = new LoggingOptions { RetainedFileCount = 30 }
        };
        Assert(AppSettingsValidator.Validate(maximum).IsValid, "Maximum range edges must be valid.");

        var loopback = CreateSettings() with
        {
            Providers = new[]
            {
                new ProviderOptions
                {
                    Id = "ollama",
                    Kind = ProviderKind.Ollama,
                    BaseUri = new Uri("http://127.0.0.1:11434"),
                    Model = "local",
                    TimeoutSeconds = 1
                }
            }
        };
        Assert(AppSettingsValidator.Validate(loopback).IsValid, "Loopback HTTP Ollama must be valid.");
    }

    private static void TestInvalidSettingsAggregateFields()
    {
        var invalid = CreateSettings() with
        {
            SchemaVersion = 99,
            Capture = new CaptureOptions { FramesPerSecond = 0, QueueCapacity = 3 },
            Ocr = new OcrOptions
            {
                LanguageModel = string.Empty,
                MinimumConfidence = double.NaN,
                Preprocessing = PreprocessingOptions.PlainSubtitle with
                {
                    Contrast = 0,
                    Scale = 5,
                    PaddingPixels = 65,
                    MaximumOutputWidth = 63,
                    MaximumOutputHeight = 9000,
                    MaximumOutputPixels = 100,
                    EnableDiagnosticImages = true
                }
            },
            Stabilization = new StabilizationOptions
            {
                RequiredObservations = 0,
                ObservationWindowMilliseconds = 5_001,
                MinimumAverageConfidence = -1,
                SimilarityThreshold = 0.4,
                SpatialOverlapThreshold = 2,
                ImmediateChangeConfidence = double.NaN,
                DisappearanceObservations = 0,
                MaximumCandidates = 33,
                MaximumSpansPerObservation = 0
            },
            Languages = new LanguageOptions { Source = "not_a_language", Target = "also_bad" },
            Providers = new[]
            {
                new ProviderOptions
                {
                    Id = "duplicate",
                    Kind = ProviderKind.CustomHttp,
                    BaseUri = new Uri("http://example.com"),
                    Model = string.Empty,
                    TimeoutSeconds = 0
                },
                new ProviderOptions
                {
                    Id = "duplicate",
                    Kind = ProviderKind.OpenAiCompatible,
                    BaseUri = null,
                    Model = "model",
                    TimeoutSeconds = 121
                }
            },
            Cache = new CacheOptions { MaximumRows = -1, MaximumMegabytes = 0 },
            Overlay = new OverlayOptions { FontSize = 7, MaximumLines = 7, BackgroundOpacity = 2 },
            Logging = new LoggingOptions { RetainedFileCount = 0, IncludeRecognizedText = true }
        };

        var validation = AppSettingsValidator.Validate(invalid);
        Assert(!validation.IsValid, "Invalid settings must fail.");
        var fields = validation.Errors.Select(error => error.Field).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
        {
            "schemaVersion", "capture.framesPerSecond", "capture.queueCapacity",
            "ocr.minimumConfidence", "ocr.preprocessing.contrast", "ocr.preprocessing.scale",
            "ocr.preprocessing.enableDiagnosticImages", "stabilization.similarityThreshold",
            "stabilization.maximumCandidates", "languages.source", "providers[0].baseUri",
            "providers[1].id", "cache.maximumRows", "overlay.fontSize",
            "logging.includeRecognizedText"
        })
        {
            Assert(fields.Contains(expected), $"Expected field-specific validation for {expected}.");
        }
    }

    private static async Task TestRoundTripAndRedactionAsync(string root)
    {
        const string secretValue = "test-secret-must-not-be-persisted";
        var path = Path.Combine(root, "roundtrip.json");
        var store = new JsonConfigurationStore(path);
        var settings = CreateSettings();
        await store.SaveAsync(settings, CancellationToken.None).ConfigureAwait(false);

        var persisted = await File.ReadAllTextAsync(path).ConfigureAwait(false);
        Assert(!persisted.Contains(secretValue, StringComparison.Ordinal), "Secret material must not enter JSON.");
        Assert(persisted.Contains("provider-key", StringComparison.Ordinal), "Opaque secret reference should persist.");

        var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert(loaded.IsSuccess, loaded.Failure?.Message ?? "Round-trip load failed.");
        Assert(loaded.Value!.Capture.FramesPerSecond == settings.Capture.FramesPerSecond, "Capture setting must round-trip.");
        Assert(loaded.Value.Ocr.Preprocessing.Preset == settings.Ocr.Preprocessing.Preset,
            "Preprocessing preset must round-trip.");
        Assert(loaded.Value.Providers[0].SecretReference == "provider-key", "Secret reference must round-trip.");

        var exported = await store.ExportSanitizedAsync(settings, CancellationToken.None).ConfigureAwait(false);
        Assert(!exported.Contains("provider-key", StringComparison.Ordinal), "Sanitized export must remove secret references.");
        Assert(!exported.Contains(secretValue, StringComparison.Ordinal), "Sanitized export must not contain secret material.");
    }

    private static async Task TestMigrationAndCorruptionAsync(string root)
    {
        var migrationPath = Path.Combine(root, "migration.json");
        await File.WriteAllTextAsync(migrationPath, "{\"schemaVersion\":1}").ConfigureAwait(false);
        var migration = await new JsonConfigurationStore(migrationPath)
            .LoadAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert(migration.IsSuccess, migration.Failure?.Message ?? "Migration failed.");
        Assert(migration.Value!.SchemaVersion == AppSettings.CurrentSchemaVersion, "v1 must migrate to current schema.");
        Assert(!migration.Value.Privacy.DiagnosticScreenshotsEnabled, "Migrated screenshot diagnostics must be disabled.");

        var versionTwoPath = Path.Combine(root, "migration-v2.json");
        await File.WriteAllTextAsync(
            versionTwoPath,
            "{\"schemaVersion\":2,\"stabilization\":{\"requiredObservations\":3,\"observationWindowMilliseconds\":700}}")
            .ConfigureAwait(false);
        var versionTwo = await new JsonConfigurationStore(versionTwoPath)
            .LoadAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert(versionTwo.IsSuccess && versionTwo.Value!.SchemaVersion == AppSettings.CurrentSchemaVersion,
            "v2 configuration must migrate to the stabilization schema.");
        var migratedVersionTwo = versionTwo.Value ?? throw new InvalidOperationException("v2 migration returned no settings.");
        Assert(migratedVersionTwo.Stabilization.RequiredObservations == 3 &&
            migratedVersionTwo.Stabilization.MaximumCandidates == 8,
            "v2 migration must retain existing values and add bounded stabilization defaults.");

        var corruptPath = Path.Combine(root, "corrupt.json");
        await File.WriteAllTextAsync(corruptPath, "{not-json").ConfigureAwait(false);
        var corrupt = await new JsonConfigurationStore(corruptPath)
            .LoadAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert(!corrupt.IsSuccess && corrupt.Failure!.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase),
            "Corrupt JSON must return an actionable failure.");

        var futurePath = Path.Combine(root, "future.json");
        await File.WriteAllTextAsync(futurePath, "{\"schemaVersion\":999}").ConfigureAwait(false);
        var future = await new JsonConfigurationStore(futurePath)
            .LoadAsync(CancellationToken.None)
            .ConfigureAwait(false);
        Assert(!future.IsSuccess && future.Failure!.Message.Contains("newer", StringComparison.OrdinalIgnoreCase),
            "Future schema must be rejected without overwrite.");
    }

    private static async Task TestSecretStoreAndCancellationAsync(string root)
    {
        var secrets = new FakeSecretStore();
        await secrets.SetAsync("provider-key", "value", CancellationToken.None).ConfigureAwait(false);
        Assert(await secrets.GetAsync("provider-key", CancellationToken.None).ConfigureAwait(false) == "value",
            "Fake secret should round-trip in isolation.");
        await secrets.DeleteAsync("provider-key", CancellationToken.None).ConfigureAwait(false);
        Assert(await secrets.GetAsync("provider-key", CancellationToken.None).ConfigureAwait(false) is null,
            "Deleted fake secret should be absent.");

        var store = new JsonConfigurationStore(Path.Combine(root, "cancelled.json"));
        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => store.LoadAsync(source.Token)).ConfigureAwait(false);
        await AssertThrowsAsync<OperationCanceledException>(() => store.SaveAsync(CreateSettings(), source.Token)).ConfigureAwait(false);
        await AssertThrowsAsync<OperationCanceledException>(() => secrets.SetAsync("key", "value", source.Token)).ConfigureAwait(false);
    }

    private static AppSettings CreateSettings() => new()
    {
        Languages = new LanguageOptions { Source = "ja", Target = "zh-Hans" },
        Providers = new[]
        {
            new ProviderOptions
            {
                Id = "remote",
                Kind = ProviderKind.OpenAiCompatible,
                BaseUri = new Uri("https://example.com/v1/"),
                Model = "model",
                SecretReference = "provider-key",
                EndpointDialect = OpenAiEndpointDialect.ChatCompletions,
                TimeoutSeconds = 30
            }
        }
    };

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_secrets.GetValueOrDefault(key));
        }

        public Task SetAsync(string key, string secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets.Remove(key);
            return Task.CompletedTask;
        }
    }
}

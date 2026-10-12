using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Configuration;

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _filePath;

    public JsonConfigurationStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = Path.GetFullPath(filePath);
    }

    public async Task<OperationResult<AppSettings>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_filePath))
        {
            return OperationResult.Success(new AppSettings());
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var migrated = Migrate(json);
            if (!migrated.IsSuccess)
            {
                return OperationResult.Failed<AppSettings>(migrated.Failure!);
            }

            var settings = migrated.Value!.Deserialize<AppSettings>(SerializerOptions);
            if (settings is null)
            {
                return Failed("Configuration is empty or has an invalid root object.");
            }

            var validation = AppSettingsValidator.Validate(settings);
            if (!validation.IsValid)
            {
                return Failed(string.Join("; ", validation.Errors.Select(error => $"{error.Field}: {error.Message}")));
            }

            return OperationResult.Success(settings);
        }
        catch (JsonException exception)
        {
            return Failed($"Configuration JSON is corrupt: {exception.Message}");
        }
        catch (IOException exception)
        {
            return Failed($"Configuration could not be read: {exception.Message}", isTransient: true);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = AppSettingsValidator.Validate(settings);
        if (!validation.IsValid)
        {
            throw new ConfigurationValidationException(validation.Errors);
        }

        var directory = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(settings, SerializerOptions);

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, _filePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var sanitized = settings with
        {
            Providers = settings.Providers
                .Select(provider => provider with
                {
                    SecretReference = null,
                    CustomHttp = provider.CustomHttp is null ? null : provider.CustomHttp with
                    {
                        Headers = provider.CustomHttp.Headers.Select(header =>
                            header with { Value = null, SecretReference = null }).ToArray()
                    }
                })
                .ToArray(),
            Logging = settings.Logging with { IncludeRecognizedText = false },
            Privacy = settings.Privacy with { DiagnosticScreenshotsEnabled = false }
        };
        return Task.FromResult(JsonSerializer.Serialize(sanitized, SerializerOptions));
    }

    private static OperationResult<JsonObject> Migrate(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null)
        {
            return OperationResult.Failed<JsonObject>(CreateFailure("Configuration root must be a JSON object."));
        }

        var schemaVersion = root["schemaVersion"]?.GetValue<int?>();
        if (schemaVersion is null or < 1)
        {
            return OperationResult.Failed<JsonObject>(CreateFailure("Configuration schemaVersion is missing or invalid."));
        }

        if (schemaVersion > AppSettings.CurrentSchemaVersion)
        {
            return OperationResult.Failed<JsonObject>(CreateFailure(
                $"Configuration schema version {schemaVersion} is newer than supported version {AppSettings.CurrentSchemaVersion}."));
        }

        if (schemaVersion == 1)
        {
            var privacy = root["privacy"] as JsonObject ?? new JsonObject();
            privacy["diagnosticScreenshotsEnabled"] ??= false;
            root["privacy"] = privacy;
            schemaVersion = 2;
        }

        if (schemaVersion == 2)
        {
            var defaults = new StabilizationOptions();
            var stabilization = root["stabilization"] as JsonObject ?? new JsonObject();
            stabilization["minimumAverageConfidence"] ??= defaults.MinimumAverageConfidence;
            stabilization["similarityThreshold"] ??= defaults.SimilarityThreshold;
            stabilization["spatialOverlapThreshold"] ??= defaults.SpatialOverlapThreshold;
            stabilization["immediateChangeConfidence"] ??= defaults.ImmediateChangeConfidence;
            stabilization["disappearanceObservations"] ??= defaults.DisappearanceObservations;
            stabilization["maximumCandidates"] ??= defaults.MaximumCandidates;
            stabilization["maximumSpansPerObservation"] ??= defaults.MaximumSpansPerObservation;
            root["stabilization"] = stabilization;
        }

        if (schemaVersion <= 3)
        {
            // Earlier schemas had no hotkey consent/configuration. Never activate an unknown legacy field.
            root["hotkeys"] = JsonSerializer.SerializeToNode(new HotkeyOptions(), SerializerOptions);
        }
        root["schemaVersion"] = AppSettings.CurrentSchemaVersion;

        return OperationResult.Success(root);
    }

    private static OperationResult<AppSettings> Failed(string message, bool isTransient = false) =>
        OperationResult.Failed<AppSettings>(CreateFailure(message, isTransient));

    private static PipelineFailure CreateFailure(string message, bool isTransient = false) =>
        new(FailureCode.Validation, message, CorrelationId.New(), isTransient);
}

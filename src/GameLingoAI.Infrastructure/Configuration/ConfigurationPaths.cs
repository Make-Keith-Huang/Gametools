namespace GameLingoAI.Infrastructure.Configuration;

internal static class ConfigurationPaths
{
    public static string GetDefaultSettingsPath() => AppDataPaths.Settings;

    public static string GetDefaultOcrModelsPath() => AppDataPaths.OcrModels;
}

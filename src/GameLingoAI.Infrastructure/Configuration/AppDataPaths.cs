namespace GameLingoAI.Infrastructure.Configuration;

/// <summary>Application-owned data paths; explicit relocation never migrates existing data or falls back.</summary>
public static class AppDataPaths
{
    /// <summary>Optional process environment setting, selected before application startup.</summary>
    public const string EnvironmentVariable = "GAMELINGOAI_DATA_ROOT";

    /// <summary>Resolves the selected local directory without creating it or modifying existing data.</summary>
    public static string GetRoot()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (configured is null or "")
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLingoAI");
        }

        if (!Path.IsPathFullyQualified(configured) || configured.StartsWith(@"\\", StringComparison.Ordinal))
        { throw new ArgumentException("GAMELINGOAI_DATA_ROOT must be an absolute local directory, not a network or device path."); }
        var root = Path.GetFullPath(configured);
        var drive = Path.GetPathRoot(root)!;
        if (!Directory.Exists(drive))
        { throw new DriveNotFoundException("The selected GameLingoAI data drive is unavailable. No fallback directory was used."); }
        if (string.Equals(Path.TrimEndingDirectorySeparator(root), Path.TrimEndingDirectorySeparator(drive), StringComparison.OrdinalIgnoreCase)
            || File.Exists(root))
        { throw new ArgumentException("Select a dedicated GameLingoAI data directory, not a drive root or file."); }
        return root;
    }

    /// <summary>Settings JSON path.</summary>
    public static string Settings => Path.Combine(GetRoot(), "settings.json");
    /// <summary>Local OCR model directory; Ollama manages its own separate models.</summary>
    public static string OcrModels => Path.Combine(GetRoot(), "models", "ocr");
    /// <summary>Persistent database path.</summary>
    public static string Database => Path.Combine(GetRoot(), "gamelingo.db");
    /// <summary>Bounded production diagnostic directory.</summary>
    public static string Logs => Path.Combine(GetRoot(), "logs");
    /// <summary>Saved capture-region draft path.</summary>
    public static string RegionDraft => Path.Combine(GetRoot(), "region-draft.json");
}

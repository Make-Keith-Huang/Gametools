using System.Text.Json.Serialization;

namespace GameLingoAI.Core.Models;

public sealed record CaptureRegion
{
    [JsonConstructor]
    public CaptureRegion(Guid id, string name, NormalizedRegion bounds, int sortOrder)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(sortOrder);
        Id = id;
        Name = name.Trim();
        Bounds = bounds;
        SortOrder = sortOrder;
    }

    public Guid Id { get; }

    public string Name { get; }

    public NormalizedRegion Bounds { get; }

    public int SortOrder { get; }
}

/// <summary>Portable, non-secret settings required to resume a user-authorized game profile.</summary>
public sealed record GameProfile
{
    [JsonConstructor]
    public GameProfile(
        ProfileId id,
        string name,
        string sourceLanguage,
        string targetLanguage,
        int captureFramesPerSecond,
        IReadOnlyList<CaptureRegion> regions,
        bool historyEnabled,
        PreprocessingOptions? preprocessing = null,
        ProfileSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetLanguage);
        if (captureFramesPerSecond is < 1 or > 30)
        {
            throw new ArgumentOutOfRangeException(nameof(captureFramesPerSecond));
        }

        Id = id;
        Name = name;
        SourceLanguage = sourceLanguage;
        TargetLanguage = targetLanguage;
        CaptureFramesPerSecond = captureFramesPerSecond;
        Regions = regions ?? throw new ArgumentNullException(nameof(regions));
        HistoryEnabled = historyEnabled;
        Preprocessing = preprocessing ?? PreprocessingOptions.PlainSubtitle;
        Settings = settings ?? new ProfileSettings();
    }

    public ProfileId Id { get; }

    public string Name { get; }

    public string SourceLanguage { get; }

    public string TargetLanguage { get; }

    public int CaptureFramesPerSecond { get; }

    public IReadOnlyList<CaptureRegion> Regions { get; }

    public bool HistoryEnabled { get; }

    public PreprocessingOptions Preprocessing { get; }

    public ProfileSettings Settings { get; }
}

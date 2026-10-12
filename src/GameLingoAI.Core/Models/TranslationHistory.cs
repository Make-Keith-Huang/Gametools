namespace GameLingoAI.Core.Models;

public sealed record HistoryPolicy
{
    public HistoryPolicy(bool enabled = false, int retentionDays = 30, int pruneBatchSize = 128)
    {
        if (retentionDays is < 1 or > 3650 || pruneBatchSize is < 1 or > 512)
        { throw new ArgumentOutOfRangeException(nameof(retentionDays)); }
        Enabled = enabled;
        RetentionDays = retentionDays;
        PruneBatchSize = pruneBatchSize;
    }
    public bool Enabled { get; }
    public int RetentionDays { get; }
    public int PruneBatchSize { get; }
}

/// <summary>Optional local text history; never contains image bytes or provider credentials.</summary>
public sealed record TranslationHistoryEntry
{
    public TranslationHistoryEntry(SessionId sessionId, ProfileId? profileId, string sourceText,
        string translatedText, DateTimeOffset observedUtc, long latencyMilliseconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceText);
        ArgumentException.ThrowIfNullOrWhiteSpace(translatedText);
        if (sessionId.Value == Guid.Empty || profileId?.Value == Guid.Empty || sourceText.Length > 4000 ||
            translatedText.Length > 4000 || observedUtc.Offset != TimeSpan.Zero || latencyMilliseconds < 0)
        { throw new ArgumentException("History metadata or text is invalid."); }
        SessionId = sessionId;
        ProfileId = profileId;
        SourceText = sourceText;
        TranslatedText = translatedText;
        ObservedUtc = observedUtc;
        LatencyMilliseconds = latencyMilliseconds;
    }
    public SessionId SessionId { get; }
    public ProfileId? ProfileId { get; }
    public string SourceText { get; }
    public string TranslatedText { get; }
    public DateTimeOffset ObservedUtc { get; }
    public long LatencyMilliseconds { get; }
}

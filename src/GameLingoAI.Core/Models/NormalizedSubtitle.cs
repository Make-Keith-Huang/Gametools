namespace GameLingoAI.Core.Models;

public sealed record NormalizedSubtitle(
    string Text,
    IReadOnlyList<OcrTextSpan> OrderedSpans,
    NormalizedRegion Bounds,
    double AverageConfidence);

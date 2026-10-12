using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Preprocessing;

/// <summary>
/// Receives local debug images only when both diagnostic screenshot consent and the preprocessing
/// diagnostic flag are enabled. Implementations must warn about sensitive text and enforce retention.
/// </summary>
public interface IPreprocessingDiagnosticSink
{
    ValueTask WriteAsync(
        PipelineMetadata metadata,
        PreprocessedRegionImage image,
        CancellationToken cancellationToken);
}

public sealed class DisabledPreprocessingDiagnosticSink : IPreprocessingDiagnosticSink
{
    public ValueTask WriteAsync(
        PipelineMetadata metadata,
        PreprocessedRegionImage image,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(new InvalidOperationException(
            "Diagnostic image output is not configured. Enable an explicit retained/redacted diagnostic sink first."));
}

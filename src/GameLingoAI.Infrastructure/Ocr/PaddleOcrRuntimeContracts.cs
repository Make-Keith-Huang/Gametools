using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Ocr;

public sealed record PaddleRuntimeSpan(
    string Text,
    double Confidence,
    double X,
    double Y,
    double Width,
    double Height);

public interface IPaddleOcrRuntime : IDisposable
{
    IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image);

    /// <summary>Cooperative cancellation; synchronous native calls finish before ownership is released.</summary>
    IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Recognize(image);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}

public interface IPaddleOcrRuntimeFactory
{
    IPaddleOcrRuntime Create(OcrModelPackage package, string installationPath);
}

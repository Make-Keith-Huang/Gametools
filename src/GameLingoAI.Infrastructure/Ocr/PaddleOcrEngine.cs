using System.Text;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Ocr;

public sealed class PaddleOcrEngine : IOcrEngine
{
    private readonly OcrModelProvisioner _provisioner;
    private readonly IPaddleOcrRuntimeFactory _runtimeFactory;
    private readonly OcrModelPackage _package;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private IPaddleOcrRuntime? _runtime;
    private bool _disposed;

    public PaddleOcrEngine(
        OcrModelProvisioner provisioner,
        IPaddleOcrRuntimeFactory runtimeFactory,
        OcrModelPackage package)
    {
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
        _package = package ?? throw new ArgumentNullException(nameof(package));
        Capabilities = new OcrEngineCapabilities(
            package.Languages,
            new HashSet<OcrExecutionDevice> { OcrExecutionDevice.Cpu },
            SerializesInference: true,
            RequiresExplicitModelProvisioning: true);
    }

    public OcrEngineCapabilities Capabilities { get; }

    public async Task<OperationResult<OcrResult>> RecognizeAsync(
        PreprocessedFrame frame,
        OcrRecognitionOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(options);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_package.Languages.Contains(options.Language))
        {
            return Failure(
                frame.Metadata,
                FailureCode.UnsupportedLanguage,
                $"The selected OCR model does not support {options.Language}.",
                isTransient: false);
        }

        var status = await _provisioner.GetStatusAsync(_package, cancellationToken).ConfigureAwait(false);
        if (status.State != OcrModelState.Ready)
        {
            var code = status.State is OcrModelState.Missing or OcrModelState.Partial
                ? FailureCode.ModelMissing
                : FailureCode.ModelCorrupt;
            return Failure(frame.Metadata, code, status.Message, isTransient: false);
        }

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _runtime ??= _runtimeFactory.Create(_package, status.InstallationPath!);
                var spans = await Task.Run(() => RecognizeAll(frame, options, cancellationToken), CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return OperationResult.Success(new OcrResult(frame.Metadata, spans));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or DllNotFoundException or BadImageFormatException)
            {
                DisposeRuntime();
                return Failure(
                    frame.Metadata,
                    FailureCode.ModelCorrupt,
                    "The local OCR runtime or model could not be loaded. Repair the model installation and retry.",
                    isTransient: false);
            }
            catch (Exception)
            {
                DisposeRuntime();
                return Failure(
                    frame.Metadata,
                    FailureCode.Unexpected,
                    "Local OCR inference failed. The engine was reset and can be retried.",
                    isTransient: true);
            }
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _inferenceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeRuntime();
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private OcrTextSpan[] RecognizeAll(PreprocessedFrame frame, OcrRecognitionOptions options, CancellationToken cancellationToken)
    {
        var output = new List<(int RegionIndex, OcrTextSpan Span)>();
        for (var regionIndex = 0; regionIndex < frame.Regions.Count; regionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = frame.Regions[regionIndex];
            foreach (var span in _runtime!.Recognize(image, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(span.Text) || !double.IsFinite(span.Confidence) ||
                    span.Confidence < options.MinimumConfidence || span.Confidence > 1)
                {
                    continue;
                }

                var bounds = MapBounds(image, span);
                if (bounds is null)
                {
                    continue;
                }

                var normalizedText = span.Text.Normalize(NormalizationForm.FormKC).Trim();
                if (normalizedText.Length > 0)
                {
                    output.Add((regionIndex, new OcrTextSpan(normalizedText, span.Confidence, bounds.Value, image.RegionId)));
                }
            }
        }

        return output
            .OrderBy(item => item.RegionIndex)
            .ThenBy(item => item.Span.Bounds.Y)
            .ThenBy(item => item.Span.Bounds.X)
            .Select(item => item.Span)
            .ToArray();
    }

    private static NormalizedRegion? MapBounds(PreprocessedRegionImage image, PaddleRuntimeSpan span)
    {
        if (!double.IsFinite(span.X) || !double.IsFinite(span.Y) ||
            !double.IsFinite(span.Width) || !double.IsFinite(span.Height) ||
            span.Width <= 0 || span.Height <= 0)
        {
            return null;
        }

        var left = Math.Clamp(span.X / image.Width, 0, 1);
        var top = Math.Clamp(span.Y / image.Height, 0, 1);
        var right = Math.Clamp((span.X + span.Width) / image.Width, 0, 1);
        var bottom = Math.Clamp((span.Y + span.Height) / image.Height, 0, 1);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        var source = image.SourceBounds;
        return new NormalizedRegion(
            source.X + (left * source.Width),
            source.Y + (top * source.Height),
            (right - left) * source.Width,
            (bottom - top) * source.Height);
    }

    private static OperationResult<OcrResult> Failure(
        PipelineMetadata metadata,
        FailureCode code,
        string message,
        bool isTransient) =>
        OperationResult.Failed<OcrResult>(new PipelineFailure(code, message, metadata.CorrelationId, isTransient));

    private void DisposeRuntime()
    {
        var runtime = Interlocked.Exchange(ref _runtime, null);
        runtime?.Dispose();
    }
}

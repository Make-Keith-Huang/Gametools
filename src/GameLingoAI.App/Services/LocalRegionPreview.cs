using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Capture;

namespace GameLingoAI.App.Services;

public sealed record LocalPreviewFrame(BitmapSource Image, int Width, int Height);

public interface ILocalRegionPreview : IAsyncDisposable
{
    event EventHandler<LocalPreviewFrame>? FrameReady;

    event EventHandler<Exception>? PreviewFailed;

    bool IsRunning { get; }

    Task StartAsync(
        ConfirmedCaptureWindow target,
        SessionId sessionId,
        CancellationToken cancellationToken);

    void RequestStop();

    Task WhenStopped { get; }
}

public sealed class WgcLocalRegionPreview : ILocalRegionPreview, IDisposable
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(250);
    private readonly IWindowsGraphicsCaptureSourceFactory _captureFactory;
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task _pump = Task.CompletedTask;
    private int _disposed;

    public WgcLocalRegionPreview(IWindowsGraphicsCaptureSourceFactory captureFactory) =>
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));

    public event EventHandler<LocalPreviewFrame>? FrameReady;

    public event EventHandler<Exception>? PreviewFailed;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return !_pump.IsCompleted;
            }
        }
    }

    public Task WhenStopped
    {
        get
        {
            lock (_sync)
            {
                return _pump;
            }
        }
    }

    public async Task StartAsync(
        ConfirmedCaptureWindow target,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        RequestStop();
        await WhenStopped.ConfigureAwait(false);

        var result = _captureFactory.Create(target, sessionId);
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(result.Failure!.Message);
        }

        var cancellation = new CancellationTokenSource();
        var source = result.Value!;
        lock (_sync)
        {
            _cancellation = cancellation;
            _pump = PumpAsync(source, cancellation);
        }
    }

    public void RequestStop()
    {
        lock (_sync)
        {
            _cancellation?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        RequestStop();
        await WhenStopped.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            RequestStop();
        }
    }

    private async Task PumpAsync(GameLingoAI.Core.Abstractions.ICaptureSource source, CancellationTokenSource cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastPublished = -PreviewInterval;
        try
        {
            await foreach (var frame in source.CaptureAsync(cancellation.Token).ConfigureAwait(false))
            {
                using (frame)
                {
                    if (stopwatch.Elapsed - lastPublished < PreviewInterval)
                    {
                        continue;
                    }

                    var pixels = frame.BgraPixels.ToArray();
                    var image = BitmapSource.Create(
                        frame.Width,
                        frame.Height,
                        96,
                        96,
                        PixelFormats.Bgra32,
                        null,
                        pixels,
                        checked(frame.Width * 4));
                    image.Freeze();
                    lastPublished = stopwatch.Elapsed;
                    FrameReady?.Invoke(this, new LocalPreviewFrame(image, frame.Width, frame.Height));
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PreviewFailed?.Invoke(this, exception);
        }
        finally
        {
            await source.DisposeAsync().ConfigureAwait(false);
            cancellation.Dispose();
            lock (_sync)
            {
                if (ReferenceEquals(_cancellation, cancellation))
                {
                    _cancellation = null;
                }
            }
        }
    }
}

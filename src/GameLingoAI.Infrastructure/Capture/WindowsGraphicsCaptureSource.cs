using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GameLingoAI.Infrastructure.Capture;

public sealed class WindowsGraphicsCaptureSource : ICaptureSource
{
    private readonly ConfirmedCaptureWindow _target;
    private readonly SessionId _sessionId;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private long _sequence = -1;
    private long _lastTimestampTicks;
    private int _disposed;

    internal WindowsGraphicsCaptureSource(ConfirmedCaptureWindow target, SessionId sessionId)
    {
        _target = target;
        _sessionId = sessionId;
    }

    public async IAsyncEnumerable<CaptureFrame> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            var recoveryAttempt = 0;
            while (true)
            {
                CaptureSessionResources? resources = null;
                var recoverDevice = false;
                try
                {
                    resources = await StartWhenReadyAsync(_target.Window, linkedCancellation.Token)
                        .ConfigureAwait(false);
                    await using var frames = resources.Frames
                        .ReadAllAsync(linkedCancellation.Token)
                        .GetAsyncEnumerator(linkedCancellation.Token);
                    while (true)
                    {
                        CaptureFrame? output = null;
                        try
                        {
                            if (!await frames.MoveNextAsync().ConfigureAwait(false))
                            {
                                break;
                            }

                            using var nativeFrame = frames.Current;
                            var contentSize = nativeFrame.ContentSize;
                            if (contentSize.Width <= 0 || contentSize.Height <= 0)
                            {
                                continue;
                            }

                            var pixels = await CopyPixelsAsync(nativeFrame, linkedCancellation.Token)
                                .ConfigureAwait(false);
                            var metadata = new PipelineMetadata(
                                _sessionId,
                                new SequenceNumber(Interlocked.Increment(ref _sequence)),
                                NextTimestamp(),
                                CorrelationId.New());
                            output = new CaptureFrame(
                                metadata,
                                pixels.Width,
                                pixels.Height,
                                pixels.Bytes);
                        }
                        catch (Exception exception) when (
                            GraphicsCaptureRecoveryPolicy.IsRecoverableDeviceLoss(exception) &&
                            recoveryAttempt < GraphicsCaptureRecoveryPolicy.MaximumAttempts)
                        {
                            recoverDevice = true;
                        }

                        if (recoverDevice)
                        {
                            break;
                        }

                        if (output is not null)
                        {
                            recoveryAttempt = 0;
                            yield return output;
                        }
                    }
                }
                finally
                {
                    resources?.Dispose();
                }

                if (!recoverDevice)
                {
                    yield break;
                }

                recoveryAttempt++;
                await Task.Delay(TimeSpan.FromMilliseconds(100), linkedCancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        await _runGate.WaitAsync().ConfigureAwait(false);
        _runGate.Release();
        _lifetime.Dispose();
        _runGate.Dispose();
    }

    private static async Task<CaptureSessionResources> StartWhenReadyAsync(
        CaptureWindow target,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return CaptureSessionResources.Start(target);
            }
            catch (Exception exception) when (
                GraphicsCaptureRecoveryPolicy.IsTransientStartupState(exception) &&
                attempt < GraphicsCaptureRecoveryPolicy.MaximumStartupStateAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<PixelCopy> CopyPixelsAsync(
        Direct3D11CaptureFrame frame,
        CancellationToken cancellationToken)
    {
        using var bitmap = await SoftwareBitmap
            .CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        var length = checked(bitmap.PixelWidth * bitmap.PixelHeight * 4);
        var pixels = new byte[length];
        var buffer = new Windows.Storage.Streams.Buffer((uint)length);
        bitmap.CopyToBuffer(buffer);
        using var reader = DataReader.FromBuffer(buffer);
        reader.ReadBytes(pixels);
        return new PixelCopy(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private sealed record PixelCopy(int Width, int Height, byte[] Bytes);

    private DateTimeOffset NextTimestamp()
    {
        while (true)
        {
            var previous = Volatile.Read(ref _lastTimestampTicks);
            var now = DateTimeOffset.UtcNow.UtcTicks;
            var next = Math.Max(now, previous + 1);
            if (Interlocked.CompareExchange(ref _lastTimestampTicks, next, previous) == previous)
            {
                return new DateTimeOffset(next, TimeSpan.Zero);
            }
        }
    }

    private sealed class CaptureSessionResources : IDisposable
    {
        private readonly CaptureWindow _target;
        private readonly IDirect3DDevice _device;
        // CsWinRT may share a projected capture item through its COM identity cache. Disposing the
        // projection's NativeObject manually can invalidate another active/recreated session.
        private readonly GraphicsCaptureItem _item;
        private readonly Direct3D11CaptureFramePool _pool;
        private readonly GraphicsCaptureSession _session;
        private readonly Timer _targetMonitor;
        private SizeInt32 _size;
        private int _disposed;

        private CaptureSessionResources(
            CaptureWindow target,
            IDirect3DDevice device,
            GraphicsCaptureItem item,
            Direct3D11CaptureFramePool pool,
            GraphicsCaptureSession session,
            SizeInt32 size)
        {
            _target = target;
            _device = device;
            _item = item;
            _pool = pool;
            _session = session;
            _size = size;
            Frames = new LatestFrameBuffer<Direct3D11CaptureFrame>();
            _pool.FrameArrived += OnFrameArrived;
            _item.Closed += OnItemClosed;
            _targetMonitor = new Timer(OnTargetMonitor, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        }

        public LatestFrameBuffer<Direct3D11CaptureFrame> Frames { get; }

        public static CaptureSessionResources Start(CaptureWindow target)
        {
            RevalidateTarget(target);
            var item = WinRtCaptureInterop.CreateItemForWindow(
                new IntPtr(unchecked((long)target.Identity.Id.Value)));
            var size = item.Size;
            if (size.Width <= 0 || size.Height <= 0)
            {
                throw new InvalidOperationException("Restore the selected window before starting capture.");
            }

            var device = WinRtCaptureInterop.CreateDirect3DDevice();
            Direct3D11CaptureFramePool? pool = null;
            GraphicsCaptureSession? session = null;
            try
            {
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    device,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized,
                    2,
                    size);
                session = pool.CreateCaptureSession(item);
                var resources = new CaptureSessionResources(target, device, item, pool, session, size);
                session.StartCapture();
                return resources;
            }
            catch
            {
                session?.Dispose();
                pool?.Dispose();
                device.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _pool.FrameArrived -= OnFrameArrived;
            _item.Closed -= OnItemClosed;
            _targetMonitor.Dispose();
            Frames.Dispose();
            _session.Dispose();
            _pool.Dispose();
            _device.Dispose();
        }

        private static void RevalidateTarget(CaptureWindow target)
        {
            var handle = new IntPtr(unchecked((long)target.Identity.Id.Value));
            _ = GetWindowThreadProcessId(handle, out var processId);
            if (processId != target.Identity.ProcessId || IsIconic(handle))
            {
                throw new InvalidOperationException(
                    "The confirmed capture target is unavailable. Restore it or select the window again.");
            }

            try
            {
                using var process = Process.GetProcessById(target.Identity.ProcessId);
                var startMarker = process.StartTime.ToUniversalTime().Ticks;
                if (target.Identity.ProcessStartMarker != 0 && startMarker != target.Identity.ProcessStartMarker)
                {
                    throw new InvalidOperationException(
                        "The confirmed window identity changed. Refresh and confirm the target again.");
                }
            }
            catch (ArgumentException exception)
            {
                throw new InvalidOperationException(
                    "The confirmed capture target has closed. Refresh and select another window.",
                    exception);
            }
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object arguments)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                var size = frame.ContentSize;
                var handle = new IntPtr(unchecked((long)_target.Identity.Id.Value));
                if (size.Width <= 0 || size.Height <= 0 || IsIconic(handle))
                {
                    frame.Dispose();
                    return;
                }

                if (size.Width != _size.Width || size.Height != _size.Height)
                {
                    frame.Dispose();
                    _size = size;
                    sender.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    return;
                }

                if (!Frames.TryWrite(frame))
                {
                    frame.Dispose();
                }
            }
            catch (Exception exception) when (exception is COMException or ObjectDisposedException)
            {
                Frames.Complete(new InvalidOperationException(
                    "Window capture stopped because the graphics device or target became unavailable.",
                    exception));
            }
        }

        private void OnItemClosed(GraphicsCaptureItem sender, object arguments) =>
            Frames.Complete(new InvalidOperationException(
                "The captured window closed. Refresh and select another target."));

        private void OnTargetMonitor(object? state)
        {
            var handle = new IntPtr(unchecked((long)_target.Identity.Id.Value));
            if (!IsWindow(handle))
            {
                Frames.Complete(new InvalidOperationException(
                    "The captured window closed. Refresh and select another target."));
            }
        }

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr window);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Capture;

internal static class CaptureTests
{
    private static readonly int[] NewestValues = [3, 4];

    public static async Task RunAsync()
    {
        await TestLatestFrameBufferAsync().ConfigureAwait(false);
        await TestLatestFrameBufferCancellationAndErrorAsync().ConfigureAwait(false);
        TestDeviceLossRecoveryPolicy();
        TestConfirmationGateAndMinimizedFailure();
        await TestWindowsCaptureLifecycleAsync().ConfigureAwait(false);
    }

    private static async Task TestLatestFrameBufferAsync()
    {
        using var buffer = new LatestFrameBuffer<DisposableFrame>();
        var frames = Enumerable.Range(1, 4).Select(value => new DisposableFrame(value)).ToArray();
        foreach (var frame in frames)
        {
            Assert(buffer.TryWrite(frame), "The latest-frame buffer should accept a frame.");
        }

        Assert(frames[0].IsDisposed && frames[1].IsDisposed,
            "A full capacity-two buffer must dispose its oldest stale frames.");
        buffer.Complete();
        var received = new List<int>();
        await foreach (var frame in buffer.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            received.Add(frame.Value);
            frame.Dispose();
        }

        Assert(received.SequenceEqual(NewestValues), "Only the two newest frames should survive backpressure.");
        Assert(frames.All(frame => frame.IsDisposed), "Every fake frame must be disposed exactly once.");
    }

    private static async Task TestLatestFrameBufferCancellationAndErrorAsync()
    {
        using (var cancelledBuffer = new LatestFrameBuffer<DisposableFrame>())
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(async () =>
            {
                await using var enumerator = cancelledBuffer
                    .ReadAllAsync(cancellation.Token)
                    .GetAsyncEnumerator(cancellation.Token);
                _ = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }

        using var failedBuffer = new LatestFrameBuffer<DisposableFrame>();
        failedBuffer.Complete(new InvalidOperationException("synthetic device failure"));
        await AssertThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var enumerator = failedBuffer
                .ReadAllAsync(CancellationToken.None)
                .GetAsyncEnumerator(CancellationToken.None);
            _ = await enumerator.MoveNextAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private static void TestDeviceLossRecoveryPolicy()
    {
        Assert(GraphicsCaptureRecoveryPolicy.IsRecoverableDeviceLossHResult(unchecked((int)0x887A0005)),
            "DXGI device removal should be recoverable.");
        Assert(GraphicsCaptureRecoveryPolicy.IsRecoverableDeviceLossHResult(unchecked((int)0x887A0006)),
            "DXGI device hangs should be recoverable.");
        Assert(GraphicsCaptureRecoveryPolicy.IsRecoverableDeviceLossHResult(unchecked((int)0x887A0007)),
            "DXGI device resets should be recoverable.");
        Assert(!GraphicsCaptureRecoveryPolicy.IsRecoverableDeviceLossHResult(unchecked((int)0x80070005)),
            "Unrelated HRESULT failures must not trigger device-loss retries.");
        Assert(!GraphicsCaptureRecoveryPolicy.IsRecoverableDeviceLoss(new InvalidOperationException("target closed")),
            "Target-close failures must not trigger device recreation.");
        Assert(GraphicsCaptureRecoveryPolicy.IsTransientStartupStateHResult(unchecked((int)0x8007139F)),
            "Rapid-restart resource-state failures should receive a bounded startup retry.");
        Assert(!GraphicsCaptureRecoveryPolicy.IsTransientStartupStateHResult(unchecked((int)0x80070005)),
            "Unrelated startup failures must not be retried.");
    }

    private static void TestConfirmationGateAndMinimizedFailure()
    {
        var window = CreateWindow(999, isMinimized: true);
        var tracker = new WindowSelectionTracker();
        tracker.Select(window);
        Assert(tracker.ConfirmedSelection is null, "Selecting without confirmation must not create a capture target.");
        tracker.Confirm(new[] { window });
        var confirmed = tracker.ConfirmedSelection ?? throw new InvalidOperationException("Confirmation gate failed.");
        var result = new WindowsGraphicsCaptureSourceFactory().Create(confirmed, SessionId.New());
        Assert(!result.IsSuccess && result.Failure?.Code == FailureCode.TargetUnavailable,
            "A minimized confirmed target should return a recoverable failure before capture starts.");
    }

    private static async Task TestWindowsCaptureLifecycleAsync()
    {
        var executable = FindAppExecutable();
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--synthetic-test" },
            UseShellExecute = false,
            CreateNoWindow = false
        }) ?? throw new InvalidOperationException("Capture test window process did not start.");

        try
        {
            var discovery = new WindowsWindowDiscovery();
            var target = await WaitForWindowAsync(discovery, process.Id).ConfigureAwait(false);
            var tracker = new WindowSelectionTracker();
            tracker.Select(target);
            tracker.Confirm(new[] { target });
            var confirmed = tracker.ConfirmedSelection ?? throw new InvalidOperationException("Target was not confirmed.");
            var factory = new WindowsGraphicsCaptureSourceFactory();
            Assert(factory.DetectCapability().IsSupported, factory.DetectCapability().Message);
            var sourceResult = factory.Create(confirmed, SessionId.New());
            Assert(sourceResult.IsSuccess, sourceResult.Failure?.Message ?? "Capture source creation failed.");
            await using var source = sourceResult.Value!;

            await VerifyOrderedFramesAsync(source).ConfigureAwait(false);
            await VerifyThrottledPreviewLifecycleAsync(factory, confirmed).ConfigureAwait(false);

            await VerifyResizeAsync(source, confirmed.Window).ConfigureAwait(false);

            var privateBytesBefore = process.PrivateMemorySize64;
            for (var cycle = 0; cycle < 50; cycle++)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var frame = await ReadOneFrameAsync(source, timeout.Token).ConfigureAwait(false);
            }

            process.Refresh();
            var privateBytesAfter = process.PrivateMemorySize64;
            var growth = privateBytesAfter - privateBytesBefore;
            Assert(growth < 128L * 1024 * 1024,
                $"50 capture start/stop cycles grew target private memory by {growth} bytes.");
            Console.WriteLine($"Capture soak: 50 cycles, target private-memory delta {growth} bytes.");

            await VerifyTargetCloseAsync(source, process).ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task VerifyThrottledPreviewLifecycleAsync(
        IWindowsGraphicsCaptureSourceFactory factory,
        ConfirmedCaptureWindow target)
    {
        await using var preview = new WgcLocalRegionPreview(factory);
        var received = new TaskCompletionSource<LocalPreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        preview.FrameReady += (_, frame) => received.TrySetResult(frame);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await preview.StartAsync(target, SessionId.New(), timeout.Token).ConfigureAwait(false);
        var previewFrame = await received.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        Assert(previewFrame.Image.IsFrozen, "Published local preview images must be immutable across UI threads.");
        Assert(previewFrame.Width > 0 && previewFrame.Height > 0, "Local preview dimensions must be positive.");
        preview.RequestStop();
        await preview.WhenStopped.WaitAsync(timeout.Token).ConfigureAwait(false);
        Assert(!preview.IsRunning, "Stopping the local preview must release its capture session promptly.");
    }

    private static async Task VerifyOrderedFramesAsync(ICaptureSource source)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = source.CaptureAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        SequenceNumber? previousSequence = null;
        DateTimeOffset? previousTimestamp = null;
        for (var index = 0; index < 3; index++)
        {
            Assert(await enumerator.MoveNextAsync().ConfigureAwait(false),
                "Capture ended before three ordered frames arrived.");
            using var frame = enumerator.Current;
            Assert(frame.Width > 0 && frame.Height > 0, "Captured frame dimensions must be positive.");
            Assert(frame.BgraPixels.Length == frame.Width * frame.Height * 4,
                "Captured frame must contain tightly packed BGRA pixels.");
            if (previousSequence is not null)
            {
                Assert(frame.Metadata.Sequence > previousSequence.Value,
                    "Capture sequence numbers must increase strictly.");
                Assert(frame.Metadata.CapturedAtUtc > previousTimestamp!.Value,
                    "Capture timestamps must increase strictly.");
            }

            previousSequence = frame.Metadata.Sequence;
            previousTimestamp = frame.Metadata.CapturedAtUtc;
        }
    }

    private static async Task VerifyResizeAsync(ICaptureSource source, CaptureWindow target)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = source.CaptureAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Capture ended before the resize test.");
        var firstWidth = enumerator.Current.Width;
        var firstHeight = enumerator.Current.Height;
        enumerator.Current.Dispose();

        var handle = new IntPtr(unchecked((long)target.Identity.Id.Value));
        Assert(SetWindowPos(
            handle,
            IntPtr.Zero,
            0,
            0,
            target.ClientBounds.Width + 160,
            target.ClientBounds.Height + 90,
            SetWindowPositionNoMove | SetWindowPositionNoZOrder | SetWindowPositionNoActivate),
            "Synthetic capture window resize failed.");

        while (await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            using var frame = enumerator.Current;
            if (frame.Width != firstWidth || frame.Height != firstHeight)
            {
                Assert(frame.BgraPixels.Length == frame.Width * frame.Height * 4,
                    "Resized frame BGRA storage must match its dimensions.");
                return;
            }
        }

        throw new InvalidOperationException("Capture ended before a resized frame arrived.");
    }

    private static async Task VerifyTargetCloseAsync(ICaptureSource source, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = source.CaptureAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert(await enumerator.MoveNextAsync().ConfigureAwait(false), "Capture ended before target-close verification.");
        enumerator.Current.Dispose();
        Assert(process.CloseMainWindow(), "Capture test window did not accept the close request.");
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        var reportedClosure = false;
        try
        {
            while (await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                enumerator.Current.Dispose();
            }
        }
        catch (InvalidOperationException)
        {
            reportedClosure = true;
        }

        Assert(reportedClosure, "Closing the target should end capture with an actionable failure.");
    }

    private static async Task<CaptureFrame> ReadOneFrameAsync(
        ICaptureSource source,
        CancellationToken cancellationToken)
    {
        await using var enumerator = source.CaptureAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException("Capture completed before producing a frame.");
        }

        return enumerator.Current;
    }

    private static async Task<CaptureWindow> WaitForWindowAsync(
        WindowsWindowDiscovery discovery,
        int processId)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            var result = await discovery
                .DiscoverAsync(new HashSet<WindowId>(), CancellationToken.None)
                .ConfigureAwait(false);
            Assert(result.IsSuccess, result.Failure?.Message ?? "Window discovery failed.");
            var match = result.Value!.FirstOrDefault(window => window.Identity.ProcessId == processId);
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Capture test window was not discovered.");
    }

    private static CaptureWindow CreateWindow(int processId, bool isMinimized) => new(
        new WindowIdentity(new WindowId(123), processId, 1),
        "Test window",
        "Test process",
        new PixelBounds(0, 0, 640, 480),
        96,
        isMinimized);

    private static string FindAppExecutable()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "artifacts",
                "bin",
                "GameLingoAI.App",
                "release",
                "GameLingoAI.App.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The built GameLingoAI.App executable was not found.");
    }

    private sealed class DisposableFrame(int value) : IDisposable
    {
        private int _disposeCount;

        public int Value { get; } = value;

        public bool IsDisposed => Volatile.Read(ref _disposeCount) > 0;

        public void Dispose()
        {
            var count = Interlocked.Increment(ref _disposeCount);
            Assert(count == 1, "A buffered frame must be disposed exactly once.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private const uint SetWindowPositionNoMove = 0x0002;
    private const uint SetWindowPositionNoZOrder = 0x0004;
    private const uint SetWindowPositionNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}

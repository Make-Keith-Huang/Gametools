using System.Diagnostics;
using System.Runtime.InteropServices;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Capture;

internal static class WindowDiscoveryTests
{
    public static async Task RunAsync()
    {
        TestSelectionIdentityAndConfirmation();
        await TestViewModelRefreshAndCancellationAsync().ConfigureAwait(false);
        await TestWindowsDiscoveryIntegrationAsync().ConfigureAwait(false);
        await TestSyntheticWindowLifecycleAsync().ConfigureAwait(false);
    }

    private static void TestSelectionIdentityAndConfirmation()
    {
        var original = CreateWindow(1, 101, 1001, "Original");
        var renamed = CreateWindow(1, 101, 1001, "Renamed");
        var reusedHandle = CreateWindow(1, 202, 2002, "Different process");
        var tracker = new WindowSelectionTracker();

        tracker.Select(original);
        Assert(tracker.State == WindowSelectionValidationState.ConfirmationRequired, "Selection must require confirmation.");
        tracker.Confirm(new[] { original });
        Assert(tracker.IsConfirmed, "Explicit confirmation should make the selection ready.");
        Assert(tracker.Refresh(new[] { renamed }) == renamed, "A title change should preserve identity and selection.");
        Assert(tracker.IsConfirmed, "Confirmation should survive refresh for the same identity.");
        Assert(tracker.Refresh(new[] { reusedHandle }) is null, "A reused handle with different process identity must be rejected.");
        Assert(tracker.State == WindowSelectionValidationState.TargetUnavailable, "Lost identity should be recoverable.");
    }

    private static async Task TestViewModelRefreshAndCancellationAsync()
    {
        var first = CreateWindow(2, 303, 3003, "First");
        var renamed = new CaptureWindow(
            first.Identity,
            "First renamed",
            first.ProcessDisplayHint,
            first.ClientBounds,
            first.Dpi,
            true);
        var discovery = new SequenceWindowDiscovery(
            new[] { first },
            new[] { renamed },
            Array.Empty<CaptureWindow>());
        var reporter = new TestErrorReporter();
        using var viewModel = new WindowSelectionViewModel(discovery, reporter);

        await viewModel.RefreshCommand.ExecuteAsync().ConfigureAwait(false);
        Assert(viewModel.Windows.Count == 1, "Refresh should publish discovered windows.");
        viewModel.SelectedWindow = viewModel.Windows[0];
        viewModel.ConfirmCommand.Execute(true);
        Assert(viewModel.ValidationState == WindowSelectionValidationState.Ready, "Confirmation should be observable.");

        await viewModel.RefreshCommand.ExecuteAsync().ConfigureAwait(false);
        Assert(viewModel.SelectedWindow?.Title == "First renamed", "Selection should update metadata for the same identity.");
        Assert(viewModel.SelectedWindow?.IsMinimized == true, "Minimized state should update on refresh.");

        await viewModel.RefreshCommand.ExecuteAsync().ConfigureAwait(false);
        Assert(viewModel.SelectedWindow is null, "Closed windows should clear selection.");
        Assert(viewModel.ValidationState == WindowSelectionValidationState.TargetUnavailable, "Closed windows need recovery state.");

        var cancelledDiscovery = new CancellingWindowDiscovery();
        using var cancelledViewModel = new WindowSelectionViewModel(cancelledDiscovery, reporter);
        var execution = cancelledViewModel.RefreshCommand.ExecuteAsync();
        cancelledViewModel.RefreshCommand.Cancel();
        await execution.ConfigureAwait(false);
        Assert(reporter.Messages.Count == 0, "Expected refresh cancellation must not report an error.");
    }

    private static async Task TestWindowsDiscoveryIntegrationAsync()
    {
        var discovery = new WindowsWindowDiscovery();
        var result = await discovery
            .DiscoverAsync(new HashSet<WindowId>(), CancellationToken.None)
            .ConfigureAwait(false);
        Assert(result.IsSuccess, result.Failure?.Message ?? "Native discovery failed.");
        var windows = result.Value ?? throw new InvalidOperationException("Native discovery returned no list.");
        Assert(windows.All(window => window.Identity.ProcessId != Environment.ProcessId),
            "Discovery must exclude windows owned by this application process.");
        Assert(windows.All(window => window.ClientBounds.Width > 0 && window.ClientBounds.Height > 0),
            "Every discovered window needs a non-empty client area.");

        using var source = new CancellationTokenSource();
        source.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() =>
            discovery.DiscoverAsync(new HashSet<WindowId>(), source.Token)).ConfigureAwait(false);
    }

    private static async Task TestSyntheticWindowLifecycleAsync()
    {
        var executable = FindAppExecutable();
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--synthetic-test" },
            UseShellExecute = false,
            CreateNoWindow = false
        }) ?? throw new InvalidOperationException("Synthetic test window process did not start.");

        try
        {
            var discovery = new WindowsWindowDiscovery();
            var opened = await WaitForWindowAsync(discovery, process.Id, "open", window => !window.IsMinimized)
                .ConfigureAwait(false);
            var originalIdentity = opened.Identity;

            var handle = new IntPtr(unchecked((long)opened.Identity.Id.Value));
            Assert(SetWindowText(handle, "GameLingoAI Synthetic Renamed"), "Synthetic window rename failed.");
            var renamed = await WaitForWindowAsync(
                discovery,
                process.Id,
                "rename",
                window => window.Title == "GameLingoAI Synthetic Renamed").ConfigureAwait(false);
            Assert(renamed.Identity == originalIdentity, "Rename must preserve native window identity.");

            _ = ShowWindow(handle, ShowWindowMinimize);
            await WaitForNativeStateAsync(handle, expectedMinimized: true).ConfigureAwait(false);
            var minimizedResult = await discovery
                .DiscoverAsync(new HashSet<WindowId>(), CancellationToken.None)
                .ConfigureAwait(false);
            Assert(minimizedResult.IsSuccess, minimizedResult.Failure?.Message ?? "Minimized discovery failed.");
            var minimized = minimizedResult.Value!.FirstOrDefault(window => window.Identity.ProcessId == process.Id);
            Assert(minimized is null || minimized.IsMinimized,
                "A minimized window must either be reported as minimized or filtered when DWM cloaks it.");

            _ = ShowWindow(handle, ShowWindowRestore);
            var restored = await WaitForWindowAsync(discovery, process.Id, "restore", window => !window.IsMinimized)
                .ConfigureAwait(false);
            Assert(restored.Identity == originalIdentity, "Restored window must retain its native identity.");
            _ = process.CloseMainWindow();
            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(closeTimeout.Token).ConfigureAwait(false);
            var afterClose = await discovery
                .DiscoverAsync(new HashSet<WindowId>(), CancellationToken.None)
                .ConfigureAwait(false);
            Assert(afterClose.IsSuccess, afterClose.Failure?.Message ?? "Post-close discovery failed.");
            Assert(afterClose.Value!.All(window => window.Identity.ProcessId != process.Id),
                "Closed synthetic window must disappear from discovery.");
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

    private static async Task<CaptureWindow> WaitForWindowAsync(
        WindowsWindowDiscovery discovery,
        int processId,
        string stage,
        Func<CaptureWindow, bool> predicate)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        IReadOnlyList<CaptureWindow> lastWindows = Array.Empty<CaptureWindow>();
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            var result = await discovery
                .DiscoverAsync(new HashSet<WindowId>(), CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(result.Failure!.Message);
            }

            lastWindows = result.Value!;
            var match = lastWindows.FirstOrDefault(window =>
                window.Identity.ProcessId == processId && predicate(window));
            if (match is not null)
            {
                return match;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        var processWindows = string.Join(", ", lastWindows
            .Where(window => window.Identity.ProcessId == processId)
            .Select(window => $"'{window.Title}' minimized={window.IsMinimized}"));
        throw new InvalidOperationException(
            $"Synthetic window {stage} was not observed for process {processId}. Last matches: {processWindows}");
    }

    private static async Task WaitForNativeStateAsync(IntPtr handle, bool expectedMinimized)
    {
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (IsIconic(handle) == expectedMinimized)
            {
                return;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Synthetic window did not reach minimized={expectedMinimized}.");
    }

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

        throw new FileNotFoundException("Built GameLingoAI application executable was not found for integration testing.");
    }

    private static CaptureWindow CreateWindow(
        ulong handle,
        int processId,
        long processStart,
        string title) =>
        new(
            new WindowIdentity(new WindowId(handle), processId, processStart),
            title,
            $"Process {processId}",
            new PixelBounds(0, 0, 800, 600),
            96,
            false);

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

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class SequenceWindowDiscovery : IWindowDiscovery
    {
        private readonly Queue<IReadOnlyList<CaptureWindow>> _responses;

        public SequenceWindowDiscovery(params IReadOnlyList<CaptureWindow>[] responses)
        {
            _responses = new Queue<IReadOnlyList<CaptureWindow>>(responses);
        }

        public Task<OperationResult<IReadOnlyList<CaptureWindow>>> DiscoverAsync(
            IReadOnlySet<WindowId> excludedWindows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult.Success(_responses.Dequeue()));
        }
    }

    private sealed class CancellingWindowDiscovery : IWindowDiscovery
    {
        public async Task<OperationResult<IReadOnlyList<CaptureWindow>>> DiscoverAsync(
            IReadOnlySet<WindowId> excludedWindows,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return OperationResult.Success<IReadOnlyList<CaptureWindow>>(Array.Empty<CaptureWindow>());
        }
    }

    private sealed class TestErrorReporter : IErrorReporter
    {
        public event EventHandler<string>? ErrorReported;

        public List<string> Messages { get; } = new();

        public void Report(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            Messages.Add("A safe error occurred.");
            ErrorReported?.Invoke(this, Messages[^1]);
        }
    }

    private const int ShowWindowMinimize = 7;
    private const int ShowWindowRestore = 9;

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr window, string text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GameLingoAI.App.Overlay;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

internal static class OverlayWindowTests
{
    public static async Task RunAsync()
    {
        // WPF Application shutdown is process-global. Earlier shell tests must not
        // leave this native harness with an already shut-down application lifetime.
        if (Environment.GetEnvironmentVariable("GAMELINGOAI_OVERLAY_CHILD") == "1")
        {
            await RunInProcessAsync();
            return;
        }

        // VSTest hosts this assembly in testhost.exe. Launch our own apphost, never recursively start testhost.
        var start = new ProcessStartInfo(System.IO.Path.ChangeExtension(typeof(OverlayWindowTests).Assembly.Location, ".exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--overlay-harness");
        start.Environment["GAMELINGOAI_OVERLAY_CHILD"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start isolated overlay harness.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(16));
            Console.Write(await output);
            var errorText = await error;
            Console.Error.Write(errorText);
            Assert(process.ExitCode == 0, $"Isolated overlay harness failed with exit code {process.ExitCode}. {errorText}");
        }
        finally
        {
            if (!process.HasExited)
            { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private static async Task RunInProcessAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var kickoff = new DispatcherTimer(TimeSpan.FromMilliseconds(1), DispatcherPriority.Normal,
                async (sender, _) =>
                {
                    ((DispatcherTimer)sender!).Stop();
                    try
                    { await RunOnDispatcherAsync(); completion.TrySetResult(); }
                    catch (Exception error) { completion.TrySetException(error); }
                    finally { dispatcher.InvokeShutdown(); }
                }, dispatcher);
            Dispatcher.Run();
            kickoff.Stop();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var manual = Environment.GetEnvironmentVariable("GAMELINGOAI_OVERLAY_MANUAL") == "1" ||
            Environment.GetEnvironmentVariable("GAMELINGOAI_SESSION_MANUAL") == "1";
        await completion.Task.WaitAsync(manual ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(60));
    }

    private static async Task RunOnDispatcherAsync()
    {
        var clicks = 0;
        var button = new Button { Content = "Click beneath the subtitle overlay — counter: 0", FontSize = 20 };
        button.Click += (_, _) => button.Content = $"Actual user clicks received beneath overlay: {++clicks}";
        var target = new Window
        {
            Title = "GameLingoAI overlay synthetic target",
            Width = 720,
            Height = 480,
            Left = 80,
            Top = 80,
            Content = button
        };
        target.Show();
        await Task.Delay(50);
        try
        {
            var handle = new WindowInteropHelper(target).Handle;
            using var process = Process.GetCurrentProcess();
            var identity = new WindowIdentity(new WindowId(unchecked((ulong)handle.ToInt64())), process.Id,
                process.StartTime.ToUniversalTime().Ticks);
            var window = new CaptureWindow(identity, "Synthetic", "Tests", new PixelBounds(0, 0, 1, 1), 96, false);
            var selection = new WindowSelectionTracker();
            selection.Select(window);
            selection.Confirm([window]);
            var probe = new WindowsOverlayTargetProbe();
            var confirmed = selection.ConfirmedSelection!;
            var state = probe.Inspect(confirmed)!;
            Assert(state.IsVisible, "Synthetic target should be visible.");
            var invalidSelection = new WindowSelectionTracker();
            var wrongIdentity = new CaptureWindow(new WindowIdentity(identity.Id, identity.ProcessId, identity.ProcessStartMarker + 1),
                "Synthetic", "Tests", state.ClientBounds, state.Dpi, false);
            invalidSelection.Select(wrongIdentity);
            invalidSelection.Confirm([wrongIdentity]);
            Assert(probe.Inspect(invalidSelection.ConfirmedSelection!) is null, "Reused native identity must not be followed.");
            var options = new OverlayOptions { FontSize = 24 };
            using (var overlay = new SubtitleWindow())
            {
                var foreground = GetForegroundWindow();
                overlay.Present(state, "<b>Literal text</b>\n日本語 😀", options, false);
                await Task.Delay(50);
                Assert(GetForegroundWindow() == foreground, "Showing an overlay changed foreground focus.");
                var styles = GetWindowLong(overlay.NativeHandle, -20);
                Assert((styles & (0x20 | 0x80 | 0x08000000 | 0x80000)) == (0x20 | 0x80 | 0x08000000 | 0x80000),
                    "Running overlay needs transparent/tool/no-activate/layered styles.");
                Assert((styles & 0x40000) == 0 && !overlay.ShowInTaskbar, "Overlay must not appear in taskbar/Alt-Tab.");
                AssertBounds(overlay, state, options);
                var expected = OverlayLayout.Place(state.ClientBounds, GetDpiForWindow(overlay.NativeHandle), options);
                var hit = WindowFromPoint(new NativePoint { X = expected.Left + expected.Width / 2, Y = expected.Top + expected.Height / 2 });
                Assert(hit != overlay.NativeHandle, "Native point hit-test must skip the click-through overlay.");
                overlay.Present(state, "Updated subtitle", options, false);
                await Task.Delay(25);
                Assert(GetForegroundWindow() == foreground, "Updating an overlay changed foreground focus.");
                target.Left += 90;
                target.Top += 40;
                target.Width += 100;
                await Task.Delay(25);
                state = probe.Inspect(confirmed)!;
                overlay.Present(state, "Moved target", options, false);
                await Task.Delay(25);
                AssertBounds(overlay, state, options);
                overlay.Present(state, "Interactive layout preview", options, true);
                Assert((GetWindowLong(overlay.NativeHandle, -20) & 0x20) == 0, "Explicit edit mode must allow its own handles.");
                overlay.EndEdit();
                Assert((GetWindowLong(overlay.NativeHandle, -20) & (0x20 | 0x08000000)) == (0x20 | 0x08000000),
                    "Leaving edit mode must restore running styles immediately.");
                Assert(!overlay.IsVisible, "Leaving edit mode must hide until the pipeline resumes.");

                var renderer = new SubtitleRenderer();
                renderer.Update("<b>Plain</b>\n你好😀", options);
                renderer.Measure(new Size(600, 160));
                renderer.Arrange(new Rect(0, 0, 600, 160));
                var bitmap = new RenderTargetBitmap(600, 160, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(renderer);
                var pixels = new byte[600 * 160 * 4];
                bitmap.CopyPixels(pixels, 600 * 4, 0);
                Assert(pixels.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 0), "Subtitle renderer produced no visible pixels.");
                Assert(renderer.DisplayText.Contains("<b>", StringComparison.Ordinal), "Markup was interpreted instead of rendered literally.");

                // Use every actual monitor, not just mathematical DPI substitutions. Both the
                // synthetic target and overlay must agree with native DPI and client pixel size.
                foreach (var monitor in MonitorWorkAreas())
                {
                    Assert(SetWindowPos(handle, IntPtr.Zero, monitor.Left + 32, monitor.Top + 32,
                        Math.Min(720, monitor.Right - monitor.Left - 64), Math.Min(480, monitor.Bottom - monitor.Top - 64), 0x0004 | 0x0010),
                        "Could not move the owned synthetic target between monitors.");
                    await Task.Delay(150);
                    state = probe.Inspect(confirmed)!;
                    overlay.Present(state, "Cross-monitor DPI verification\n完整字幕 — Full subtitle", options, false);
                    await Task.Delay(150);
                    target.UpdateLayout();
                    overlay.UpdateLayout();
                    AssertDpiAndClient(target, button);
                    AssertDpiAndClient(overlay, (FrameworkElement)overlay.Content);
                    AssertBounds(overlay, state, options);
                    Console.WriteLine($"Overlay monitor probe: target native DPI {GetDpiForWindow(handle)}, WPF {VisualTreeHelper.GetDpi(target).DpiScaleX:F2}; overlay native DPI {GetDpiForWindow(overlay.NativeHandle)}, WPF {VisualTreeHelper.GetDpi(overlay).DpiScaleX:F2}.");
                }

                if (Environment.GetEnvironmentVariable("GAMELINGOAI_OVERLAY_MANUAL") == "1")
                {
                    overlay.Present(state, "MANUAL: click here; the target counter must increase.\nMove/resize/minimize the target; close it when done.", options, false);
                    Console.WriteLine("Manual overlay harness: click the overlay-covered target, inspect focus and scaling; close the target to finish. No input is simulated.");
                    var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    target.Closed += (_, _) => closed.TrySetResult();
                    var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Normal,
                        (_, _) =>
                        {
                            var current = probe.Inspect(confirmed);
                            if (current is null || !current.IsVisible)
                            { overlay.HideImmediately(); }
                            else
                            { overlay.Present(current, "MANUAL: click through to the button below", options, false); }
                        }, Dispatcher.CurrentDispatcher);
                    try
                    { await closed.Task; }
                    finally { timer.Stop(); }
                    Console.WriteLine($"Manual overlay harness received {clicks} real user clicks (zero does not prove click delivery).");
                    return;
                }
            }

            var display = new SessionDisplay();
            var reporter = new Reporter();
            using (var controller = new OverlayController(probe, display, reporter))
            {
                // Closing the preceding cross-monitor overlay can change activation. A running
                // subtitle is intentionally hidden unless this owned target is foreground.
                _ = target.Activate();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert(probe.Inspect(confirmed)?.IsForeground == true,
                    "Foreground precondition unavailable for the owned overlay target.");
                var session = SessionId.New();
                await display.ShowAsync(Result(session, 1), CancellationToken.None);
                controller.Bind(session, confirmed, options);
                await Task.Delay(25);
                var overlay = OwnedOverlay();
                Assert(overlay is not null && overlay.IsVisible, "Controller did not display the latest session value.");
                await display.ShowAsync(Result(SessionId.New(), 200), CancellationToken.None);
                controller.Refresh();
                Assert(overlay!.DisplayText == "Translation 1", "Foreign-session result replaced the current subtitle.");
                var watch = Stopwatch.StartNew();
                target.WindowState = WindowState.Minimized;
                while (overlay.IsVisible && watch.Elapsed < TimeSpan.FromMilliseconds(250))
                { await Task.Delay(5); }
                Assert(!overlay.IsVisible && watch.Elapsed < TimeSpan.FromMilliseconds(250), "Minimize did not hide within 250 ms.");
                target.WindowState = WindowState.Normal;
                await Task.Delay(120);
                Assert(overlay.IsVisible, "Restore did not restore the current subtitle.");
                controller.Unbind();
                Assert(!IsWindow(new WindowInteropHelper(overlay).Handle), "Unbind leaked the overlay HWND.");
                Assert(reporter.Errors == 0, "Overlay controller reported an unexpected error.");
            }

            var fakeProbe = new Probe { State = probe.Inspect(confirmed) };
            using (var controller = new OverlayController(fakeProbe, display, reporter))
            {
                var session = SessionId.New();
                await display.ShowAsync(Result(session, 1), CancellationToken.None);
                controller.Bind(session, confirmed, options);
                var overlay = OwnedOverlay()!;
                fakeProbe.State = fakeProbe.State! with { IsForeground = false };
                controller.Refresh();
                Assert(!overlay.IsVisible, "Switching to another foreground app must hide the running overlay.");
                fakeProbe.State = fakeProbe.State! with { IsForeground = true };
                controller.Refresh();
                Assert(overlay.IsVisible, "Returning to the target must restore the subtitle.");
                var hiddenAt = Stopwatch.StartNew();
                controller.SetSuppressed(true);
                Assert(controller.IsSuppressed && !overlay.IsVisible && hiddenAt.Elapsed < TimeSpan.FromSeconds(2),
                    "Explicit hide must hide immediately.");
                await display.ShowAsync(Result(session, 2), CancellationToken.None);
                controller.Refresh();
                await Task.Delay(80);
                Assert(!overlay.IsVisible, "Timer/new subtitle bypassed explicit suppression.");
                controller.SetSuppressed(false);
                Assert(overlay.IsVisible && overlay.DisplayText == "Translation 2",
                    "Show must retain the binding and present the newest subtitle.");
                controller.SetSuppressed(true);
                fakeProbe.State = fakeProbe.State! with { IsForeground = false };
                controller.SetSuppressed(false);
                Assert(!overlay.IsVisible, "Show must not override foreground protection.");
                fakeProbe.State = fakeProbe.State! with { IsForeground = true };
                controller.Refresh();
                Assert(overlay.IsVisible, "Foreground return after Show did not restore subtitles.");
                fakeProbe.State = fakeProbe.State! with { IsVisible = false };
                controller.Refresh();
                Assert(!overlay.IsVisible, "Cloaked/invisible target must hide without requiring new translation.");
                fakeProbe.State = null;
                controller.Refresh();
                Assert(!IsWindow(new WindowInteropHelper(overlay).Handle), "Invalid target must release the overlay HWND.");
                fakeProbe.State = probe.Inspect(confirmed);
                controller.SetSuppressed(true);
                controller.Bind(session, confirmed, options);
                Assert(!controller.IsSuppressed, "A new binding inherited old suppression.");
                overlay = OwnedOverlay()!;
                fakeProbe.Fail = true;
                controller.Refresh();
                Assert(reporter.Errors == 1 && !IsWindow(new WindowInteropHelper(overlay).Handle),
                    "Target-probe failure must be reported safely and release the window.");
            }

            state = probe.Inspect(confirmed)!;
            for (var cycle = 0; cycle < 50; cycle++)
            {
                var overlay = new SubtitleWindow();
                overlay.Present(state, "Lifecycle", options, false);
                var overlayHandle = overlay.NativeHandle;
                overlay.Dispose();
                Assert(!IsWindow(overlayHandle), "Repeated overlay disposal leaked a native window.");
            }
            await SessionControlsTests.RunOnDispatcherAsync(confirmed);
            target.Close();
            Assert(probe.Inspect(confirmed) is null, "Closed target identity remained valid.");
            Console.WriteLine("Overlay native harness: focus, styles, hit-test, bounds, minimize <250 ms and 50 HWND disposal cycles passed. Physical mixed-DPI/click delivery require the explicit manual harness.");
        }
        finally { if (target.IsLoaded) { target.Close(); } }
    }

    private static SubtitleWindow? OwnedOverlay() =>
        PresentationSource.CurrentSources.OfType<HwndSource>().Select(source => source.RootVisual).OfType<SubtitleWindow>().FirstOrDefault();

    private static void AssertBounds(SubtitleWindow window, OverlayTargetState target, OverlayOptions options)
    {
        Assert(GetWindowRect(window.NativeHandle, out var rectangle), "Overlay rectangle unavailable.");
        var expected = OverlayLayout.Place(target.ClientBounds, GetDpiForWindow(window.NativeHandle), options);
        Assert(Math.Abs(rectangle.Left - expected.Left) <= 1 && Math.Abs(rectangle.Top - expected.Top) <= 1 &&
            Math.Abs(rectangle.Right - rectangle.Left - expected.Width) <= 1 && Math.Abs(rectangle.Bottom - rectangle.Top - expected.Height) <= 1,
            $"Overlay physical bounds mismatch: actual {rectangle.Left},{rectangle.Top},{rectangle.Right},{rectangle.Bottom}; expected {expected}.");
    }

    private static void AssertDpiAndClient(Window window, FrameworkElement content)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window);
        Assert(Math.Abs(dpi.DpiScaleX - GetDpiForWindow(handle) / 96d) < .01,
            "Native and WPF DPI disagree after a monitor transition.");
        Assert(GetClientRect(handle, out var client), "Client bounds unavailable after DPI transition.");
        Assert(Math.Abs(content.ActualWidth * dpi.DpiScaleX - (client.Right - client.Left)) < 2 &&
            Math.Abs(content.ActualHeight * dpi.DpiScaleY - (client.Bottom - client.Top)) < 2,
            $"WPF content does not fill its native client after DPI transition: {content.ActualWidth * dpi.DpiScaleX:F1}x{content.ActualHeight * dpi.DpiScaleY:F1} vs {client.Right - client.Left}x{client.Bottom - client.Top}.");
    }

    private static List<NativeRectangle> MonitorWorkAreas()
    {
        var areas = new List<NativeRectangle>();
        MonitorCallback callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            { areas.Add(info.Work); }
            return areas.Count < 16;
        };
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return areas;
    }
    private static TranslationResult Result(SessionId session, long sequence) => new(
        new PipelineMetadata(session, new SequenceNumber(sequence), DateTimeOffset.UnixEpoch, CorrelationId.New()),
        "Source", $"Translation {sequence}", "fake", null, TimeSpan.Zero, false, false);
    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            // Only authored assertion labels and owned-window geometry are printed, never captured text.
            Console.Error.WriteLine(message);
            throw new InvalidOperationException(message);
        }
    }
    private sealed class Reporter : IErrorReporter
    {
        public int Errors;
        public event EventHandler<string>? ErrorReported;
        public void Report(Exception exception) { Errors++; ErrorReported?.Invoke(this, "Overlay failed."); }
    }
    private sealed class Probe : IOverlayTargetProbe
    {
        public OverlayTargetState? State;
        public bool Fail;
        public OverlayTargetState? Inspect(ConfirmedCaptureWindow target) => Fail
            ? throw new InvalidOperationException("Synthetic probe failure") : State;
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRectangle rectangle);
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr device, IntPtr rectangle, IntPtr data);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr device, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRectangle Monitor; public NativeRectangle Work; public uint Flags; }
}

using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Infrastructure.Hotkeys;

internal static class HotkeyRegistrationTests
{
    public static async Task RunAsync()
    {
        TestOwnershipAndFailures();
        await TestNativeAsync();
        Console.WriteLine("Hotkey registration: no-repeat, conflict rollback, stale-message rejection, thread ownership, release retry and native HWND dispatch passed.");
    }

    private static HotkeyOptions Options(params HotkeyBinding[] bindings) => new() { Enabled = true, Bindings = bindings };
    private static HotkeyBinding Binding(HotkeyAction action = HotkeyAction.EmergencyStop, string key = "F11") =>
        new(action, HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, key);

    public static void TestOwnershipAndFailures()
    {
        var api = new FakeApi();
        using var service = new WindowsHotkeyService(1, api);
        var calls = new List<HotkeyAction>();
        service.Triggered += (_, action) => calls.Add(action);
        Assert(service.Apply(Options(Binding())).IsSuccess, "Valid registration failed.");
        var first = api.Registered.Single();
        Assert((first.Value.Modifiers & 0x4000) != 0, "Native autorepeat was not disabled.");
        var messageData = (nint)((first.Value.Key << 16) | (first.Value.Modifiers & 7));
        Assert(!service.ProcessMessage(0x100, first.Key, messageData) &&
            !service.ProcessMessage(WindowsHotkeyService.HotkeyMessage, first.Key, 0), "Non-hotkey/mismatched input was dispatched.");
        Assert(service.ProcessMessage(WindowsHotkeyService.HotkeyMessage, first.Key, messageData) &&
            calls.SequenceEqual(new[] { HotkeyAction.EmergencyStop }), "Registered action did not dispatch.");
        Assert(!service.Apply(new HotkeyOptions { Enabled = true }).IsSuccess && api.Registered.Count == 1,
            "Invalid replacement altered prior registration.");
        Assert(service.Apply(Options(Binding(HotkeyAction.Start))).IsSuccess, "Replacement failed.");
        Assert(!service.ProcessMessage(WindowsHotkeyService.HotkeyMessage, first.Key, messageData), "Stale notification dispatched after reconfiguration.");

        api.FailOnAttempt = api.Attempts + 2;
        Assert(!service.Apply(Options(Binding(), Binding(HotkeyAction.Start, "F10"))).IsSuccess && api.Registered.Count == 0,
            "Partial conflict did not release the successfully registered prefix.");
        api.FailOnAttempt = 0;
        Assert(service.Apply(Options(Binding())).IsSuccess, "Recovery registration failed.");
        api.FailRelease = true;
        var pending = api.Registered.Single();
        Assert(!service.Clear().IsSuccess && !service.ProcessMessage(WindowsHotkeyService.HotkeyMessage, pending.Key, messageData),
            "Release failure was hidden or still dispatched actions.");
        try
        { service.Dispose(); throw new InvalidOperationException("Release failure was not reported."); }
        catch (InvalidOperationException error) when (error.Message.Contains("Windows could not release", StringComparison.Ordinal)) { }
        api.FailRelease = false;
        service.Dispose();
        service.Dispose();
        Assert(api.Registered.Count == 0, "Release retry leaked a registration.");
        try
        { service.Apply(Options(Binding())); throw new InvalidOperationException("Disposed service accepted registration."); }
        catch (ObjectDisposedException) { }

        var otherThreadRejected = false;
        var thread = new Thread(() =>
        {
            try
            { service.Clear(); }
            catch (InvalidOperationException) { otherThreadRejected = true; }
        });
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(5)) && otherThreadRejected, "Wrong-thread native access was not rejected.");
        try
        { using var invalid = new WindowsHotkeyService(0, api); throw new InvalidOperationException("Foreign/null HWND accepted."); }
        catch (ArgumentException) { }
    }

    public static async Task TestNativeAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1), DispatcherPriority.Normal, async (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                try
                {
                    {
                        using var firstWindow = new HwndSource(new HwndSourceParameters("GameLingoAI hotkey test") { ParentWindow = -3 });
                        using var secondWindow = new HwndSource(new HwndSourceParameters("GameLingoAI hotkey conflict test") { ParentWindow = -3 });
                        var observer = new ObservingApi();
                        using var first = new WindowsHotkeyService(firstWindow.Handle, observer);
                        using var second = new WindowsHotkeyService(secondWindow.Handle);
                        firstWindow.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
                        { handled = first.ProcessMessage(message, wParam, lParam); return 0; });
                        var fired = new TaskCompletionSource<HotkeyAction>(TaskCreationOptions.RunContinuationsAsynchronously);
                        first.Triggered += (_, action) => fired.TrySetResult(action);
                        Assert(first.Apply(Options(Binding())).IsSuccess, "Native test combination is unavailable; release Ctrl+Alt+Shift+F11 conflicts and rerun.");
                        Assert(!second.Apply(Options(Binding())).IsSuccess, "Windows accepted a conflicting registration.");
                        // Post an action notification to OUR message-only window, never keyboard input or another app.
                        // Physical chord delivery/foreground independence remains a separate manual smoke.
                        Assert(PostMessage(firstWindow.Handle, WindowsHotkeyService.HotkeyMessage, observer.Id,
                            (nint)((observer.Key << 16) | (observer.Modifiers & 7))), "Own-window notification could not be queued.");
                        Assert(await fired.Task.WaitAsync(TimeSpan.FromSeconds(2)) == HotkeyAction.EmergencyStop, "Native HWND dispatch lost the action.");
                        first.Dispose();
                        Assert(second.Apply(Options(Binding())).IsSuccess, "Disposed owner did not release its native hotkey.");
                        Assert(second.Apply(new HotkeyOptions()).IsSuccess, "Disabling native shortcuts failed.");
                    }
                    {
                        var errors = new Reporter();
                        var observer = new ObservingApi();
                        using var host = new GlobalHotkeyHost(errors, observer);
                        Assert(host.Apply(new HotkeyOptions()).IsSuccess && observer.Window == 0,
                            "Disabled host created a native registration.");
                        Assert(host.Apply(Options(Binding())).IsSuccess && observer.Window != 0, "Message host did not register.");
                        EventHandler<HotkeyAction> failing = (_, _) => throw new InvalidOperationException("Synthetic action failure");
                        host.Triggered += failing;
                        var data = (nint)((observer.Key << 16) | (observer.Modifiers & 7));
                        Assert(PostMessage(observer.Window, WindowsHotkeyService.HotkeyMessage, observer.Id, data), "Host notification failed.");
                        await errors.Reported.Task.WaitAsync(TimeSpan.FromSeconds(2));
                        host.Triggered -= failing;
                        var fired = new TaskCompletionSource<HotkeyAction>(TaskCreationOptions.RunContinuationsAsynchronously);
                        host.Triggered += (_, action) => fired.TrySetResult(action);
                        Assert(PostMessage(observer.Window, WindowsHotkeyService.HotkeyMessage, observer.Id, data), "Host recovery notification failed.");
                        Assert(await fired.Task.WaitAsync(TimeSpan.FromSeconds(2)) == HotkeyAction.EmergencyStop && errors.Count == 1,
                            "Host failed to contain action errors or recover dispatch.");
                        host.Dispose();
                        host.Dispose();
                        using var next = new GlobalHotkeyHost(errors);
                        Assert(next.Apply(Options(Binding())).IsSuccess, "Disposed host retained the shortcut.");
                    }
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }, dispatcher);
            Dispatcher.Run();
            timer.Stop();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(thread.Join(TimeSpan.FromSeconds(5)), "Native hotkey test thread did not exit.");
    }

    private sealed class FakeApi : IWindowsHotkeyApi
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = new();
        public int Attempts, FailOnAttempt;
        public bool FailRelease;
        public bool IsCurrentThreadWindow(nint window) => window == 1;
        public int Register(nint window, int id, uint modifiers, uint key)
        {
            if (++Attempts == FailOnAttempt)
            { return 1409; }
            Registered.Add(id, (modifiers, key));
            return 0;
        }
        public bool Unregister(nint window, int id) => !FailRelease && Registered.Remove(id);
    }

    private sealed class ObservingApi : IWindowsHotkeyApi
    {
        private readonly WindowsHotkeyApi _inner = new();
        public int Id;
        public uint Modifiers, Key;
        public nint Window;
        public bool IsCurrentThreadWindow(nint window) => _inner.IsCurrentThreadWindow(window);
        public int Register(nint window, int id, uint modifiers, uint key)
        { Window = window; Id = id; Modifiers = modifiers; Key = key; return _inner.Register(window, id, modifiers, key); }
        public bool Unregister(nint window, int id) => _inner.Unregister(window, id);
    }

    private sealed class Reporter : IErrorReporter
    {
        public int Count;
        public TaskCompletionSource Reported { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler<string>? ErrorReported { add { } remove { } }
        public void Report(Exception exception) { Count++; Reported.TrySetResult(); }
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, int message, nint wParam, nint lParam);

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using GameLingoAI.App.Composition;
using GameLingoAI.App.Overlay;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using GameLingoAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

// Runs on the isolated native overlay harness's dispatcher, using its explicitly owned target.
internal static class SessionControlsTests
{
    public static async Task RunOnDispatcherAsync(ConfirmedCaptureWindow target)
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-SessionControls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection();
            services.AddGameLingoInfrastructure(Path.Combine(directory, "test.db"));
            services.AddGameLingoApplicationShell();
            var factory = new SessionPipelineTests.Factory();
            var hotkeys = new Hotkeys();
            var configuration = new Configuration();
            var errors = new Reporter();
            services.AddSingleton<ISessionResourcesFactory>(factory);
            services.AddSingleton<IConfigurationStore>(configuration);
            services.AddSingleton<IHotkeyService>(hotkeys);
            services.AddSingleton<IErrorReporter>(errors);
            services.AddSingleton<IOverlayTargetProbe>(new Probe());
            using var provider = services.BuildServiceProvider();
            var session = provider.GetRequiredService<SessionViewModel>();
            try
            {
                var repository = provider.GetRequiredService<IProfileRepository>();
                var profile = new GameProfile(ProfileId.New(), "Session control fixture", "en", "ja", 15,
                    [new CaptureRegion(SessionPipelineTests.Region, "Subtitle", new NormalizedRegion(0, 0, 1, 1), 0)], false,
                    settings: new ProfileSettings { ProviderId = "fake", Model = "model", OcrLanguageModel = "en" });
                await repository.SaveAsync(profile, CancellationToken.None);
                await session.LoadProvidersCommand.ExecuteAsync();
                session.SelectedSavedProfile = session.SavedProfiles.Single();
                var selection = provider.GetRequiredService<WindowSelectionViewModel>();
                selection.Windows.Add(target.Window);
                selection.SelectedWindow = target.Window;
                selection.ConfirmCommand.Execute(true);
                var display = provider.GetRequiredService<SessionDisplay>();
                var overlay = provider.GetRequiredService<OverlayController>();
                var status = provider.GetRequiredService<SessionStatusViewModel>();

                hotkeys.Emit(HotkeyAction.Start);
                await UntilAsync(() => !session.StartCommand.IsExecuting && display.Current is not null);
                overlay.Refresh();
                var window = OwnedOverlay();
                Assert(window?.IsVisible == true, "Running session did not show its native overlay.");
                Assert(!session.StartCommand.CanExecute(null) && session.PauseCommand.CanExecute(null) &&
                    !session.ResumeCommand.CanExecute(null), "Running commands have invalid admission.");
                var first = factory.Last!;
                hotkeys.Emit(HotkeyAction.Start);
                Assert(factory.Calls == 1, "Repeated Start acquired another resource set.");
                hotkeys.Emit(HotkeyAction.ToggleOverlay);
                Assert(!window!.IsVisible && overlay.IsSuppressed && session.OverlayVisibilityLabel == "Show subtitles",
                    "Hide shortcut did not suppress the native overlay.");
                first.TranslationDuration = TimeSpan.FromMilliseconds(17);
                first.Source.Pulses.Writer.TryWrite(true);
                await UntilAsync(() => display.Current?.Sequence == 1);
                overlay.Refresh();
                Assert(!window.IsVisible, "New pipeline output bypassed Hide.");
                hotkeys.Emit(HotkeyAction.ToggleOverlay);
                Assert(window.IsVisible && session.OverlayVisibilityLabel == "Hide subtitles", "Show shortcut failed.");
                Assert(session.SessionDetails.Contains(profile.Name, StringComparison.Ordinal) &&
                    session.SessionDetails.Contains(target.Window.Title, StringComparison.Ordinal) &&
                    session.SessionDetails.Contains("fake", StringComparison.Ordinal) &&
                    session.Latency.Contains("17 ms", StringComparison.Ordinal), "Active identity/latency feedback is incorrect.");

                hotkeys.Emit(HotkeyAction.PauseResume);
                hotkeys.Emit(HotkeyAction.PauseResume);
                await UntilAsync(() => session.ResumeCommand.CanExecute(null));
                overlay.Refresh();
                Assert(first.Source.Active == 0 && first.Disposals == 0 && !window.IsVisible,
                    "Pause did not drain while retaining adapters.");
                hotkeys.Emit(HotkeyAction.PauseResume);
                hotkeys.Emit(HotkeyAction.PauseResume);
                await UntilAsync(() => !session.ResumeCommand.IsExecuting && display.Current?.Sequence == 2);
                Assert(factory.Calls == 1 && session.PauseCommand.CanExecute(null), "Resume reacquired adapters or failed.");

                // Emergency must signal cancellation and hide before a noncooperative request finishes.
                await session.StopCommand.ExecuteAsync();
                first.AssertDisposed();
                factory.IgnoreTranslationCancellation = true;
                await session.StartCommand.ExecuteAsync();
                await UntilAsync(() => display.Current is not null);
                overlay.Refresh();
                window = OwnedOverlay();
                Assert(window?.IsVisible == true, "Emergency fixture has no visible subtitle.");
                var blocked = factory.Last!;
                blocked.TranslationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                blocked.Source.Pulses.Writer.TryWrite(true);
                await UntilAsync(() => blocked.TranslationCalls >= 2);
                var watch = Stopwatch.StartNew();
                hotkeys.Emit(HotkeyAction.EmergencyStop);
                await UntilAsync(() => blocked.TranslationCancellations > 0);
                Assert(!window!.IsVisible && OwnedOverlay() is null && watch.Elapsed < TimeSpan.FromSeconds(2),
                    "Emergency did not hide/cancel within two seconds.");
                Assert(session.EmergencyStopCommand.IsExecuting && !session.StartCommand.CanExecute(null),
                    "Emergency falsely completed before the noncooperative request drained.");
                hotkeys.Emit(HotkeyAction.Start);
                hotkeys.Emit(HotkeyAction.Stop);
                hotkeys.Emit(HotkeyAction.PauseResume);
                hotkeys.Emit(HotkeyAction.EmergencyStop);
                blocked.TranslationGate.TrySetResult();
                await UntilAsync(() => !session.EmergencyStopCommand.IsExecuting);
                Assert(display.Current is null && OwnedOverlay() is null && status.State == SessionState.Stopped,
                    "Late translation escaped emergency stop.");
                blocked.AssertDisposed();

                // Resume is waiting for a first frame: emergency still interrupts it without rebinding.
                factory.IgnoreTranslationCancellation = false;
                await session.StartCommand.ExecuteAsync();
                await UntilAsync(() => display.Current is not null);
                await session.PauseCommand.ExecuteAsync();
                var resuming = factory.Last!;
                resuming.Source.BeforeEnumeration = new TaskCompletionSource().Task;
                var resume = session.ResumeCommand.ExecuteAsync();
                await UntilAsync(() => resuming.Source.Active == 1);
                await session.EmergencyStopCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await resume;
                resuming.AssertDisposed();
                Assert(display.Current is null && !overlay.IsEditing && OwnedOverlay() is null,
                    "Cancelled Resume restored the overlay.");

                // Stop during resource acquisition, before there is any capture enumeration.
                factory.BlockStartup = true;
                factory.Started = false;
                var start = session.StartCommand.ExecuteAsync();
                await UntilAsync(() => factory.Started);
                await session.EmergencyStopCommand.ExecuteAsync().WaitAsync(TimeSpan.FromSeconds(2));
                await start;
                Assert(session.StartCommand.CanExecute(null), "Cancelled startup did not recover.");
                factory.BlockStartup = false;

                // Layout pauses first; a concurrent emergency must prevent its late edit continuation.
                factory.IgnoreTranslationCancellation = true;
                factory.BlockTranslation = true;
                await session.StartCommand.ExecuteAsync();
                var editing = factory.Last!;
                await UntilAsync(() => editing.TranslationCalls == 1);
                var edit = session.EditOverlayCommand.ExecuteAsync();
                await UntilAsync(() => editing.TranslationCancellations > 0);
                Assert(!session.StartCommand.CanExecute(null) && !session.ResumeCommand.CanExecute(null) &&
                    !session.PauseCommand.CanExecute(null) && !session.LoadProvidersCommand.CanExecute(null),
                    "An unfinished layout transition admitted a conflicting command.");
                var emergency = session.EmergencyStopCommand.ExecuteAsync();
                var normalStop = session.StopCommand.ExecuteAsync();
                editing.TranslationGate.TrySetResult();
                await Task.WhenAll(edit, emergency, normalStop).WaitAsync(TimeSpan.FromSeconds(2));
                Assert(!overlay.IsEditing && OwnedOverlay() is null, "Late layout continuation reopened after emergency.");
                editing.AssertDisposed();
                factory.IgnoreTranslationCancellation = false;
                factory.BlockTranslation = false;

                // Cleanup failures remain actionable and do not claim a successful stop.
                factory.Fault = "dispose";
                await session.StartCommand.ExecuteAsync();
                await UntilAsync(() => display.Current is not null);
                await session.StopCommand.ExecuteAsync();
                Assert(status.State == SessionState.Faulted && !session.Message.Contains("Session stopped", StringComparison.Ordinal) &&
                    !session.Message.Contains("SECRET", StringComparison.Ordinal) && OwnedOverlay() is null,
                    "Disposal failure was hidden, leaked details or left an overlay.");
                factory.Last!.AssertDisposed();
                factory.Fault = null;
                await session.StopCommand.ExecuteAsync();

                // Editing with no running session must also be dismissible by emergency stop.
                await session.EditOverlayCommand.ExecuteAsync();
                Assert(overlay.IsEditing && OwnedOverlay()?.IsVisible == true, "Layout edit fixture failed.");
                hotkeys.Emit(HotkeyAction.EmergencyStop);
                await UntilAsync(() => !session.EmergencyStopCommand.IsExecuting);
                Assert(!overlay.IsEditing && OwnedOverlay() is null, "Emergency left interactive layout handles open.");
                var persisted = await repository.GetAsync(profile.Id, CancellationToken.None);
                Assert(configuration.Saves == 0 && persisted is not null && ProfileJson.Export(persisted) == ProfileJson.Export(profile) &&
                    persisted.Settings.SecretReference == profile.Settings.SecretReference,
                    "Session controls changed persisted settings/profile.");
                Assert(errors.Count == 0, "Session controls reported unexpected errors.");
                if (Environment.GetEnvironmentVariable("GAMELINGOAI_SESSION_MANUAL") == "1")
                {
                    ((Probe)provider.GetRequiredService<IOverlayTargetProbe>()).UseNative = true;
                    await RunManualAsync(session, hotkeys, errors, status, target);
                }
            }
            finally
            {
                factory.Last?.TranslationGate.TrySetResult();
                await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert(hotkeys.Subscribers == 0, "Session shutdown leaked the hotkey subscription.");
            Console.WriteLine("Session controls: real overlay hide/show, running/pause/resume, rapid hotkeys, pending provider/start/resume emergency <2s, late-result rejection and unchanged settings passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SubtitleWindow? OwnedOverlay() => PresentationSource.CurrentSources.OfType<HwndSource>()
        .Select(source => source.RootVisual).OfType<SubtitleWindow>().FirstOrDefault();
    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5))
            { throw new TimeoutException("Session control condition did not complete."); }
            await Task.Delay(5);
        }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Probe : IOverlayTargetProbe
    {
        public bool UseNative;
        public OverlayTargetState? Inspect(ConfirmedCaptureWindow target) =>
            UseNative ? new WindowsOverlayTargetProbe().Inspect(target) : new(new PixelBounds(100, 100, 640, 480), 96, true, true);
    }

    private static async Task RunManualAsync(SessionViewModel session, Hotkeys relay, Reporter errors,
        SessionStatusViewModel status, ConfirmedCaptureWindow target)
    {
        var handle = unchecked((nint)(long)target.Window.Identity.Id.Value);
        var window = HwndSource.FromHwnd(handle)?.RootVisual as Window ?? throw new InvalidOperationException("Manual target disappeared.");
        var actions = new[] { HotkeyAction.Start, HotkeyAction.PauseResume, HotkeyAction.ToggleOverlay, HotkeyAction.Stop, HotkeyAction.EmergencyStop };
        var options = new HotkeyOptions
        {
            Enabled = true,
            Bindings = actions.Select((action, index) => new HotkeyBinding(action,
                HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, $"F{index + 7}")).ToArray()
        };
        using var host = new GlobalHotkeyHost(errors);
        var counts = actions.ToDictionary(action => action, _ => 0);
        var outside = 0;
        var focusChanged = false;
        var enabled = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            FontSize = 18,
            TextWrapping = TextWrapping.Wrap,
            Text = "TASK-020 真实按键验收（仅假字幕，无游戏/网络）\n" +
                "先点击启用。全部组合均为 Ctrl + Alt + Shift + 功能键：\n" +
                "F7 开始；F8 暂停/恢复（按两次）；F9 隐藏/显示（按两次）；F10 停止；F11 紧急停止。\n" +
                "顺序建议：F7 → F8 → F8 → F9 → F9 → F10 → F7。\n" +
                "然后切到文件资源管理器等其他应用，再按 F11，确认焦点没有跳回。最后回来点击完成。\n" +
                "切到其他应用时字幕自动隐藏是正常行为。关闭测试窗口会取消验收并释放快捷键。"
        });
        var enable = new Button { Content = "启用上述测试快捷键（仅本次）", Margin = new Thickness(0, 14, 0, 8), FontSize = 17 };
        var feedback = new TextBlock { FontSize = 17, TextWrapping = TextWrapping.Wrap };
        var finish = new Button { Content = "完成验收并关闭", IsEnabled = false, Margin = new Thickness(0, 12, 0, 0), FontSize = 17 };
        panel.Children.Add(enable);
        panel.Children.Add(feedback);
        panel.Children.Add(finish);
        window.Content = panel;
        window.Title = "GameLingoAI TASK-020 真实快捷键测试";
        window.Width = 900;
        window.Height = 680;
        window.Activate();
        host.Triggered += (_, action) =>
        {
            var foreground = GetForegroundWindow();
            counts[action]++;
            if (foreground != handle && foreground != 0)
            { outside++; }
            relay.Emit(action);
            focusChanged |= GetForegroundWindow() != foreground;
        };
        enable.Click += (_, _) =>
        {
            var applied = host.Apply(options);
            enabled = applied.IsSuccess;
            enable.Content = applied.IsSuccess ? "已启用；关闭即释放" : applied.Message;
            enable.IsEnabled = !applied.IsSuccess;
        };
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            feedback.Text = $"会话：{status.State}；{session.OverlayVisibilityLabel}\n" +
                $"按键收到：F7={counts[HotkeyAction.Start]}，F8={counts[HotkeyAction.PauseResume]}，F9={counts[HotkeyAction.ToggleOverlay]}，F10={counts[HotkeyAction.Stop]}，F11={counts[HotkeyAction.EmergencyStop]}\n" +
                $"其他窗口前台触发：{outside}；检测到抢焦点：{focusChanged}\n{session.Message}";
            finish.IsEnabled = enabled && counts[HotkeyAction.Start] >= 2 && counts[HotkeyAction.PauseResume] >= 2 &&
                counts[HotkeyAction.ToggleOverlay] >= 2 && counts[HotkeyAction.Stop] >= 1 && counts[HotkeyAction.EmergencyStop] >= 1 &&
                outside > 0 && !focusChanged && status.State == SessionState.Stopped && !session.EmergencyStopCommand.IsExecuting;
        }, Dispatcher.CurrentDispatcher);
        finish.Click += (_, _) => completion.TrySetResult();
        EventHandler closed = (_, _) => completion.TrySetException(new InvalidOperationException("Manual shortcut acceptance was closed before completion."));
        window.Closed += closed;
        Console.WriteLine("MANUAL READY: click Enable in the owned TASK-020 test window; use the displayed physical chords, then Finish. No keys are simulated.");
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromMinutes(14));
            Assert(errors.Count == 0, "Manual shortcut host reported an error.");
            Assert(host.Clear().IsSuccess, "Manual shortcuts failed to unregister.");
            using var releaseProbe = new GlobalHotkeyHost(errors);
            Assert(releaseProbe.Apply(options).IsSuccess, "Manual shortcut release/re-registration failed.");
            Assert(releaseProbe.Clear().IsSuccess, "Manual release probe could not clear.");
            Console.WriteLine($"Manual physical shortcut acceptance passed: all five actions received; {outside} foreground-other notifications; focus unchanged; release/re-registration verified.");
        }
        finally { timer.Stop(); window.Closed -= closed; }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    private sealed class Hotkeys : IHotkeyService
    {
        public event EventHandler<HotkeyAction>? Triggered;
        public int Subscribers => Triggered?.GetInvocationList().Length ?? 0;
        public void Emit(HotkeyAction action) => Triggered?.Invoke(this, action);
        public HotkeyApplyResult Apply(HotkeyOptions options) => new(true, "Applied");
        public HotkeyApplyResult Clear() => new(true, "Cleared");
        public void Dispose() { }
    }
    private sealed class Reporter : IErrorReporter
    {
        public int Count;
        public event EventHandler<string>? ErrorReported;
        public void Report(Exception exception) { Count++; ErrorReported?.Invoke(this, "Synthetic error."); }
    }
    private sealed class Configuration : IConfigurationStore
    {
        public int Saves;
        public Task<OperationResult<AppSettings>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success(new AppSettings
            {
                Providers = [new ProviderOptions { Id = "fake", Kind = ProviderKind.Ollama, Model = "model" }]
            }));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        { Saves++; return Task.CompletedTask; }
        public Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

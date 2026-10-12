using System.Windows.Threading;
using GameLingoAI.App.Composition;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

internal static class SessionMaintenanceTests
{
    public static async Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-SessionMaintenance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            var kickoff = new DispatcherTimer(TimeSpan.FromMilliseconds(1), DispatcherPriority.Normal, async (sender, _) =>
            {
                ((DispatcherTimer)sender!).Stop();
                try
                {
                    var services = new ServiceCollection();
                    services.AddGameLingoInfrastructure(Path.Combine(directory, "test.db"));
                    services.AddGameLingoApplicationShell();
                    var factory = new InspectingFactory();
                    var configuration = new Configuration();
                    var hotkeys = new Hotkeys();
                    services.AddSingleton<ISessionResourcesFactory>(factory);
                    services.AddSingleton<IConfigurationStore>(configuration);
                    services.AddSingleton<IHotkeyService>(hotkeys);
                    using (var provider = services.BuildServiceProvider())
                    {
                        var session = provider.GetRequiredService<SessionViewModel>();
                        session.SelectedProvider = new ProviderOptions { Id = "fake" };
                        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var maintenance = session.RunAsync(async token =>
                        { entered.TrySetResult(); await release.Task.WaitAsync(token); }, CancellationToken.None);
                        await entered.Task;
                        var start = session.StartCommand.ExecuteAsync();
                        if (start.IsCompleted || !session.StartCommand.IsExecuting)
                        { throw new InvalidOperationException("Session startup did not wait behind active database maintenance."); }
                        release.TrySetResult();
                        await maintenance;
                        await start;
                        if (!session.Message.Contains("Confirm", StringComparison.Ordinal))
                        { throw new InvalidOperationException("Session startup bypassed target confirmation after maintenance."); }
                        await TestSavedProfileAsync(provider, session, factory);
                        await TestEmergencyBeforeControllerStartAsync(session, hotkeys, factory);
                        await TestLoadDuringShutdownAsync(session, configuration);
                        hotkeys.Emit(HotkeyAction.Start);
                        hotkeys.Emit(HotkeyAction.EmergencyStop);
                        if (session.StartCommand.IsExecuting || session.EmergencyStopCommand.IsExecuting || hotkeys.Subscribers != 0)
                        { throw new InvalidOperationException("Shutdown retained a session hotkey subscription."); }
                        var invoked = false;
                        try
                        { await session.RunAsync(_ => { invoked = true; return Task.CompletedTask; }, CancellationToken.None); }
                        catch (InvalidOperationException) { }
                        if (invoked)
                        { throw new InvalidOperationException("Maintenance ran after session shutdown."); }
                    }
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.InvokeShutdown(); }
            }, dispatcher);
            Dispatcher.Run();
            kickoff.Stop();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        { await completion.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("Actual session commands: startup waits for maintenance, target confirmation remains required, shutdown rejects maintenance.");
    }

    private static async Task TestSavedProfileAsync(ServiceProvider services, SessionViewModel session, InspectingFactory factory)
    {
        var repository = services.GetRequiredService<IProfileRepository>();
        var original = new GameProfile(ProfileId.New(), "Saved fixture", "ja", "en", 8,
            new[] { new CaptureRegion(Guid.NewGuid(), "Saved region", new NormalizedRegion(.1, .7, .8, .2), 0) }, false,
            settings: new ProfileSettings { ProviderId = "local", Model = "old-model", OcrLanguageModel = "japan" });
        await repository.SaveAsync(original, CancellationToken.None);
        await session.LoadProvidersCommand.ExecuteAsync();
        var selection = services.GetRequiredService<WindowSelectionViewModel>();
        // Model-only fixture: the factory below never creates capture or touches this HWND.
        var window = new CaptureWindow(new WindowIdentity(new WindowId(123), 456, 789), "Fixture", "fixture",
            new PixelBounds(0, 0, 640, 480), 96, false);
        selection.Windows.Add(window);
        selection.SelectedWindow = window;
        selection.ConfirmCommand.Execute(true);
        if (selection.ConfirmedSelection is null)
        { throw new InvalidOperationException("Synthetic confirmation fixture failed."); }
        session.SelectedSavedProfile = session.SavedProfiles.Single();
        if (selection.ConfirmedSelection is not null || factory.Calls != 0)
        { throw new InvalidOperationException("Profile selection inherited capture authorization or started automatically."); }
        await session.StartCommand.ExecuteAsync();
        if (factory.Calls != 0)
        { throw new InvalidOperationException("Unconfirmed saved profile reached resource creation."); }
        var updated = new GameProfile(original.Id, "Updated after selection", "ja", "zh-Hans", 14,
            original.Regions, true, PreprocessingOptions.OutlinedSubtitle,
            original.Settings with { Model = "fresh-model", Overlay = new OverlayOptions { FontSize = 43 }, Cache = new CacheOptions { MaximumRows = 37 } });
        await repository.SaveAsync(updated, CancellationToken.None);
        selection.SelectedWindow = window;
        selection.ConfirmCommand.Execute(true);
        await session.StartCommand.ExecuteAsync();
        var request = factory.Request;
        if (factory.Calls != 1 || request is null || request.Profile.Id != original.Id || request.Profile.Name != updated.Name ||
            request.Provider.Model != "fresh-model" || request.Settings.Capture.FramesPerSecond != 14 ||
            request.Settings.Overlay.FontSize != 43 || request.Settings.Cache.MaximumRows != 37 ||
            request.Settings.Ocr.LanguageModel != "ja" || !request.Settings.Privacy.HistoryEnabled ||
            request.Settings.Privacy.RemoteTextProcessingConsent || request.Profile.Regions[0].Id != original.Regions[0].Id)
        { throw new InvalidOperationException("Actual session did not reload/apply saved profile settings and identity."); }
        await session.StopCommand.ExecuteAsync();
        await repository.DeleteAsync(original.Id, CancellationToken.None);
        await session.StartCommand.ExecuteAsync();
        if (factory.Calls != 1 || !session.Message.Contains("no longer exists", StringComparison.Ordinal))
        { throw new InvalidOperationException("Deleted saved profile fell back to an old snapshot or ad-hoc session."); }
        await session.UseAdHocCommand.ExecuteAsync();
        if (session.SelectedSavedProfile is not null || selection.ConfirmedSelection is not null)
        { throw new InvalidOperationException("Ad-hoc switch retained profile authorization."); }
    }

    private static async Task TestLoadDuringShutdownAsync(SessionViewModel session, Configuration configuration)
    {
        configuration.HoldNextLoad = true;
        var selected = session.Providers.Single();
        session.SelectedProvider = selected;
        var message = session.Message;
        var load = session.LoadProvidersCommand.ExecuteAsync();
        await configuration.Entered.Task;
        try
        { await session.ShutdownAsync(); }
        finally { configuration.Release.TrySetResult(); }
        await load;
        if (session.SelectedProvider != selected || session.Message != message)
        { throw new InvalidOperationException("A delayed configuration load changed session UI state after shutdown."); }
    }

    private static async Task TestEmergencyBeforeControllerStartAsync(SessionViewModel session, Hotkeys hotkeys, InspectingFactory factory)
    {
        session.SelectedProvider = session.Providers.Single();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var maintenance = session.RunAsync(_ => release.Task, CancellationToken.None);
        var calls = factory.Calls;
        hotkeys.Emit(HotkeyAction.Start);
        if (!session.StartCommand.IsExecuting || !session.StopCommand.CanExecute(null))
        { throw new InvalidOperationException("Hotkey Start was not routed or pending startup cannot be stopped."); }
        hotkeys.Emit(HotkeyAction.Start);
        hotkeys.Emit(HotkeyAction.PauseResume);
        hotkeys.Emit(HotkeyAction.ToggleOverlay);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var stop = session.EmergencyStopCommand.ExecuteAsync();
        hotkeys.Emit(HotkeyAction.EmergencyStop);
        await Task.Delay(20);
        if (session.StartCommand.IsExecuting || watch.Elapsed >= TimeSpan.FromSeconds(2) ||
            session.StartCommand.CanExecute(null) || factory.Calls != calls)
        { throw new InvalidOperationException("Emergency failed to cancel pending startup or allowed reentry."); }
        release.TrySetResult();
        await maintenance;
        await stop;
        if (!session.StartCommand.CanExecute(null) || session.StopCommand.CanExecute(null) ||
            session.PauseCommand.CanExecute(null) || session.ResumeCommand.CanExecute(null) ||
            session.ToggleOverlayCommand.CanExecute(null))
        { throw new InvalidOperationException("Stopped command availability is incorrect."); }
        hotkeys.Emit(HotkeyAction.EmergencyStop);
        if (!session.Message.Contains("settings are unchanged", StringComparison.Ordinal))
        { throw new InvalidOperationException("Emergency hotkey did not dispatch safely while stopped."); }
    }

    private sealed class Hotkeys : IHotkeyService
    {
        public event EventHandler<HotkeyAction>? Triggered;
        public int Subscribers => Triggered?.GetInvocationList().Length ?? 0;
        public void Emit(HotkeyAction action) => Triggered?.Invoke(this, action);
        public HotkeyApplyResult Apply(HotkeyOptions options) => new(true, "Applied");
        public HotkeyApplyResult Clear() => new(true, "Cleared");
        public void Dispose() { }
    }

    private sealed class InspectingFactory : ISessionResourcesFactory
    {
        public int Calls { get; private set; }
        public SessionStartRequest? Request { get; private set; }
        public Task<SessionResources> CreateAsync(SessionStartRequest request, SessionId sessionId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Request = request;
            throw new SessionStageException(new PipelineFailure(FailureCode.ModelMissing,
                "Synthetic stop before capture creation.", CorrelationId.New(), false));
        }
    }
    private sealed class Configuration : IConfigurationStore
    {
        public bool HoldNextLoad { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OperationResult<AppSettings>> LoadAsync(CancellationToken cancellationToken)
        {
            if (HoldNextLoad)
            {
                Entered.TrySetResult();
                // Emulate a completed read whose continuation arrives after cancellation.
                await Release.Task;
            }
            return OperationResult.Success(new AppSettings
            {
                Providers = new[] { new ProviderOptions { Id = "local", Kind = ProviderKind.Ollama, Model = "configured" } }
            });
        }
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

using System.ComponentModel;
using System.Windows;
using GameLingoAI.App.Composition;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GameLingoAI.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns Application lifetime; the startup cancellation source is cancelled and disposed in OnExit.")]
public partial class App : Application
{
    private ServiceProvider? _services;
    private readonly Services.ApplicationDiagnostics _diagnostics = new();
    private readonly CancellationTokenSource _startupCancellation = new();
    private Task _diagnosticsInitialization = Task.CompletedTask;
    private bool _closing;
    private bool _shutdownReady;
    private string? _syntheticDataDirectory;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var services = new ServiceCollection();
            if (e.Args.Contains("--synthetic-test", StringComparer.Ordinal))
            { _syntheticDataDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "GameLingoAI-Synthetic-" + Guid.NewGuid().ToString("N")); }
            services.AddGameLingoInfrastructure(_syntheticDataDirectory is null ? null : System.IO.Path.Combine(_syntheticDataDirectory, "test.db"));
            if (_syntheticDataDirectory is not null)
            {
                // Manual settings acceptance must never overwrite the user's normal configuration.
                services.AddSingleton<GameLingoAI.Core.Abstractions.IConfigurationStore>(
                    new GameLingoAI.Infrastructure.Configuration.JsonConfigurationStore(
                        System.IO.Path.Combine(_syntheticDataDirectory, "test-settings.json")));
            }
            services.AddGameLingoApplicationShell();
            // Instance registration leaves asynchronous ownership with the App, not synchronous DI disposal.
            services.AddSingleton<GameLingoAI.Core.Abstractions.IDiagnosticEvents>(_diagnostics);
            services.AddSingleton(_diagnostics);
            _services = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
            var window = _services.GetRequiredService<MainWindow>();
            window.Closing += OnWindowClosing;
            window.Loaded += OnMainWindowLoaded;
            window.Show();
        }
        catch (Exception)
        {
            var title = TryFindResource("StartupErrorTitle") as string ?? "GameLingoAI could not start";
            var message = TryFindResource("StartupErrorMessage") as string ??
                "The application could not initialize. Verify the runtime and configuration.";
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _diagnostics.RequestShutdown();
        _startupCancellation.Cancel();
        _startupCancellation.Dispose();
        _services?.Dispose();
        if (_syntheticDataDirectory is not null && System.IO.Directory.Exists(_syntheticDataDirectory))
        {
            try
            { System.IO.Directory.Delete(_syntheticDataDirectory, recursive: true); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        base.OnExit(e);
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        ((Window)sender).Loaded -= OnMainWindowLoaded;
        if (_closing)
        { return; }
        try
        {
            _diagnosticsInitialization = _diagnostics.InitializeAsync(
                _services!.GetRequiredService<GameLingoAI.Core.Abstractions.IConfigurationStore>(), retained =>
            {
                if (_syntheticDataDirectory is not null)
                { return GameLingoAI.Infrastructure.Diagnostics.DiagnosticWriters.CreateProduction(System.IO.Path.Combine(_syntheticDataDirectory, "logs"), retained); }
#if DEBUG
                return GameLingoAI.Infrastructure.Diagnostics.DiagnosticWriters.CreateDevelopment();
#else
            return GameLingoAI.Infrastructure.Diagnostics.DiagnosticWriters.CreateProduction(retainedFileCount: retained);
#endif
            }, _startupCancellation.Token);
            await _diagnosticsInitialization;
            if (_closing)
            { return; }
            await _services!.GetRequiredService<DatabaseViewModel>().InitializeAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _services!.GetRequiredService<Services.IErrorReporter>().Report(error); }
    }

    private async void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdownReady)
        { return; }
        e.Cancel = true;
        if (_closing)
        { return; }
        var profiles = _services!.GetRequiredService<ProfilesViewModel>();
        var hotkeys = _services!.GetRequiredService<HotkeysViewModel>();
        if ((profiles.HasUnsavedChanges || hotkeys.HasUnsavedChanges) && MessageBox.Show((Window)sender!,
            "Profile or shortcut definitions have unsaved changes. Close and discard those changes? Choose No to save first.",
            "Unsaved settings", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { return; }
        _closing = true;
        _startupCancellation.Cancel();
        try
        {
            try
            { await _diagnosticsInitialization; }
            catch (OperationCanceledException) { }
            await hotkeys.ShutdownAsync();
            await _services!.GetRequiredService<DiagnosticsViewModel>().ShutdownAsync();
            await _services!.GetRequiredService<SessionViewModel>().ShutdownAsync();
            await profiles.ShutdownAsync();
            await _services!.GetRequiredService<DatabaseViewModel>().ShutdownAsync();
            await _diagnostics.ShutdownAsync();
            // Empty-session cleanup can complete synchronously. Leave the current Closing
            // event before invoking Close again, otherwise WPF rejects the reentrant call.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            _shutdownReady = true;
            ((Window)sender!).Close();
        }
        catch (Exception)
        {
            _closing = false;
            MessageBox.Show("Session shutdown did not finish. Stop the session and retry closing.", "GameLingoAI");
        }
    }
}

using GameLingoAI.App.Commands;
using GameLingoAI.App.Composition;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure;
using GameLingoAI.Infrastructure.Ocr;
using Microsoft.Extensions.DependencyInjection;

internal static class UiShellTests
{
    public static async Task RunAsync()
    {
        TestNavigationWithoutWindow();
        TestServiceLifetimesAndGraph();
        TestMainWindowConstruction();
        await TestAsyncCommandDuplicatePreventionAsync().ConfigureAwait(false);
        await TestAsyncCommandCancellationAsync().ConfigureAwait(false);
        await TestAsyncCommandExceptionHandlingAsync().ConfigureAwait(false);
    }

    private static void TestMainWindowConstruction()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new GameLingoAI.App.App();
                app.InitializeComponent();
                var services = new ServiceCollection();
                services.AddGameLingoInfrastructure();
                services.AddGameLingoApplicationShell();
                using var provider = services.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });
                var window = provider.GetRequiredService<GameLingoAI.App.MainWindow>();
                window.Show();
                TestProfileControls(window, provider.GetRequiredService<ShellViewModel>());
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert(thread.Join(TimeSpan.FromSeconds(15)), "Main window startup test timed out.");
        Assert(failure is null, $"Main window failed during startup: {failure}");
    }

    private static void TestProfileControls(GameLingoAI.App.MainWindow window, ShellViewModel shell)
    {
        shell.NavigateCommand.Execute(PageId.ProfilesSettings);
        var editor = shell.Profiles ?? throw new InvalidOperationException("Profile editor is missing from application composition.");
        editor.NewCommand.Execute(null);
        window.UpdateLayout();
        var view = Descendants(window).OfType<GameLingoAI.App.Views.ProfilesView>().Single();
        foreach (var expander in Descendants(view).OfType<System.Windows.Controls.Expander>().ToArray())
        { expander.IsExpanded = true; }
        window.UpdateLayout();
        var inputs = Descendants(view).OfType<System.Windows.Controls.TextBox>()
            .Where(input => input.DataContext is ProfileFormField).ToArray();
        Assert(inputs.Length > 30, "Profile form did not generate its settings controls.");
        foreach (var input in inputs.Where(input => input.IsVisible))
        {
            Assert(input.Focusable && input.IsTabStop &&
                !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(input)),
                "A visible profile input lacks keyboard focus or an accessible name.");
        }
        var name = inputs.Single(input => ((ProfileFormField)input.DataContext).Path == "name");
        name.SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "UI binding fixture");
        name.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        Assert(editor.ReadDraft().Name == "UI binding fixture", "Profile text control did not update the draft through its binding.");
        var history = Descendants(view).OfType<System.Windows.Controls.CheckBox>()
            .Single(input => input.DataContext is ProfileFormField { Path: "privacy.historyEnabled" });
        Assert(history.IsVisible && history.IsTabStop && history.Focusable &&
            !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(history)),
            "Boolean profile control is inaccessible.");
        history.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
        history.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateSource();
        Assert(editor.ReadDraft().HistoryEnabled, "Profile checkbox did not update the draft through its binding.");
        var hotkeys = shell.Hotkeys ?? throw new InvalidOperationException("Hotkey settings editor is missing.");
        var hotkeyView = Descendants(window).OfType<GameLingoAI.App.Views.HotkeysView>().Single();
        var keyInputs = Descendants(hotkeyView).OfType<System.Windows.Controls.TextBox>().ToArray();
        Assert(keyInputs.Length == 5 && keyInputs.All(input => input.IsVisible && input.Focusable && input.IsTabStop &&
            !string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetName(input))),
            "Hotkey fields are missing keyboard/accessibility support.");
        keyInputs[0].SetCurrentValue(System.Windows.Controls.TextBox.TextProperty, "F6");
        keyInputs[0].GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
        Assert(hotkeys.Rows[0].Key == "F6" && hotkeys.HasUnsavedChanges && !hotkeys.Enabled,
            "Hotkey edit binding failed or implicitly enabled registrations.");
        shell.NavigateCommand.Execute(PageId.HomeSession);
        window.UpdateLayout();
        var session = shell.Session ?? throw new InvalidOperationException("Session UI is missing.");
        var buttons = Descendants(window).OfType<System.Windows.Controls.Button>().ToArray();
        var emergency = buttons.Single(button => ReferenceEquals(button.Command, session.EmergencyStopCommand));
        var visibility = buttons.Single(button => ReferenceEquals(button.Command, session.ToggleOverlayCommand));
        Assert(emergency.IsVisible && emergency.IsEnabled && emergency.Focusable && emergency.IsTabStop &&
            visibility.IsVisible && !visibility.IsEnabled && (string)visibility.Content == session.OverlayVisibilityLabel,
            "Emergency/visibility controls have incorrect bindings or keyboard admission.");
        var feedback = Descendants(window).OfType<System.Windows.Controls.TextBlock>().ToArray();
        Assert(feedback.Any(text => text.IsVisible && text.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty)?
                .ParentBinding.Path.Path == "Session.SessionDetails") &&
            feedback.Any(text => text.IsVisible && text.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty)?
                .ParentBinding.Path.Path == "Session.Latency"), "Session identity/latency feedback is not bound visibly.");
    }

    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var nested in Descendants(child))
            { yield return nested; }
        }
    }

    private static void TestNavigationWithoutWindow()
    {
        var reporter = new TestErrorReporter();
        var windowSelection = new WindowSelectionViewModel(new EmptyWindowDiscovery(), reporter);
        using var regionEditor = new RegionEditorViewModel(
            new EmptyRegionDraftStore(),
            new IdleRegionPreview(),
            windowSelection,
            reporter);
        using var ocrModel = CreateOcrModel(reporter);
        var viewModel = new ShellViewModel(new SessionStatusViewModel(), windowSelection, regionEditor, ocrModel, reporter);
        var changes = new List<string?>();
        viewModel.PropertyChanged += (_, eventArgs) => changes.Add(eventArgs.PropertyName);

        viewModel.NavigateCommand.Execute(PageId.Diagnostics);
        Assert(viewModel.CurrentPage == PageId.Diagnostics, "Navigation command should change the current page.");
        Assert(changes.Contains(nameof(ShellViewModel.CurrentPage)), "Navigation should be observable.");

        reporter.Report(new InvalidOperationException("sensitive technical detail"));
        Assert(viewModel.LastError is not null, "Safe command errors should be observable.");
        Assert(
            !(viewModel.LastError ?? string.Empty).Contains("sensitive", StringComparison.Ordinal),
            "UI errors must not expose exception text.");
    }

    private static void TestServiceLifetimesAndGraph()
    {
        var services = new ServiceCollection();
        services.AddGameLingoInfrastructure();
        services.AddGameLingoApplicationShell();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        Assert(ReferenceEquals(
            provider.GetRequiredService<ShellViewModel>(),
            provider.GetRequiredService<ShellViewModel>()), "Shell ViewModel should be singleton.");
        Assert(!ReferenceEquals(
            provider.GetRequiredService<PlaceholderPageViewModel>(),
            provider.GetRequiredService<PlaceholderPageViewModel>()), "Placeholder pages should be transient.");

        SessionContext firstSession;
        using (var scope = provider.CreateScope())
        {
            firstSession = scope.ServiceProvider.GetRequiredService<SessionContext>();
            Assert(ReferenceEquals(
                firstSession,
                scope.ServiceProvider.GetRequiredService<SessionContext>()), "Session context should be scoped.");
        }

        using var secondScope = provider.CreateScope();
        Assert(!ReferenceEquals(
            firstSession,
            secondScope.ServiceProvider.GetRequiredService<SessionContext>()), "Different scopes need different sessions.");
    }

    private static async Task TestAsyncCommandDuplicatePreventionAsync()
    {
        var reporter = new TestErrorReporter();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executions = 0;
        using var command = new AsyncRelayCommand(
            async cancellationToken =>
            {
                executions++;
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            },
            reporter);

        var first = command.ExecuteAsync();
        await command.ExecuteAsync().ConfigureAwait(false);
        Assert(executions == 1, "Duplicate execution must be ignored while a command is running.");
        release.SetResult();
        await first.ConfigureAwait(false);
        Assert(!command.IsExecuting, "Command should reset execution state.");
    }

    private static async Task TestAsyncCommandCancellationAsync()
    {
        var reporter = new TestErrorReporter();
        using var command = new AsyncRelayCommand(
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
            reporter);
        var execution = command.ExecuteAsync();
        command.Cancel();
        await execution.ConfigureAwait(false);
        Assert(reporter.Messages.Count == 0, "Expected cancellation should not be reported as an error.");
    }

    private static async Task TestAsyncCommandExceptionHandlingAsync()
    {
        var reporter = new TestErrorReporter();
        using var command = new AsyncRelayCommand(
            _ => Task.FromException(new InvalidOperationException("boom")),
            reporter);
        await command.ExecuteAsync().ConfigureAwait(false);
        Assert(reporter.Messages.Count == 1, "Command exceptions must be caught and reported.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static OcrModelViewModel CreateOcrModel(IErrorReporter reporter) => new(
        new OcrModelProvisioner(new HttpClient(), Path.Combine(Path.GetTempPath(), "GameLingoAI-UiShellTests")),
        PaddleOcrModelCatalog.PpOcrV5Mobile,
        reporter);

    private sealed class TestErrorReporter : IErrorReporter
    {
        public event EventHandler<string>? ErrorReported;

        public List<string> Messages { get; } = new();

        public void Report(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            const string message = "A safe error occurred.";
            Messages.Add(message);
            ErrorReported?.Invoke(this, message);
        }
    }

    private sealed class EmptyWindowDiscovery : IWindowDiscovery
    {
        public Task<OperationResult<IReadOnlyList<CaptureWindow>>> DiscoverAsync(
            IReadOnlySet<WindowId> excludedWindows,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult.Success<IReadOnlyList<CaptureWindow>>(Array.Empty<CaptureWindow>()));
        }
    }

    private sealed class EmptyRegionDraftStore : IRegionDraftStore
    {
        public Task<RegionDraft?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<RegionDraft?>(null);
        }

        public Task SaveAsync(RegionDraft draft, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class IdleRegionPreview : ILocalRegionPreview
    {
        public event EventHandler<LocalPreviewFrame>? FrameReady
        {
            add { }
            remove { }
        }

        public event EventHandler<Exception>? PreviewFailed
        {
            add { }
            remove { }
        }

        public bool IsRunning => false;

        public Task WhenStopped => Task.CompletedTask;

        public Task StartAsync(
            ConfirmedCaptureWindow target,
            SessionId sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public void RequestStop()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

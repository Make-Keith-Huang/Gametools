using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameLingoAI.App.Overlay;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using GameLingoAI.Infrastructure;
using GameLingoAI.Infrastructure.Ocr;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Explicitly authorized, owned-window acceptance; never run by the default test suite.</summary>
internal static class LocalPipelineHarness
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    public static async Task RunAsync(string modelRoot, string evidenceRoot, bool lifecycle = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Startup += async (_, _) =>
            {
                try
                {
                    await RunOnDispatcherAsync(modelRoot, evidenceRoot, lifecycle ? 50 : 1);
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { application.Shutdown(); }
            };
            application.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromMinutes(lifecycle ? 16 : 4));
    }

    private static async Task RunOnDispatcherAsync(string modelRoot, string evidenceRoot, int cycles)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(cycles == 1 ? 3 : 15));
        var token = timeout.Token;
        // A new directory is mandatory: no existing database/settings are read or overwritten.
        var runRoot = System.IO.Path.Combine(System.IO.Path.GetFullPath(evidenceRoot), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(runRoot);
        using var noDownloads = new HttpClient(new RejectDownloads());
        var services = new ServiceCollection();
        services.AddGameLingoInfrastructure(System.IO.Path.Combine(runRoot, "synthetic.db"));
        services.AddSingleton(new OcrModelProvisioner(noDownloads, modelRoot));
        services.AddSingleton<IRemoteTextConsent, DeniedConsent>();
        using var provider = services.BuildServiceProvider();
        var display = new SessionDisplay();
        var errors = new CapturedErrors();
        using var overlay = new OverlayController(new WindowsOverlayTargetProbe(), display, errors);
        await using var controller = new SessionController(provider.GetRequiredService<ISessionResourcesFactory>(), display);
        var text = new TextBlock
        {
            Text = "Hello, world!",
            FontSize = 42,
            Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var target = new Window
        {
            Title = "GameLingoAI authorized local pipeline fixture",
            Width = 720,
            Height = 420,
            Background = Brushes.White,
            Content = text,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        var cases = new List<object>();
        var sessions = new List<object>();
        var sessionIds = new HashSet<SessionId>();
        double? startupMilliseconds = null;
        double? startToFirstVisibleMilliseconds = null;
        double? resumeToCachedVisibleMilliseconds = null;
        double? stopMilliseconds = null;
        var passed = false;
        try
        {
            target.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var handle = new WindowInteropHelper(target).Handle;
            using var process = Process.GetCurrentProcess();
            var identity = new WindowIdentity(new WindowId(unchecked((ulong)handle.ToInt64())), process.Id,
                process.StartTime.ToUniversalTime().Ticks);
            var ownWindow = new CaptureWindow(identity, "Authored fixture", "Owned harness",
                new PixelBounds(0, 0, 720, 420), 96, false);
            var tracker = new WindowSelectionTracker();
            tracker.Select(ownWindow);
            tracker.Confirm([ownWindow]);
            var targetState = new WindowsOverlayTargetProbe().Inspect(tracker.ConfirmedSelection!)
                ?? throw new InvalidOperationException("Owned target missing.");
            ownWindow = new CaptureWindow(identity, "Authored fixture", "Owned harness",
                targetState.ClientBounds, targetState.Dpi, false);
            tracker.Select(ownWindow);
            tracker.Confirm([ownWindow]);
            var options = new ProviderOptions
            {
                Id = "local-pipeline",
                Kind = ProviderKind.Ollama,
                Model = "qwen2.5:3b",
                BaseUri = new Uri("http://127.0.0.1:11434/"),
                TimeoutSeconds = 60,
                MaximumOutputTokens = 96
            };
            var settings = new AppSettings
            {
                Providers = [options],
                Languages = new LanguageOptions { Source = "en", Target = "zh-Hans" }
            };
            // Middle strip contains authored text, excluding the title and bottom subtitle overlay.
            var profile = new GameProfile(ProfileId.New(), "Owned fixture", "en", "zh-Hans", 8,
                [new CaptureRegion(Guid.NewGuid(), "Synthetic text", new NormalizedRegion(0.05, 0.25, 0.9, 0.4), 0)], false);
            var request = new SessionStartRequest(tracker.ConfirmedSelection!, profile, settings, options, new GlossaryCatalog([]));
            for (var cycle = 1; cycle <= cycles; cycle++)
            {
                text.Text = "Hello, world!";
                startToFirstVisibleMilliseconds = null;
                var startup = Stopwatch.StartNew();
                await controller.StartAsync(request, token);
                startupMilliseconds = startup.Elapsed.TotalMilliseconds;
                Require(controller.State == SessionState.Running, "Session start", controller);
                Require(sessionIds.Add(controller.Metrics.SessionId!.Value), "Session identity must be renewed", controller);
                overlay.Bind(controller.Metrics.SessionId!.Value, tracker.ConfirmedSelection!, settings.Overlay);
                foreach (var fixture in new[]
                {
                (Source: "Hello, world!", Terms: new[] { "你好", "世界" }),
                (Source: "The door is locked.", Terms: new[] { "门", "锁" })
            })
                {
                    text.Text = fixture.Source;
                    var watch = Stopwatch.StartNew();
                    await WaitUntilAsync(() => display.Current is { } result &&
                        fixture.Terms.All(term => result.TranslatedText.Contains(term, StringComparison.Ordinal)), controller, token);
                    overlay.Refresh();
                    await WaitUntilAsync(() => Application.Current.Windows.OfType<SubtitleWindow>().Any(window =>
                        window.IsVisible && window.DisplayText == display.Current?.TranslatedText), controller, token);
                    startToFirstVisibleMilliseconds ??= startup.Elapsed.TotalMilliseconds;
                    Require(display.Current!.FromCache == (cycle > 1), "Expected fresh miss or retained lifecycle cache", controller);
                    cases.Add(new
                    {
                        fixture = cases.Count + 1,
                        cycle,
                        category = cycle == 1 ? "local-uncached" : "cached-lifecycle",
                        passed = true,
                        elapsedMilliseconds = watch.ElapsedMilliseconds,
                        fromCache = display.Current!.FromCache
                    });
                    Console.WriteLine($"Owned fixture {cases.Count}: capture/OCR/translation/visible subtitle passed.");
                }

                await controller.PauseAsync(token);
                overlay.Refresh();
                Require(controller.State == SessionState.Paused && display.Current is null, "Pause clears text", controller);
                Require(!Application.Current.Windows.OfType<SubtitleWindow>().Any(window => window.IsVisible), "Pause hides overlay", controller);
                var resume = Stopwatch.StartNew();
                await controller.ResumeAsync(token);
                await WaitUntilAsync(() => display.Current is { FromCache: true }, controller, token);
                Require(display.Current!.TranslatedText.Contains('门') &&
                    display.Current.TranslatedText.Contains('锁'), "Cached fixture meaning", controller);
                overlay.Refresh();
                await WaitUntilAsync(() => Application.Current.Windows.OfType<SubtitleWindow>().Any(window =>
                    window.IsVisible && window.DisplayText == display.Current?.TranslatedText), controller, token);
                resumeToCachedVisibleMilliseconds = resume.Elapsed.TotalMilliseconds;
                var stop = Stopwatch.StartNew();
                overlay.Unbind();
                await controller.StopAsync(token);
                Require(controller.State == SessionState.Stopped && display.Current is null, "Stop clears text", controller);
                Require(!Application.Current.Windows.OfType<SubtitleWindow>().Any(), "Stop closes overlay", controller);
                stopMilliseconds = stop.Elapsed.TotalMilliseconds;
                Require(errors.Count == 0, "Overlay errors", controller);
                Require(stop.Elapsed < TimeSpan.FromSeconds(2), "Stop exceeds two seconds", controller);
                Require(controller.Metrics.Errors == 0, "Session pipeline errors", controller);
                sessions.Add(new
                {
                    cycle,
                    startupMilliseconds,
                    startToFirstVisibleMilliseconds,
                    resumeToCachedVisibleMilliseconds,
                    stopMilliseconds,
                    metrics = controller.Metrics
                });
                Console.WriteLine($"Pause/resume cache hit and stop passed; stopMs={stop.ElapsedMilliseconds}.");
            }
            Require(sessions.Count == cycles, "All lifecycle sessions must complete", controller);
            passed = true;
        }
        finally
        {
            overlay.Unbind();
            await controller.StopAsync(CancellationToken.None);
            target.Close();
            var report = new
            {
                passed,
                reportVersion = 3,
                requestedCycles = cycles,
                completedCycles = sessions.Count,
                sessions,
                scope = "Owned synthetic window + production pipeline, not full shell or long soak",
                timingScope = "Managed visible-window observation, 50ms polling; not physical presentation or median-budget acceptance",
                startupMilliseconds,
                startToFirstVisibleMilliseconds,
                resumeToCachedVisibleMilliseconds,
                stopMilliseconds,
                utc = DateTimeOffset.UtcNow,
                cases,
                metrics = controller.Metrics,
                overlayErrors = errors.Count,
                failureCode = controller.Failure?.Code.ToString()
            };
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(runRoot, "result.json"),
                JsonSerializer.Serialize(report, ReportOptions));
            Console.WriteLine("Sanitized evidence: " + runRoot);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, SessionController controller, CancellationToken token)
    {
        while (!predicate())
        {
            Require(controller.State == SessionState.Running, "Pipeline stopped before expected result", controller);
            await Task.Delay(50, token);
        }
    }

    private static void Require(bool condition, string check, SessionController controller)
    {
        if (!condition)
        {
            Console.Error.WriteLine($"Check failed: {check}; state={controller.State}; code={controller.Failure?.Code}");
            throw new InvalidOperationException("Owned pipeline assertion failed.");
        }
    }

    private sealed class CapturedErrors : IErrorReporter
    {
        public event EventHandler<string>? ErrorReported;
        public int Count { get; private set; }
        public void Report(Exception exception) { Count++; ErrorReported?.Invoke(this, "Overlay operation failed."); }
    }

    private sealed class DeniedConsent : IRemoteTextConsent
    {
        public ValueTask<bool> IsGrantedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class RejectDownloads : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This acceptance harness cannot download models.");
    }
}

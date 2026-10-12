using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Ocr;

internal static class RegionEditorTests
{
    public static async Task RunAsync()
    {
        await TestEditingValidationUndoAndPersistenceAsync().ConfigureAwait(false);
        await TestJsonDraftStoresOnlyNormalizedGeometryAsync().ConfigureAwait(false);
        await TestPreviewStopsOnNavigationAsync().ConfigureAwait(false);
    }

    private static async Task TestEditingValidationUndoAndPersistenceAsync()
    {
        var reporter = new TestErrorReporter();
        var draftStore = new MemoryDraftStore();
        var preview = new TestRegionPreview();
        using var windowSelection = new WindowSelectionViewModel(new EmptyWindowDiscovery(), reporter);
        using var editor = new RegionEditorViewModel(draftStore, preview, windowSelection, reporter);
        editor.SetPreviewViewport(560, 315);

        Assert(editor.Regions.Count == 1, "The editor must always begin with one region.");
        var original = editor.SelectedRegion!.Bounds;
        editor.MoveSelectedByPreviewDips(-10_000, -10_000);
        Assert(editor.SelectedRegion.Bounds.X == 0 && editor.SelectedRegion.Bounds.Y == 0,
            "Dragging outside the preview must clamp to content.");
        editor.ResizeSelectedByPreviewDips(-10_000, -10_000);
        Assert(editor.SelectedRegion.Bounds.Width >= 8d / 1280, "Resize must enforce minimum capture width.");
        Assert(editor.SelectedRegion.Bounds.Height >= 8d / 720, "Resize must enforce minimum capture height.");
        editor.UndoCommand.Execute(null);
        Assert(editor.SelectedRegion.Bounds.Width > 8d / 1280,
            "Undo should restore the geometry before resize.");
        editor.ResetCommand.Execute(null);
        Assert(editor.SelectedRegion.Bounds == original, "Reset should restore the saved baseline.");

        while (editor.AddCommand.CanExecute(null))
        {
            editor.AddCommand.Execute(null);
        }

        Assert(editor.Regions.Count == RegionEditorViewModel.MaximumRegionCount,
            "The editor must enforce the maximum region count.");
        var selected = editor.SelectedRegion!;
        editor.MoveUpCommand.Execute(null);
        Assert(selected.SortOrder == RegionEditorViewModel.MaximumRegionCount - 2,
            "Move-up should update multi-region ordering.");

        selected.Name = " ";
        Assert(!editor.CanSave && !editor.SaveCommand.CanExecute(null), "Invalid names must prevent saving.");
        selected.Name = "Secondary dialogue";
        Assert(editor.CanSave, "Valid normalized regions should be saveable.");
        await editor.SaveCommand.ExecuteAsync().ConfigureAwait(false);
        Assert(draftStore.Saved?.Regions.Count == RegionEditorViewModel.MaximumRegionCount,
            "Saving should persist every ordered region.");
        Assert(draftStore.Saved!.Regions.Select(region => region.SortOrder).SequenceEqual(Enumerable.Range(0, 8)),
            "Saved region order must be contiguous.");

        while (editor.DeleteCommand.CanExecute(null))
        {
            editor.DeleteCommand.Execute(null);
        }

        Assert(editor.Regions.Count == 1, "The editor must preserve its minimum region count.");
    }

    private static async Task TestJsonDraftStoresOnlyNormalizedGeometryAsync()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "GameLingoAI-region-tests",
            Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "draft.json");
        var store = new JsonRegionDraftStore(path);
        var region = new CaptureRegion(
            Guid.NewGuid(),
            "Dialogue",
            new NormalizedRegion(0.125, 0.7, 0.75, 0.2),
            0);
        try
        {
            await store.SaveAsync(
                new RegionDraft(ProfileId.New(), new[] { region }, DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(false);
            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            Assert(!json.Contains("pixel", StringComparison.OrdinalIgnoreCase), "Draft JSON must not contain pixels.");
            Assert(!json.Contains("screenshot", StringComparison.OrdinalIgnoreCase),
                "Draft JSON must not contain screenshots.");
            Assert(!json.Contains("image", StringComparison.OrdinalIgnoreCase), "Draft JSON must not contain images.");
            var loaded = await store.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            Assert(
                loaded?.Regions.Single().Bounds == region.Bounds,
                $"Normalized geometry must round-trip. Loaded: {loaded?.Regions.Single().Bounds}; expected: {region.Bounds}.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() =>
                store.SaveAsync(new RegionDraft(null, new[] { region }, DateTimeOffset.UtcNow), cancellation.Token))
                .ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    private static async Task TestPreviewStopsOnNavigationAsync()
    {
        var reporter = new TestErrorReporter();
        var preview = new TestRegionPreview();
        using var windowSelection = new WindowSelectionViewModel(new EmptyWindowDiscovery(), reporter);
        var window = new CaptureWindow(
            new WindowIdentity(new WindowId(42), 7, 11),
            "Synthetic",
            "Synthetic process",
            new PixelBounds(0, 0, 1280, 720),
            144,
            false);
        windowSelection.Windows.Add(window);
        windowSelection.SelectedWindow = window;
        windowSelection.ConfirmCommand.Execute(true);

        using var editor = new RegionEditorViewModel(new MemoryDraftStore(), preview, windowSelection, reporter);
        using var ocrModel = new OcrModelViewModel(
            new OcrModelProvisioner(new HttpClient(), Path.Combine(Path.GetTempPath(), "GameLingoAI-RegionEditorTests")),
            PaddleOcrModelCatalog.PpOcrV5Mobile,
            reporter);
        var shell = new ShellViewModel(new SessionStatusViewModel(), windowSelection, editor, ocrModel, reporter);
        await editor.StartPreviewCommand.ExecuteAsync().ConfigureAwait(false);
        Assert(preview.IsRunning && preview.StartCount == 1, "Confirmed targets should start an isolated preview.");

        shell.NavigateCommand.Execute(PageId.WindowRegion);
        shell.NavigateCommand.Execute(PageId.Diagnostics);
        Assert(!preview.IsRunning && preview.StopCount > 0, "Leaving the editor must stop and release preview work.");
        Assert(editor.PreviewImage is null, "Leaving the editor must release the displayed preview frame.");
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

    private sealed class MemoryDraftStore : IRegionDraftStore
    {
        public RegionDraft? Saved { get; private set; }

        public Task<RegionDraft?> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Saved);
        }

        public Task SaveAsync(RegionDraft draft, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved = draft;
            return Task.CompletedTask;
        }
    }

    private sealed class TestRegionPreview : ILocalRegionPreview
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

        public bool IsRunning { get; private set; }

        public int StartCount { get; private set; }

        public int StopCount { get; private set; }

        public Task WhenStopped => Task.CompletedTask;

        public Task StartAsync(
            ConfirmedCaptureWindow target,
            SessionId sessionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            StartCount++;
            return Task.CompletedTask;
        }

        public void RequestStop()
        {
            StopCount++;
            IsRunning = false;
        }

        public ValueTask DisposeAsync()
        {
            RequestStop();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestErrorReporter : IErrorReporter
    {
        public event EventHandler<string>? ErrorReported;

        public void Report(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            ErrorReported?.Invoke(this, "A safe error occurred.");
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
}

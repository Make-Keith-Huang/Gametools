using GameLingoAI.App.Overlay;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

internal static class OverlayTests
{
    public static async Task RunAsync()
    {
        var options = new OverlayOptions();
        Assert(OverlayLayout.Validate(options).Count == 0, "Overlay defaults must be valid.");
        Assert(OverlayLayout.Validate(options with { FontFamily = "https://example.invalid/font.ttf#Font" }).Count > 0,
            "Font references must not load files or network resources.");
        Assert(OverlayLayout.Validate(options with { FontSize = double.NaN, Foreground = "Red" }).Count == 2,
            "Nonfinite sizes and non-hex colors must be rejected.");
        Assert(OverlayLayout.Sanitize("<b>plain</b>\r\n日本語\u202e\0\tOK", options) == "<b>plain</b>\n日本語 OK",
            "Overlay text must remain literal, without bidi or control injection.");
        Assert(OverlayLayout.Sanitize("A😀B", options with { MaximumCharacters = 2 }) == "A",
            "Length limits must not split surrogate pairs.");
        Assert(OverlayLayout.Sanitize("one\ntwo\nthree", options with { MaximumLines = 2 }) == "one\ntwo",
            "Logical line limit failed.");
        foreach (var dpi in new uint[] { 96, 120, 144, 192 })
        {
            var client = new PixelBounds(-1920, -200, 1600, 1000);
            var bounds = OverlayLayout.Place(client, dpi, options);
            Assert(bounds.Width == 1440 && bounds.Left == -1840, "Physical horizontal anchor changed with DPI.");
            Assert(bounds.Top + bounds.Height == client.Top + client.Height - 24 * dpi / 96,
                "DIP vertical offset did not scale correctly.");
            var tiny = OverlayLayout.Place(new PixelBounds(10, 10, 8, 8), dpi,
                options with { HorizontalOffsetDips = 500, VerticalOffsetDips = 500 });
            Assert(tiny.Left >= 10 && tiny.Top >= 10 && tiny.Left + tiny.Width <= 18 && tiny.Top + tiny.Height <= 18,
                "Small/minimum target must contain the overlay.");
        }
        var state = new OverlayDisplayState();
        var first = SessionId.New();
        var second = SessionId.New();
        state.Bind(first);
        Assert(state.TryUpdate(Result(first, 5)), "Current session result rejected.");
        Assert(!state.TryUpdate(Result(first, 4)), "Old sequence accepted.");
        state.Hide();
        Assert(!state.TryUpdate(Result(first, 5)) && state.Current is null, "Hide must not permit replaying a stale result.");
        state.Bind(second);
        Assert(!state.TryUpdate(Result(first, 100)) && state.TryUpdate(Result(second, 0)), "Session isolation failed.");
        state.Unbind();
        Assert(!state.TryUpdate(Result(second, 1)), "Unbound display accepted updates.");
        await SettingsAsync();
    }

    private static async Task SettingsAsync()
    {
        var configuration = new Configuration();
        using var settings = new OverlaySettingsViewModel(configuration, new ErrorReporter());
        settings.FontFamily = "Arial";
        settings.FontSize = 36;
        settings.BackgroundOpacity = .4;
        await settings.SaveCommand.ExecuteAsync();
        Assert(configuration.Value.Overlay.FontSize == 36 && configuration.Value.Overlay.FontFamily == "Arial",
            "Overlay settings must persist valid edits.");
        Assert(configuration.Value.Cache.MaximumRows == 42, "Saving overlay settings overwrote unrelated configuration.");
        settings.FontSize = 0;
        await settings.SaveCommand.ExecuteAsync();
        Assert(configuration.Saves == 1, "Invalid overlay settings must not be persisted.");
        await settings.LoadCommand.ExecuteAsync();
        Assert(settings.FontSize == 36, "Saved overlay settings failed to load.");
        configuration.Fail = true;
        await settings.SaveCommand.ExecuteAsync();
        Assert(configuration.Saves == 1, "Failed configuration reads must not overwrite the file.");
        configuration.Fail = false;
        configuration.Block = true;
        var loading = settings.LoadCommand.ExecuteAsync();
        Assert(!settings.SaveCommand.CanExecute(null), "Overlay load/save commands must not overlap.");
        settings.LoadCommand.Cancel();
        await loading;
        Assert(!settings.LoadCommand.IsExecuting, "Cancelled overlay settings load did not settle.");
    }

    private sealed class Configuration : IConfigurationStore
    {
        public AppSettings Value = new() { Cache = new CacheOptions { MaximumRows = 42 } };
        public int Saves;
        public bool Fail;
        public bool Block;
        public async Task<OperationResult<AppSettings>> LoadAsync(CancellationToken token)
        {
            if (Block)
            { await Task.Delay(Timeout.Infinite, token); }
            return Fail ? OperationResult.Failed<AppSettings>(new PipelineFailure(FailureCode.PersistenceUnavailable,
                "Configuration unavailable", CorrelationId.New(), true)) : OperationResult.Success(Value);
        }
        public Task SaveAsync(AppSettings settings, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Value = settings;
            Saves++;
            return Task.CompletedTask;
        }
        public Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken token) => Task.FromResult("{}");
    }

    private static TranslationResult Result(SessionId id, long sequence) => new(
        new PipelineMetadata(id, new SequenceNumber(sequence), DateTimeOffset.UnixEpoch, CorrelationId.New()),
        "source", "translation", "fake", null, TimeSpan.Zero, false, false);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        { throw new InvalidOperationException(message); }
    }
}

using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;

internal static class HotkeysViewModelTests
{
    public static async Task RunAsync()
    {
        var configuration = new Configuration();
        var service = new Service();
        var errors = new Reporter();
        using var editor = new HotkeysViewModel(configuration, service, errors);
        Assert(service.Applies == 0 && !editor.Enabled && editor.Rows.Count == 5, "Editor activated shortcuts at construction.");
        var row = editor.Rows.Single(item => item.Action == HotkeyAction.EmergencyStop);
        row.Assigned = true;
        row.Key = "f8";
        editor.Enabled = true;
        await editor.LoadCommand.ExecuteAsync();
        Assert(row.Key == "f8" && configuration.Loads == 0, "Load silently discarded shortcut edits.");
        await editor.SaveCommand.ExecuteAsync();
        Assert(!editor.HasUnsavedChanges && configuration.Value.Hotkeys.Enabled &&
            configuration.Value.Hotkeys.Bindings.Single().Key == "F8" && configuration.Value.Capture.FramesPerSecond == 16 &&
            service.Applies == 0, "Save lost unrelated settings, failed normalization or automatically registered.");
        editor.ApplyCommand.Execute(null);
        Assert(service.Applies == 1 && service.Last!.Bindings.Single().Action == HotkeyAction.EmergencyStop, "Explicit Apply did not register selected definitions.");
        var saved = configuration.Value;
        row.Key = "F12";
        await editor.SaveCommand.ExecuteAsync();
        editor.ApplyCommand.Execute(null);
        Assert(configuration.Value == saved && service.Applies == 1 && editor.HasUnsavedChanges, "Invalid draft was saved or registered.");
        editor.DiscardConfirmed = true;
        await editor.LoadCommand.ExecuteAsync();
        Assert(row.Key == "F8" && !editor.HasUnsavedChanges && !editor.DiscardConfirmed && service.Applies == 1,
            "Explicit reload registered shortcuts or failed to consume discard confirmation.");
        service.Fail = true;
        editor.ApplyCommand.Execute(null);
        Assert(editor.Status.Contains("conflict", StringComparison.Ordinal), "Registration conflict was not actionable.");
        service.Fail = false;
        editor.DisableCommand.Execute(null);
        Assert(service.Clears == 1 && editor.Enabled && configuration.Value.Hotkeys.Enabled, "Disable changed saved definitions.");
        row.Key = "F9";
        configuration.FailSave = true;
        await editor.SaveCommand.ExecuteAsync();
        Assert(editor.HasUnsavedChanges && configuration.Value == saved && errors.Count == 0, "Save failure lost the draft or changed stored settings.");
        configuration.FailSave = false;
        configuration.DelaySave = true;
        var saving = editor.SaveCommand.ExecuteAsync();
        await configuration.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        row.Key = "F10";
        editor.Enabled = false;
        Assert(row.Key == "F9" && editor.Enabled && !editor.ApplyCommand.CanExecute(null) && !editor.LoadCommand.CanExecute(null),
            "In-flight persistence allowed edits or competing commands.");
        await editor.ShutdownAsync();
        await saving;
        Assert(configuration.Canceled && service.Clears == 2 && editor.HasUnsavedChanges && !editor.CanEdit &&
            !editor.DisableCommand.CanExecute(null) && errors.Count == 0, "Shutdown did not release shortcuts and cancel/drain save.");
        Console.WriteLine("Hotkey settings UI: explicit load/save/apply, dirty guard, conflict, invalid/save failure preservation and shutdown drain passed.");
    }

    private sealed class Configuration : IConfigurationStore
    {
        public AppSettings Value = new() { Capture = new CaptureOptions { FramesPerSecond = 16 } };
        public int Loads;
        public bool FailSave, DelaySave, Canceled;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<OperationResult<AppSettings>> LoadAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Loads++; return Task.FromResult(OperationResult.Success(Value)); }
        public async Task SaveAsync(AppSettings value, CancellationToken token)
        {
            if (FailSave)
            { throw new IOException("Synthetic access failure"); }
            if (DelaySave)
            {
                Entered.TrySetResult();
                try
                { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Canceled = true; throw; }
            }
            Value = value;
        }
        public Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Service : IHotkeyService
    {
        public int Applies, Clears;
        public bool Fail;
        public HotkeyOptions? Last;
        public event EventHandler<HotkeyAction>? Triggered { add { } remove { } }
        public HotkeyApplyResult Apply(HotkeyOptions options)
        { Applies++; Last = options; return new(!Fail, Fail ? "Shortcut conflict; choose another combination." : "Applied."); }
        public HotkeyApplyResult Clear() { Clears++; return new(true, "Disabled."); }
        public void Dispose() { }
    }

    private sealed class Reporter : IErrorReporter
    {
        public int Count;
        public event EventHandler<string>? ErrorReported { add { } remove { } }
        public void Report(Exception error) => Count++;
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

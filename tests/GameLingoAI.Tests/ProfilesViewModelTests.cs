using System.Text.Json.Nodes;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;

internal static class ProfilesViewModelTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-ProfilesUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new SqliteDatabase(Path.Combine(directory, "test.db"));
            var repository = new SqliteProfileRepository(database, new Clock());
            var errors = new Reporter();
            using var editor = new ProfilesViewModel(repository, errors);
            editor.NewCommand.Execute(null);
            var id = editor.ReadDraft().Id;
            Assert(editor.HasUnsavedChanges, "New profile is not a draft.");
            editor.NewCommand.Execute(null);
            Assert(editor.ReadDraft().Id == id, "New silently discarded dirty draft.");
            editor.SecretReference = "fixture-credential-reference";
            await editor.SaveCommand.ExecuteAsync();
            Assert(!editor.HasUnsavedChanges && (await repository.GetAsync(id, CancellationToken.None))?.Settings.SecretReference == editor.SecretReference,
                "Save lost credential reference or dirty state.");
            editor.ExportCommand.Execute(null);
            var exported = editor.TransferJson;
            Assert(!exported.Contains("fixture-credential-reference", StringComparison.Ordinal), "UI export leaked credential reference.");
            editor.DuplicateCommand.Execute(null);
            var duplicate = editor.ReadDraft();
            Assert(duplicate.Id != id && editor.HasUnsavedChanges && editor.Profiles.Count == 1, "Duplicate was not a new unsaved identity.");
            await editor.SaveCommand.ExecuteAsync();
            editor.SelectedProfile = editor.Profiles.Single(profile => profile.Id == id);
            await editor.LoadCommand.ExecuteAsync();
            Assert(editor.ReadDraft().Id == id && !editor.HasUnsavedChanges, "Explicit load failed.");
            var good = editor.DraftJson;
            editor.DraftJson = "invalid";
            await editor.SaveCommand.ExecuteAsync();
            Assert(editor.HasUnsavedChanges && (await repository.GetAsync(id, CancellationToken.None))?.Name == "New profile",
                "Invalid draft overwrote saved profile.");
            await editor.LoadCommand.ExecuteAsync();
            Assert(editor.DraftJson == "invalid", "Load discarded dirty draft without confirmation.");
            editor.DiscardConfirmed = true;
            await editor.LoadCommand.ExecuteAsync();
            Assert(editor.DraftJson == good && !editor.DiscardConfirmed, "Confirmed reload failed.");
            editor.TransferJson = exported;
            editor.ImportCommand.Execute(null);
            var imported = editor.ReadDraft();
            Assert(imported.Id != id && imported.Id != duplicate.Id && imported.Settings.SecretReference is null &&
                (await repository.ListAsync(CancellationToken.None)).Count == 2, "Import reused identity/credentials or silently saved.");
            await editor.SaveCommand.ExecuteAsync();
            Assert((await repository.ListAsync(CancellationToken.None)).Count == 3, "Imported draft did not save.");
            editor.TransferJson = "{\"schemaVersion\":999}";
            editor.ImportCommand.Execute(null);
            Assert(editor.IsReadOnly && !editor.CanEdit, "Future schema did not become read-only.");
            await editor.SaveCommand.ExecuteAsync();
            Assert((await repository.ListAsync(CancellationToken.None)).Count == 3, "Future schema modified saved data.");
            editor.SelectedProfile = editor.Profiles.Single(profile => profile.Id == id);
            await editor.LoadCommand.ExecuteAsync();
            var bad = JsonNode.Parse(editor.DraftJson)!.AsObject();
            bad["capture"]!["regions"]![0]!["id"] = duplicate.Regions[0].Id.ToString();
            bad["name"] = "must roll back";
            editor.DraftJson = bad.ToJsonString();
            await editor.SaveCommand.ExecuteAsync();
            Assert(editor.HasUnsavedChanges && (await repository.GetAsync(id, CancellationToken.None))?.Name == "New profile",
                "SQLite region conflict failed to preserve last-known-good profile.");
            await editor.DeleteCommand.ExecuteAsync();
            Assert(await repository.GetAsync(id, CancellationToken.None) is not null, "Delete did not require confirmation.");
            editor.DeleteConfirmed = true;
            await editor.DeleteCommand.ExecuteAsync();
            Assert(await repository.GetAsync(id, CancellationToken.None) is not null, "Delete discarded unsaved draft without confirmation.");
            editor.DiscardConfirmed = true;
            await editor.DeleteCommand.ExecuteAsync();
            Assert(await repository.GetAsync(id, CancellationToken.None) is null && editor.DraftJson.Length == 0 && !editor.HasUnsavedChanges,
                "Confirmed deletion failed.");
            await editor.ShutdownAsync();
            Assert(!editor.NewCommand.CanExecute(null) && !editor.SaveCommand.CanExecute(null) && errors.Count == 0,
                "Shutdown left editable commands or expected failures leaked to global error reporter.");
            var delayed = new DelayedRepository(repository);
            using var busyEditor = new ProfilesViewModel(delayed, errors);
            busyEditor.NewCommand.Execute(null);
            var pendingDraft = busyEditor.DraftJson;
            var saving = busyEditor.SaveCommand.ExecuteAsync();
            await delayed.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            busyEditor.DraftJson = "must not replace an in-flight draft";
            Assert(!busyEditor.CanEdit && !busyEditor.DeleteCommand.CanExecute(null) && busyEditor.DraftJson == pendingDraft,
                "Edits or competing commands were admitted during save.");
            await busyEditor.ShutdownAsync();
            await saving;
            Assert(delayed.Canceled && busyEditor.HasUnsavedChanges && errors.Count == 0,
                "Shutdown did not cancel/drain save while preserving the unsaved draft.");
            using var formEditor = new ProfilesViewModel(repository, errors);
            formEditor.NewCommand.Execute(null);
            ProfileFormField Field(string path) => formEditor.Form!.Groups.SelectMany(group => group.Fields).Single(field => field.Path == path);
            Field("name").Text = "Form edited";
            Field("windowMatch.processHint").Text = "example";
            Field("capture.fps").Text = "12";
            Field("privacy.historyEnabled").BooleanValue = true;
            Field("overlay.fontSize").Text = "36";
            Field("ocr.preprocessing.threshold").Text = "140";
            var formProfile = formEditor.ReadDraft();
            Assert(formProfile.Name == "Form edited" && formProfile.CaptureFramesPerSecond == 12 && formProfile.HistoryEnabled &&
                formProfile.Settings.Overlay.FontSize == 36 && formProfile.Preprocessing.Threshold == 140 &&
                formProfile.Settings.ProcessHint == "example", "Form did not update the validated draft.");
            Field("capture.fps").Text = "-";
            Assert(formEditor.Form!.HasErrors && Field("capture.fps").Text == "-", "Incomplete numeric input was not retained as an error.");
            await formEditor.SaveCommand.ExecuteAsync();
            Assert(await repository.GetAsync(formProfile.Id, CancellationToken.None) is null, "Save used a stale valid number while input was invalid.");
            var beforeAdvanced = formEditor.DraftJson;
            formEditor.DraftJson = "{}";
            Assert(formEditor.DraftJson == beforeAdvanced, "Advanced editor discarded invalid form input silently.");
            Field("capture.fps").Text = "15";
            Field("ocr.preprocessing.threshold").Text = string.Empty;
            Assert(!formEditor.Form.HasErrors && formEditor.ReadDraft().Preprocessing.Threshold is null, "Optional threshold could not be cleared.");
            formEditor.AddRegionCommand.Execute(null);
            Assert(formEditor.ReadDraft().Regions.Count == 2, "Form did not add a region.");
            formEditor.SelectedRegionNumber = 2;
            formEditor.RemoveRegionCommand.Execute(null);
            Assert(formEditor.ReadDraft().Regions.Count == 1, "Selected region was not removed.");
            await formEditor.SaveCommand.ExecuteAsync();
            Assert((await repository.GetAsync(formProfile.Id, CancellationToken.None))?.CaptureFramesPerSecond == 15,
                "Corrected form did not persist.");
            formEditor.DraftJson = formEditor.DraftJson.Replace("Form edited", "Advanced edited", StringComparison.Ordinal);
            Assert(formEditor.Form is null, "Advanced editing left stale editable form controls.");
            formEditor.LoadFormCommand.Execute(null);
            Assert(Field("name").Text == "Advanced edited", "Form did not reload advanced draft changes.");
            formEditor.OutlinedPresetCommand.Execute(null);
            Assert(formEditor.ReadDraft().Preprocessing == PreprocessingOptions.OutlinedSubtitle, "Preset changed its name without applying all parameters.");
            Field("ocr.preprocessing.threshold").Text = string.Empty;
            Field("windowMatch.processHint").Text = string.Empty;
            Assert(formEditor.ReadDraft().Preprocessing.Threshold is null &&
                formEditor.ReadDraft().Preprocessing.Preset == PreprocessingPreset.Custom && formEditor.ReadDraft().Settings.ProcessHint is null,
                "Loaded non-null optional fields could not be cleared or custom preprocessing edits were mislabeled.");
            formEditor.NoPreprocessingCommand.Execute(null);
            Assert(formEditor.ReadDraft().Preprocessing == PreprocessingOptions.NoOp, "No-op preset was not applied.");
            formEditor.PlainPresetCommand.Execute(null);
            Assert(formEditor.ReadDraft().Preprocessing == PreprocessingOptions.PlainSubtitle, "Plain preset was not applied.");
            Console.WriteLine("Profile editor: drafts, explicit save/load/import/export, new IDs, secret exclusion, future read-only, SQLite rollback and confirmed deletion passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
    private sealed class DelayedRepository(IProfileRepository inner) : IProfileRepository
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Canceled { get; private set; }
        public Task<GameProfile?> GetAsync(ProfileId id, CancellationToken token) => inner.GetAsync(id, token);
        public Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken token) => inner.ListAsync(token);
        public Task DeleteAsync(ProfileId id, CancellationToken token) => inner.DeleteAsync(id, token);
        public async Task SaveAsync(GameProfile profile, CancellationToken token)
        {
            Entered.TrySetResult();
            try
            { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Canceled = true; throw; }
        }
    }
    private sealed class Reporter : IErrorReporter
    {
        public int Count { get; private set; }
        public event EventHandler<string>? ErrorReported;
        public void Report(Exception exception) { Count++; ErrorReported?.Invoke(this, "Unexpected error"); }
    }
}

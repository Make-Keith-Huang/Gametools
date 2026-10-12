using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using GameLingoAI.Infrastructure.Persistence;

internal static class GlossaryPersistenceUiTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-GlossaryUi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new SqliteDatabase(Path.Combine(directory, "test.db"));
            var repository = new SqliteGlossaryRepository(database, new Clock());
            using var editor = new GlossaryViewModel(repository);
            await editor.LoadCommand.ExecuteAsync();
            editor.SourceTerm = "勇者";
            editor.TargetTerm = "hero";
            editor.AddCommand.Execute(null);
            Assert(editor.HasUnsavedChanges, "Draft did not become dirty.");
            await editor.LoadCommand.ExecuteAsync();
            Assert(editor.Entries.Count == 1 && editor.HasUnsavedChanges, "Load silently discarded draft.");
            await editor.SaveCommand.ExecuteAsync();
            Assert(!editor.HasUnsavedChanges && (await repository.ListAsync(CancellationToken.None)).Count == 1, "Draft did not persist.");
            var saved = editor.Snapshot.Entries;
            using var other = new GlossaryViewModel(repository);
            await other.LoadCommand.ExecuteAsync();
            Assert(other.Entries.Single() == editor.Entries.Single(), "Reload lost terms.");
            editor.SourceTerm = "dragon";
            editor.TargetTerm = "龍";
            editor.AddCommand.Execute(null);
            await editor.SaveCommand.ExecuteAsync();
            other.SourceTerm = "stale";
            other.TargetTerm = "old";
            other.AddCommand.Execute(null);
            await other.SaveCommand.ExecuteAsync();
            Assert(other.HasUnsavedChanges && other.Status.Contains("changed", StringComparison.Ordinal), "Concurrent edit conflict was not reported.");
            Assert((await repository.ListAsync(CancellationToken.None)).Any(entry => entry.SourceTerm == "dragon"), "Concurrent save lost another editor's term.");

            // A valid global row followed by a missing-profile row must roll back the delete
            // and first insertion in the replace transaction.
            var before = await repository.ListAsync(CancellationToken.None);
            var invalid = new GlossaryCatalog(new[]
            {
                new GlossaryEntry(Guid.NewGuid(), null, "en", "ja", "first", "一"),
                new GlossaryEntry(Guid.NewGuid(), ProfileId.New(), "en", "ja", "missing profile", "二")
            });
            try
            {
                await repository.ReplaceAsync(invalid.Entries, before, CancellationToken.None);
                throw new InvalidOperationException("Invalid replacement succeeded.");
            }
            catch (DatabaseStorageException) { }
            Assert(before.OrderBy(entry => entry.Id).SequenceEqual((await repository.ListAsync(CancellationToken.None)).OrderBy(entry => entry.Id)),
                "Failed replacement changed saved glossary.");
            other.DiscardConfirmed = true;
            await other.LoadCommand.ExecuteAsync();
            Assert(!other.HasUnsavedChanges && !other.DiscardConfirmed && other.Entries.Count == 2, "Confirmed reload failed.");
            other.JsonText = new GlossaryCatalog(saved).ExportJson();
            other.ImportCommand.Execute(null);
            Assert(other.HasUnsavedChanges && (await repository.ListAsync(CancellationToken.None)).Count == 2, "Import saved without an explicit save action.");
            await other.SaveCommand.ExecuteAsync();
            Assert((await repository.ListAsync(CancellationToken.None)).Count == 1, "Imported draft did not save atomically.");
            Console.WriteLine("Glossary SQLite UI: load/save, dirty-draft guard, optimistic conflict, import and replacement rollback passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}

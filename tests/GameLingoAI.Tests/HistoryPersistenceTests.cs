using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;

internal static class HistoryPersistenceTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-HistoryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new SqliteDatabase(Path.Combine(directory, "history.db"));
            var clock = new Clock();
            var history = new SqliteHistoryRepository(database, clock);
            var entry = new TranslationHistoryEntry(SessionId.New(), null, "Unicode 日本語 '); DROP TABLE translation_history; --",
                "中文译文", clock.UtcNow, 123);
            Assert(!await history.AppendAsync(entry, CancellationToken.None), "History must default off.");
            Assert((await history.ListAsync(100, true, CancellationToken.None)).Count == 0, "Disabled history stored text.");
            await history.ConfigureAsync(new HistoryPolicy(true, pruneBatchSize: 2), CancellationToken.None);
            Assert(await history.AppendAsync(entry, CancellationToken.None), "Enabled history did not save.");
            Assert((await history.ListAsync(100, true, CancellationToken.None)).Single() == entry, "History round-trip failed.");
            try
            {
                await history.ListAsync(100, false, CancellationToken.None);
                throw new InvalidOperationException("Unrequested history access succeeded.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("explicit user action", StringComparison.Ordinal)) { }
            try
            {
                await history.AppendAsync(entry, new CancellationToken(true));
                throw new InvalidOperationException("Canceled history append succeeded.");
            }
            catch (OperationCanceledException) { }
            await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => history.AppendAsync(entry, CancellationToken.None)));
            Assert((await history.ListAsync(3, true, CancellationToken.None)).Count == 3, "History list did not respect its bound.");
            clock.UtcNow = clock.UtcNow.AddDays(31);
            Assert((await history.ListAsync(100, true, CancellationToken.None)).Count == 0, "Expired text was exposed before pruning.");
            Assert(!await history.AppendAsync(entry, CancellationToken.None), "Expired entry was admitted.");
            Assert(await history.PruneAsync(CancellationToken.None) == 2, "Pruning was not bounded.");
            var removed = 2;
            int count;
            while ((count = await history.PruneAsync(CancellationToken.None)) != 0)
            { Assert(count <= 2, "Prune batch exceeded policy."); removed += count; }
            Assert(removed == 11, "History retention lost track of rows.");
            await history.ConfigureAsync(new HistoryPolicy(), CancellationToken.None);
            var current = new TranslationHistoryEntry(SessionId.New(), null, "current", "translation", clock.UtcNow, 0);
            Assert(!await history.AppendAsync(current, CancellationToken.None), "History disable did not stop writes.");
            Console.WriteLine("SQLite history: default-off, explicit text access, round-trip, cancellation, bounded retention and parallel writes passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 25, 0, 0, 0, TimeSpan.Zero); }
}

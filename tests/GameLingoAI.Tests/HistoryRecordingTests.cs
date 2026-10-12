using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;

internal static class HistoryRecordingTests
{
    public static async Task RunAsync()
    {
        await TestPersistedProfileAssociationAsync();
        var repository = new Repository();
        var display = new SessionDisplay();
        await using var presenter = new HistoryRecordingPresenter(display, repository);
        var session = SessionId.New();
        await presenter.BeginAsync(false, null, CancellationToken.None);
        await presenter.ShowAsync(Result(session, 1), CancellationToken.None);
        Assert(repository.Configured == 0 && repository.Calls == 0, "Disabled history accessed persistence.");
        await presenter.BeginAsync(true, null, CancellationToken.None);
        await presenter.ShowAsync(Result(session, 2), CancellationToken.None);
        await repository.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var sequence = 3; sequence < 103; sequence++)
        { await presenter.ShowAsync(Result(session, sequence), CancellationToken.None); }
        Assert(display.Current?.Sequence == 102 && presenter.Dropped >= 68, "Slow history blocked presentation or queue was not bounded.");
        await Task.WhenAll(presenter.StopAsync(), presenter.StopAsync());
        Assert(repository.Calls == 1, "Stopped recorder continued queued writes.");
        var priorCalls = repository.Calls;
        await presenter.ShowAsync(Result(session, 104), CancellationToken.None);
        Assert(repository.Calls == priorCalls, "Stopped recorder accepted new history.");

        var failing = new Repository { Fail = true };
        await using var failingPresenter = new HistoryRecordingPresenter(display, failing);
        await failingPresenter.BeginAsync(true, null, CancellationToken.None);
        await failingPresenter.ShowAsync(Result(session, 1), CancellationToken.None);
        await failing.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await failingPresenter.StopAsync();
        Assert(failingPresenter.Errors == 1 && display.Current?.Sequence == 1, "Storage failure escaped or hid the subtitle.");

        var successful = new Repository { Complete = true };
        await using var valid = new HistoryRecordingPresenter(display, successful);
        var profile = ProfileId.New();
        await valid.BeginAsync(true, profile, CancellationToken.None);
        await valid.ShowAsync(Result(session, 1), CancellationToken.None);
        await successful.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await valid.ShowAsync(Result(session, 1), CancellationToken.None);
        await valid.ShowAsync(Result(SessionId.New(), 99), CancellationToken.None);
        await valid.StopAsync();
        Assert(successful.Calls == 1 && successful.Last?.ProfileId == profile && successful.Last.SessionId == session,
            "History session/sequence guard or profile association failed.");
        Console.WriteLine("History presenter: opt-in, nonblocking bounded queue, cancellation/drain, storage failure and duplicate/session guards passed.");
    }

    private static async Task TestPersistedProfileAssociationAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-HistoryProfile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var database = new SqliteDatabase(Path.Combine(directory, "profile-history.db"));
            var clock = new FixedClock();
            var profiles = new SqliteProfileRepository(database, clock);
            var history = new SqliteHistoryRepository(database, clock);
            var profile = new GameProfile(ProfileId.New(), "History association fixture", "ja", "zh-Hans", 8,
                new[] { new CaptureRegion(Guid.NewGuid(), "Dialogue", new NormalizedRegion(.1, .7, .8, .2), 0) }, true);
            await profiles.SaveAsync(profile, CancellationToken.None);
            var display = new SessionDisplay();
            await using var presenter = new HistoryRecordingPresenter(display, history);
            var session = SessionId.New();
            await presenter.BeginAsync(true, profile.Id, CancellationToken.None);
            await presenter.ShowAsync(Result(session, 1), CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            IReadOnlyList<TranslationHistoryEntry> entries;
            do
            {
                entries = await history.ListAsync(10, true, timeout.Token);
                if (entries.Count == 0)
                { await Task.Delay(10, timeout.Token); }
            } while (entries.Count == 0);
            await presenter.StopAsync();
            Assert(entries.Single().ProfileId == profile.Id && entries.Single().SessionId == session &&
                display.Current?.Sequence == 1 && presenter.Errors == 0,
                "Displayed result did not persist with its saved profile foreign key.");
            await profiles.DeleteAsync(profile.Id, CancellationToken.None);
            var retained = (await history.ListAsync(10, true, CancellationToken.None)).Single();
            Assert(retained.ProfileId is null && retained.SessionId == session && retained.TranslatedText == "translated",
                "Deleting a profile must preserve presented history and clear only its profile association.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class FixedClock : IClock
    { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }

    private static TranslationResult Result(SessionId session, long sequence) => new(
        new PipelineMetadata(session, new SequenceNumber(sequence), DateTimeOffset.UnixEpoch, CorrelationId.New()),
        "source", "translated", "fake", null, TimeSpan.FromMilliseconds(12), false, true);
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
    private sealed class Repository : ITranslationHistoryRepository
    {
        public int Configured, Calls;
        public bool Fail, Complete;
        public TranslationHistoryEntry? Last;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ConfigureAsync(HistoryPolicy policy, CancellationToken cancellationToken) { Configured++; return Task.CompletedTask; }
        public async Task<bool> AppendAsync(TranslationHistoryEntry entry, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Last = entry;
            Entered.TrySetResult();
            if (Fail)
            { throw new DatabaseStorageException(DatabaseFailureKind.DiskFull); }
            if (!Complete)
            { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            return true;
        }
        public Task<int> PruneAsync(CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<IReadOnlyList<TranslationHistoryEntry>> ListAsync(int maximumEntries, bool userRequested, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationHistoryEntry>>(Array.Empty<TranslationHistoryEntry>());
    }
}

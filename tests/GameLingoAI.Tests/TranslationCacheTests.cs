using System.Diagnostics;
using System.Text.Json;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using GameLingoAI.Infrastructure.Cache;

internal static class TranslationCacheTests
{
    private static readonly TranslationCacheIdentity Identity = new("fake", "model-a", "cfg-v1");

    public static async Task RunAsync()
    {
        TestCanonicalKeys();
        await TestRetentionAndBatchesAsync().ConfigureAwait(false);
        await TestCoalescingAndMetadataAsync().ConfigureAwait(false);
        await TestAdmissionAndCancellationAsync().ConfigureAwait(false);
        await TestClearDuringMissAsync().ConfigureAwait(false);
        await TestFailuresAndDisabledCacheAsync().ConfigureAwait(false);
        await TestAdministrationAsync().ConfigureAwait(false);
        await TestConcurrentRepositoryAsync().ConfigureAwait(false);
    }

    private static void TestCanonicalKeys()
    {
        var baseline = TranslationCacheKeyBuilder.Build(Request("Hero"), Identity);
        Assert(baseline.Value == "8a561be531c6d05a91579a98d5a5c0dd9f3f971faaad5b07549ef9ac15e490b2",
            "Cache key must match the independently computed canonical SHA-256 golden vector.");
        Assert(TranslationCacheKeyBuilder.Build(Request("Cafe\u0301\r\nHero", source: "JA", target: "EN"), Identity).Value ==
            TranslationCacheKeyBuilder.Build(Request("Café\nHero"), Identity).Value,
            "Canonical Unicode, newline representation, and language-code case should hit the same key.");
        var changes = new[]
        {
            Request("hero"), Request(" Hero"), Request("Hero\n"), Request("Hero", source: "en"),
            Request("Hero", target: "fr"), Request("Hero", context: ["different prior line"]),
            Request("Hero", options: new(false, true)), Request("Hero", options: new(true, false)),
            Request("Hero", glossary: [new("Hero", "勇者")]), Request("Hero", revision: new string('a', 64))
        };
        Assert(changes.All(request => TranslationCacheKeyBuilder.Build(request, Identity).Value != baseline.Value),
            "Every output-relevant request field must invalidate the key.");
        foreach (var identity in new[] { new TranslationCacheIdentity("other", "model-a", "cfg-v1"),
            new TranslationCacheIdentity("fake", "model-b", "cfg-v1"), new TranslationCacheIdentity("fake", "model-a", "cfg-v2") })
        {
            Assert(TranslationCacheKeyBuilder.Build(Request("Hero"), identity).Value != baseline.Value,
                "Provider, model and configuration revision must be part of the key.");
        }

        var options = new ProviderOptions { Id = "fake", Model = "model-a", BaseUri = new Uri("https://example.invalid/") };
        Assert(TranslationCacheIdentity.FromOptions(options).ConfigurationRevision !=
            TranslationCacheIdentity.FromOptions(options with { Temperature = 1 }).ConfigurationRevision,
            "Output-sensitive provider settings must change the configuration fingerprint.");
        var first = TranslationCacheKeyBuilder.Build(Request("Hero Dragon", glossary: [new("Hero", "勇者"), new("Dragon", "龍")]), Identity);
        var reversed = TranslationCacheKeyBuilder.Build(Request("Hero Dragon", glossary: [new("Dragon", "龍"), new("Hero", "勇者")]), Identity);
        Assert(first.Value == reversed.Value, "Equivalent glossary order must not produce different keys.");
        Assert(!AppSettingsValidator.Validate(new AppSettings
        {
            Cache = new CacheOptions
            {
                MaximumAgeDays = 0,
                PruneBatchSize = 0,
                MaximumInFlightRequests = 0,
                MaximumWaitersPerKey = 0
            }
        }).IsValid, "New cache settings must enforce age, pruning and admission bounds.");
    }

    private static async Task TestRetentionAndBatchesAsync()
    {
        var clock = new Clock();
        var policy = new TranslationCachePolicy(maximumRows: 2, maximumBytes: 20_000,
            maximumAge: TimeSpan.FromMinutes(10), pruneBatchSize: 1);
        var repo = new InMemoryTranslationCacheRepository(clock, policy);
        var a = Entry("a", clock.UtcNow);
        var b = Entry("b", clock.UtcNow);
        var c = Entry("c", clock.UtcNow);
        Assert(await repo.UpsertBatchAsync([a, b], 0, CancellationToken.None).ConfigureAwait(false) == 2,
            "Batch writes should admit valid entries atomically.");
        clock.UtcNow += TimeSpan.FromSeconds(1);
        var hit = await repo.FindAsync(a.Key.Value, CancellationToken.None).ConfigureAwait(false);
        Assert(hit.Entry?.HitCount == 1 && hit.Entry.LastUsedUtc == clock.UtcNow,
            "Lookup must atomically update hit metadata.");
        await repo.UpsertBatchAsync([c], 0, CancellationToken.None).ConfigureAwait(false);
        Assert((await repo.FindAsync(b.Key.Value, CancellationToken.None).ConfigureAwait(false)).Entry is null &&
            (await repo.FindAsync(a.Key.Value, CancellationToken.None).ConfigureAwait(false)).Entry is not null,
            "Capacity eviction must remove the least-recently-used entry.");
        var replacement = new TranslationCacheEntry(a.Key, "updated", clock.UtcNow, clock.UtcNow);
        await repo.UpsertBatchAsync([replacement], 0, CancellationToken.None).ConfigureAwait(false);
        Assert((await repo.FindAsync(a.Key.Value, CancellationToken.None).ConfigureAwait(false)).Entry?.TranslatedText == "updated",
            "Same-key writes must upsert without adding rows.");
        var byteRepo = new InMemoryTranslationCacheRepository(clock,
            new TranslationCachePolicy(10, a.SizeBytes + b.SizeBytes - 1));
        await byteRepo.UpsertBatchAsync([a, b], 0, CancellationToken.None).ConfigureAwait(false);
        var bytes = await byteRepo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false);
        Assert(bytes.Rows == 1 && bytes.Bytes <= a.SizeBytes + b.SizeBytes - 1,
            "Logical byte retention must hold independently of row count.");
        clock.UtcNow += TimeSpan.FromMinutes(11);
        Assert(await repo.PruneAsync(CancellationToken.None).ConfigureAwait(false) == 1 &&
            (await repo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false)).Rows == 1,
            "Maintenance must remove at most one configured batch.");
        Assert((await repo.FindAsync(a.Key.Value, CancellationToken.None).ConfigureAwait(false)).Entry is null,
            "Expired entries must never be served while incremental pruning is in progress.");
        await repo.PruneAsync(CancellationToken.None).ConfigureAwait(false);
        Assert((await repo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false)).Rows == 0,
            "Repeated bounded maintenance should remove every expired entry.");
        await repo.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        Assert(await repo.UpsertBatchAsync([Entry("late", clock.UtcNow)], 0, CancellationToken.None).ConfigureAwait(false) == 0,
            "A stale generation cannot repopulate cleared cache data.");
        await ThrowsAsync<OperationCanceledException>(() => repo.FindAsync(a.Key.Value, new CancellationToken(true))).ConfigureAwait(false);
        await ThrowsAsync<InvalidOperationException>(() => repo.ExportContentAsync(10, false, CancellationToken.None)).ConfigureAwait(false);
        await ThrowsAsync<ArgumentException>(() => repo.UpsertBatchAsync([a, null!], 1, CancellationToken.None)).ConfigureAwait(false);
        Assert((await repo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false)).Rows == 0,
            "Invalid batches must leave the repository unchanged.");
    }

    private static async Task TestCoalescingAndMetadataAsync()
    {
        var clock = new Clock();
        var repo = new InMemoryTranslationCacheRepository(clock);
        var started = Signal();
        var release = Signal();
        var provider = new Provider(async (request, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return Success(request);
        });
        await using var cache = new CachedTranslationProvider(provider, repo, clock, Identity);
        var requests = Enumerable.Range(0, 32).Select(_ => Request("same subtitle")).ToArray();
        var calls = requests.Select(request => cache.TranslateAsync(request, CancellationToken.None)).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert(provider.Calls == 1 && cache.ActiveRequestCount == 1 && cache.CoalescedRequests == 31,
            "Concurrent identical misses should have exactly one bounded provider call.");
        release.SetResult();
        var results = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        for (var index = 0; index < results.Length; index++)
        {
            Assert(results[index].IsSuccess && results[index].Value!.Metadata == requests[index].Metadata,
                "Each shared result must carry the caller's own session/sequence/correlation metadata.");
        }

        var next = Request("same subtitle");
        var cached = await cache.TranslateAsync(next, CancellationToken.None).ConfigureAwait(false);
        Assert(cached.Value is { FromCache: true } && cached.Value.Metadata == next.Metadata && provider.Calls == 1,
            "A later exact lookup must hit without replaying the old session metadata.");
        Assert(cache.ActiveRequestCount == 0, "Completed flight keys must be removed.");
    }

    private static async Task TestAdmissionAndCancellationAsync()
    {
        var clock = new Clock();
        var repo = new InMemoryTranslationCacheRepository(clock);
        var release = Signal();
        var started = Signal();
        var provider = new Provider(async (request, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return Success(request);
        });
        await using var cache = new CachedTranslationProvider(provider, repo, clock, Identity,
            new TranslationCachePolicy(maximumInFlight: 1, maximumWaitersPerKey: 2));
        using var cancelOne = new CancellationTokenSource();
        var cancelled = cache.TranslateAsync(Request("shared"), cancelOne.Token);
        var survivor = cache.TranslateAsync(Request("shared"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var fullKey = await cache.TranslateAsync(Request("other"), CancellationToken.None).ConfigureAwait(false);
        var fullWaiters = await cache.TranslateAsync(Request("shared"), CancellationToken.None).ConfigureAwait(false);
        Assert(fullKey.Failure?.Code == FailureCode.RateLimited && fullWaiters.Failure?.Code == FailureCode.RateLimited,
            "Distinct-key and same-key waiter admission must both remain bounded.");
        cancelOne.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => cancelled).ConfigureAwait(false);
        release.SetResult();
        Assert((await survivor.ConfigureAwait(false)).IsSuccess && provider.Calls == 1,
            "Cancelling one waiter must not cancel another waiter's shared provider call.");

        var allStarted = Signal();
        var neverRelease = Signal();
        var allProvider = new Provider(async (request, token) =>
        {
            allStarted.TrySetResult();
            await neverRelease.Task.WaitAsync(token).ConfigureAwait(false);
            return Success(request);
        });
        await using var allCache = new CachedTranslationProvider(allProvider, repo, clock, Identity);
        using var cancelAll = new CancellationTokenSource();
        var all = allCache.TranslateAsync(Request("abandoned"), cancelAll.Token);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        cancelAll.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => all).ConfigureAwait(false);
        await allCache.DisposeAsync().ConfigureAwait(false);
        Assert(allCache.ActiveRequestCount == 0 &&
            (await repo.FindAsync(TranslationCacheKeyBuilder.Build(Request("abandoned"), Identity).Value,
                CancellationToken.None).ConfigureAwait(false)).Entry is null,
            "The last cancelled waiter must cancel and drain its worker without caching a result.");
    }

    private static async Task TestClearDuringMissAsync()
    {
        var clock = new Clock();
        var repo = new InMemoryTranslationCacheRepository(clock);
        var started = Signal();
        var release = Signal();
        var provider = new Provider(async (request, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return Success(request);
        });
        await using var cache = new CachedTranslationProvider(provider, repo, clock, Identity);
        var pending = cache.TranslateAsync(Request("before clear"), CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await repo.ClearAsync(CancellationToken.None).ConfigureAwait(false);
        release.SetResult();
        Assert((await pending.ConfigureAwait(false)).IsSuccess &&
            (await repo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false)).Rows == 0,
            "Clearing during a miss must preserve translation availability but suppress its later cache write.");
    }

    private static async Task TestFailuresAndDisabledCacheAsync()
    {
        var clock = new Clock();
        foreach (var kind in new[] { CacheStorageFailureKind.Busy, CacheStorageFailureKind.Corrupt })
        {
            foreach (var failWrite in new[] { false, true })
            {
                var faulting = new FaultingRepository(new InMemoryTranslationCacheRepository(clock), kind, failWrite);
                var provider = new Provider((request, _) => Task.FromResult(Success(request)));
                await using var cache = new CachedTranslationProvider(provider, faulting, clock, Identity);
                Assert((await cache.TranslateAsync(Request("uncached fallback"), CancellationToken.None).ConfigureAwait(false)).IsSuccess &&
                    cache.PersistenceFailures == 1, "Busy/corrupt read and write failures must not fail translation.");
            }
        }

        var repo = new InMemoryTranslationCacheRepository(clock);
        foreach (var fail in new[] { true, false })
        {
            var provider = new Provider((request, _) => Task.FromResult(fail
                ? OperationResult.Failed<TranslationResult>(new PipelineFailure(FailureCode.ProviderUnavailable,
                    "Unavailable", request.Metadata.CorrelationId, true)) : Success(request, cacheable: false)));
            await using var cache = new CachedTranslationProvider(provider, repo, clock, Identity);
            await cache.TranslateAsync(Request("failure"), CancellationToken.None).ConfigureAwait(false);
            await cache.TranslateAsync(Request("failure"), CancellationToken.None).ConfigureAwait(false);
            Assert(provider.Calls == 2 && (await repo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false)).Rows == 0,
                "Failures and explicitly non-cacheable results must never be cached.");
        }

        var disabledProvider = new Provider((request, _) => Task.FromResult(Success(request)));
        await using var disabled = new CachedTranslationProvider(disabledProvider, repo, clock, Identity,
            new TranslationCachePolicy(maximumRows: 0));
        await disabled.TranslateAsync(Request("disabled"), CancellationToken.None).ConfigureAwait(false);
        await disabled.TranslateAsync(Request("disabled"), CancellationToken.None).ConfigureAwait(false);
        Assert(disabledProvider.Calls == 2, "Zero-row policy must bypass cache even when the repository is shared.");

        var wrongRow = new FaultingRepository(repo, CacheStorageFailureKind.Corrupt, true)
        {
            WrongEntry = Entry("different subtitle", clock.UtcNow)
        };
        var recoveredProvider = new Provider((request, _) => Task.FromResult(Success(request)));
        await using var recovered = new CachedTranslationProvider(recoveredProvider, wrongRow, clock, Identity);
        var recoveredResult = await recovered.TranslateAsync(Request("requested subtitle"), CancellationToken.None).ConfigureAwait(false);
        Assert(recoveredResult.IsSuccess && !recoveredResult.Value!.FromCache && recoveredProvider.Calls == 1,
            "A corrupt mismatched cache row must fall back to the provider instead of being displayed.");
    }

    private static async Task TestAdministrationAsync()
    {
        var clock = new Clock();
        var repo = new InMemoryTranslationCacheRepository(clock);
        await repo.UpsertBatchAsync([Entry("private subtitle fixture", clock.UtcNow)], 0, CancellationToken.None).ConfigureAwait(false);
        var configuration = new Configuration();
        var reporter = new Reporter();
        using var vm = new CacheViewModel(repo, configuration, reporter);
        await ((AsyncRelayCommand)vm.ExportStatisticsCommand).ExecuteAsync().ConfigureAwait(false);
        using var stats = JsonDocument.Parse(vm.ExportJson);
        Assert(stats.RootElement.GetProperty("Rows").GetInt32() == 1 &&
            !vm.ExportJson.Contains("private subtitle fixture", StringComparison.Ordinal),
            "Statistics export must not contain source or translated text.");
        await ((AsyncRelayCommand)vm.ExportContentCommand).ExecuteAsync().ConfigureAwait(false);
        Assert(vm.ExportJson.Contains("private subtitle fixture", StringComparison.Ordinal),
            "Explicit content export should expose bounded user-requested text.");
        vm.MaximumRows = 0;
        await ((AsyncRelayCommand)vm.ApplyCommand).ExecuteAsync().ConfigureAwait(false);
        Assert(configuration.Settings.Cache.MaximumRows == 0 && !configuration.Settings.Privacy.HistoryEnabled &&
            (await repo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false)).Rows == 0,
            "Retention settings must apply independently of history and support disabling storage.");
        await ((AsyncRelayCommand)vm.ClearCommand).ExecuteAsync().ConfigureAwait(false);
        Assert(vm.ExportJson.Length == 0 && reporter.Errors == 0, "Clear must also clear displayed text export without UI errors.");
    }

    private static async Task TestConcurrentRepositoryAsync()
    {
        var clock = new Clock();
        var repo = new InMemoryTranslationCacheRepository(clock, new TranslationCachePolicy(16, 20_000));
        await Task.WhenAll(Enumerable.Range(0, 200).Select(index => Task.Run(async () =>
        {
            var entry = Entry($"subtitle-{index % 24}", clock.UtcNow);
            await repo.UpsertBatchAsync([entry], 0, CancellationToken.None).ConfigureAwait(false);
            await repo.FindAsync(entry.Key.Value, CancellationToken.None).ConfigureAwait(false);
        }))).ConfigureAwait(false);
        var stats = await repo.GetStatisticsAsync(CancellationToken.None).ConfigureAwait(false);
        Assert(stats.Rows <= 16 && stats.Bytes <= 20_000 && stats.Bytes >= 0,
            "Parallel upserts/lookups must preserve row, byte and index consistency.");
        var retained = Entry("benchmark hit", clock.UtcNow);
        await repo.UpsertBatchAsync([retained], 0, CancellationToken.None).ConfigureAwait(false);
        var timings = new double[1_000];
        for (var index = 0; index < timings.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var hit = await repo.FindAsync(retained.Key.Value, CancellationToken.None).ConfigureAwait(false);
            timings[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert(hit.Entry is not null, "The lookup benchmark must measure hits, not missing keys.");
        }
        Array.Sort(timings);
        Assert(timings[949] < 5, "Cache-hit p95 must stay within the documented 5 ms lookup budget.");
        Console.WriteLine($"Cache lookup smoke: 1,000 hits, p50={timings[499]:F4} ms, p95={timings[949]:F4} ms.");
    }

    private static TranslationRequest Request(string text, string source = "ja", string target = "en",
        IReadOnlyList<string>? context = null, IReadOnlyList<GlossaryTerm>? glossary = null,
        TranslationPromptOptions? options = null, string? revision = null) => new(
        new PipelineMetadata(SessionId.New(), new SequenceNumber(1), DateTimeOffset.UnixEpoch, CorrelationId.New()),
        source, target, text, context, glossary, "model-a", options, revision);

    private static TranslationCacheEntry Entry(string text, DateTimeOffset now) =>
        new(TranslationCacheKeyBuilder.Build(Request(text), Identity), "translated fixture", now, now);

    private static OperationResult<TranslationResult> Success(TranslationRequest request, bool cacheable = true) =>
        OperationResult.Success(new TranslationResult(request.Metadata, request.Text, "translated fixture", "fake",
            request.Model, TimeSpan.Zero, false, cacheable));

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try
        { await action().ConfigureAwait(false); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    }

    private sealed class Provider(Func<TranslationRequest, CancellationToken, Task<OperationResult<TranslationResult>>> translate)
        : ITranslationProvider
    {
        private int _calls;
        public string Id => "fake";
        public int Calls => Volatile.Read(ref _calls);
        public Task<OperationResult<TranslationResult>> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return translate(request, cancellationToken);
        }
    }

    private sealed class FaultingRepository(ITranslationCacheRepository inner, CacheStorageFailureKind kind, bool failWrite)
        : ITranslationCacheRepository
    {
        public TranslationCacheEntry? WrongEntry { get; init; }
        public Task<TranslationCacheRead> FindAsync(string key, CancellationToken cancellationToken) => WrongEntry is not null
            ? Task.FromResult(new TranslationCacheRead(WrongEntry, 0))
            : failWrite ? inner.FindAsync(key, cancellationToken) : throw new CacheStorageException(kind);
        public Task<int> UpsertBatchAsync(IReadOnlyList<TranslationCacheEntry> entries, long expectedGeneration,
            CancellationToken cancellationToken) => throw new CacheStorageException(kind);
        public Task<int> PruneAsync(CancellationToken cancellationToken) => inner.PruneAsync(cancellationToken);
        public Task ConfigureAsync(TranslationCachePolicy policy, CancellationToken cancellationToken) => inner.ConfigureAsync(policy, cancellationToken);
        public Task ClearAsync(CancellationToken cancellationToken) => inner.ClearAsync(cancellationToken);
        public Task<TranslationCacheStatistics> GetStatisticsAsync(CancellationToken cancellationToken) => inner.GetStatisticsAsync(cancellationToken);
        public Task<IReadOnlyList<TranslationCacheEntry>> ExportContentAsync(int maximumEntries, bool userRequested,
            CancellationToken cancellationToken) => inner.ExportContentAsync(maximumEntries, userRequested, cancellationToken);
    }

    private sealed class Configuration : IConfigurationStore
    {
        public AppSettings Settings { get; private set; } = new();
        public Task<OperationResult<AppSettings>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(OperationResult.Success(Settings));
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) { Settings = settings; return Task.CompletedTask; }
        public Task<string> ExportSanitizedAsync(AppSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult("{}");
    }

    private sealed class Reporter : IErrorReporter
    {
        public event EventHandler<string>? ErrorReported;
        public int Errors { get; private set; }
        public void Report(Exception exception) { Errors++; ErrorReported?.Invoke(this, "Unexpected failure"); }
    }
}

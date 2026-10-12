using System.Diagnostics;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.Infrastructure.Cache;

/// <summary>Bounded same-key request sharing. The caller owns the underlying provider's lifetime.</summary>
public sealed class CachedTranslationProvider : ITranslationMemory, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ITranslationProvider _provider;
    private readonly ITranslationCacheRepository _repository;
    private readonly IClock _clock;
    private readonly TranslationCacheIdentity _identity;
    private readonly TranslationCachePolicy _policy;
    private readonly Dictionary<string, Flight> _flights = new(StringComparer.Ordinal);
    private bool _disposed;
    private long _persistenceFailures;
    private long _coalescedRequests;

    public CachedTranslationProvider(ITranslationProvider provider, ITranslationCacheRepository repository,
        IClock clock, TranslationCacheIdentity identity, TranslationCachePolicy? policy = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _policy = policy ?? new TranslationCachePolicy();
        if (provider.Id != identity.ProviderId)
        {
            throw new ArgumentException("Provider and cache identity must agree.", nameof(identity));
        }
    }

    public string Id => _provider.Id;
    public TranslationProviderCapabilities Capabilities => _provider.Capabilities;
    public long PersistenceFailures => Interlocked.Read(ref _persistenceFailures);
    public long CoalescedRequests => Interlocked.Read(ref _coalescedRequests);
    public int ActiveRequestCount { get { lock (_gate) { return _flights.Count; } } }

    public ValueTask<OperationResult<bool>> ValidateConfigurationAsync(CancellationToken cancellationToken) =>
        _provider.ValidateConfigurationAsync(cancellationToken);

    public ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        _provider.CheckHealthAsync(cancellationToken);

    public async Task<OperationResult<TranslationResult>> TranslateAsync(TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var configured = new TranslationRequest(request.Metadata, request.SourceLanguage, request.TargetLanguage,
            request.Text, request.Context, request.Glossary, _identity.Model, request.PromptOptions, request.GlossaryRevision);
        TranslationCacheKey key;
        try
        { key = TranslationCacheKeyBuilder.Build(configured, _identity); }
        catch (ArgumentException)
        {
            return Failed(FailureCode.Validation, "Translation cache input is invalid.", request);
        }

        Flight flight;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_flights.TryGetValue(key.Value, out var existing))
            {
                if (existing.Retiring || existing.Waiters >= _policy.MaximumWaitersPerKey)
                {
                    return Failed(FailureCode.RateLimited, "Translation request capacity is temporarily full.", request);
                }

                flight = existing;
                flight.Waiters++;
                Interlocked.Increment(ref _coalescedRequests);
            }
            else
            {
                if (_flights.Count >= _policy.MaximumInFlight)
                {
                    return Failed(FailureCode.RateLimited, "Translation request capacity is temporarily full.", request);
                }

                flight = new Flight(key, configured);
                _flights.Add(key.Value, flight);
                flight.Worker = RunFlightAsync(flight);
            }
        }

        try
        {
            var result = await flight.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Rebind(result, request);
        }
        finally
        {
            var cancel = false;
            lock (_gate)
            {
                flight.Waiters--;
                if (flight.Waiters == 0 && !flight.Completion.Task.IsCompleted)
                {
                    flight.Retiring = true;
                    cancel = true;
                }
            }

            if (cancel)
            {
                Cancel(flight);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Flight[] flights;
        lock (_gate)
        {
            _disposed = true;
            flights = _flights.Values.ToArray();
            foreach (var flight in flights)
            {
                flight.Retiring = true;
            }
        }

        foreach (var flight in flights)
        {
            Cancel(flight);
        }

        await Task.WhenAll(flights.Select(flight => flight.Worker)).ConfigureAwait(false);
    }

    private async Task RunFlightAsync(Flight flight)
    {
        // Publish the supervised worker handle under the gate before allowing completion or disposal.
        await Task.Yield();
        var token = flight.Cancellation.Token;
        OperationResult<TranslationResult> outcome = default;
        var cancelled = false;
        try
        {
            token.ThrowIfCancellationRequested();
            var watch = Stopwatch.StartNew();
            TranslationCacheRead? read = null;
            try
            {
                if (_policy.MaximumRows > 0)
                {
                    read = await _repository.FindAsync(flight.Key.Value, token).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                Interlocked.Increment(ref _persistenceFailures);
            }

            if (read?.Entry is { } entry && entry.Key == flight.Key &&
                _clock.UtcNow - entry.CreatedUtc < _policy.MaximumAge)
            {
                token.ThrowIfCancellationRequested();
                outcome = OperationResult.Success(new TranslationResult(flight.Request.Metadata,
                    flight.Request.Text, entry.TranslatedText, Id, _identity.Model, watch.Elapsed, true, true));
                return;
            }

            if (read?.Entry is not null)
            {
                Interlocked.Increment(ref _persistenceFailures);
            }

            var result = await _provider.TranslateAsync(flight.Request, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.IsSuccess && result.Value is { Cacheable: true, FromCache: false } translated &&
                read is not null && translated.ProviderId == Id && translated.Model == _identity.Model)
            {
                try
                {
                    var now = _clock.UtcNow;
                    var stored = new TranslationCacheEntry(flight.Key, translated.TranslatedText, now, now);
                    await _repository.UpsertBatchAsync([stored], read.Generation, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsStorageFailure(exception) || exception is ArgumentException)
                {
                    Interlocked.Increment(ref _persistenceFailures);
                }
            }

            token.ThrowIfCancellationRequested();
            outcome = result;
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception)
        {
            outcome = Failed(FailureCode.Unexpected,
                "Translation could not complete. Retry or check the provider.", flight.Request);
        }
        finally
        {
            lock (_gate)
            {
                _flights.Remove(flight.Key.Value);
                flight.Cancellation.Dispose();
                if (cancelled)
                {
                    flight.Completion.TrySetCanceled();
                }
                else
                {
                    flight.Completion.TrySetResult(outcome);
                }
            }
        }
    }

    private static void Cancel(Flight flight)
    {
        try
        { flight.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is CacheStorageException or IOException or UnauthorizedAccessException;

    private static OperationResult<TranslationResult> Rebind(OperationResult<TranslationResult> result,
        TranslationRequest request)
    {
        if (!result.IsSuccess)
        {
            var failure = result.Failure!;
            return OperationResult.Failed<TranslationResult>(new PipelineFailure(failure.Code, failure.Message,
                request.Metadata.CorrelationId, failure.IsTransient));
        }

        var value = result.Value!;
        return OperationResult.Success(new TranslationResult(request.Metadata, request.Text, value.TranslatedText,
            value.ProviderId, value.Model, value.Duration, value.FromCache, value.Cacheable, value.Usage));
    }

    private static OperationResult<TranslationResult> Failed(FailureCode code, string message, TranslationRequest request) =>
        OperationResult.Failed<TranslationResult>(new PipelineFailure(code, message, request.Metadata.CorrelationId,
            code is FailureCode.RateLimited or FailureCode.ProviderUnavailable));

    private sealed class Flight(TranslationCacheKey key, TranslationRequest request)
    {
        public TranslationCacheKey Key { get; } = key;
        public TranslationRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<OperationResult<TranslationResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Worker { get; set; } = Task.CompletedTask;
        public int Waiters { get; set; } = 1;
        public bool Retiring { get; set; }
    }
}

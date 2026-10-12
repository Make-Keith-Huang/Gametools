using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Translation;

/// <summary>Shared, bounded HTTP foundation for text-only translation adapters.</summary>
public sealed class HttpTranslationProvider : ITranslationProvider, IDisposable
{
    private readonly HttpClient _client;
    private readonly ITranslationHttpProtocol _protocol;
    private readonly IRemoteTextConsent _consent;
    private readonly IClock _clock;
    private readonly ITranslationDelay _delay;
    private readonly IRetryJitter _jitter;
    private readonly TranslationHttpPolicy _policy;
    private readonly SemaphoreSlim _concurrency;
    private readonly SemaphoreSlim _rateGate = new(1, 1);
    private DateTimeOffset _nextRequestAt;
    private int _disposed;
    private readonly TranslationCircuit _circuit;

    public HttpTranslationProvider(
        HttpClient client,
        ITranslationHttpProtocol protocol,
        IRemoteTextConsent consent,
        IClock clock,
        ITranslationDelay delay,
        IRetryJitter jitter,
        TranslationHttpPolicy policy)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        policy.Validate();
        _circuit = new TranslationCircuit(clock, policy);
        _concurrency = new SemaphoreSlim(policy.MaximumConcurrentRequests, policy.MaximumConcurrentRequests);
    }

    public string Id => _protocol.Id;

    public TranslationProviderCapabilities Capabilities => _protocol.Capabilities;

    public ValueTask<OperationResult<bool>> ValidateConfigurationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = _protocol.BaseUri;
        if (string.IsNullOrWhiteSpace(Id) ||
            !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) ||
            Capabilities.MaximumInputCharacters is < 1 or > 16_000)
        {
            return ValueTask.FromResult(Failure<bool>(FailureCode.Validation,
                "The translation provider endpoint or capability configuration is invalid.",
                CorrelationId.New(), false));
        }

        return ValueTask.FromResult(OperationResult.Success(true));
    }

    public async ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Capabilities.SupportsHealthCheck)
        {
            return new ProviderHealth(ProviderHealthStatus.Unknown);
        }

        using var request = _protocol.CreateHealthRequest();
        if (request is null)
        {
            return new ProviderHealth(ProviderHealthStatus.Unknown);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_policy.TimeoutMilliseconds);
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            return new ProviderHealth(response.IsSuccessStatusCode
                ? ProviderHealthStatus.Available
                : ProviderHealthStatus.Unavailable);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProviderHealth(ProviderHealthStatus.Unavailable);
        }
        catch (HttpRequestException)
        {
            return new ProviderHealth(ProviderHealthStatus.Unavailable);
        }
    }

    public async Task<OperationResult<TranslationResult>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var generation = _circuit.TryEnter();
        if (generation is null)
        {
            return Failure<TranslationResult>(FailureCode.ProviderUnavailable,
                "Translation is temporarily paused after repeated provider failures. Retry shortly.",
                request.Metadata.CorrelationId, true);
        }
        bool? transientFailure = null;
        var acquired = false;
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            var result = await TranslateCoreAsync(request, generation.Value, cancellationToken).ConfigureAwait(false);
            transientFailure = result.Failure?.IsTransient == true;
            return result;
        }
        finally
        {
            _circuit.Complete(generation.Value, transientFailure);
            if (acquired)
            { _concurrency.Release(); }
        }
    }

    private async Task<OperationResult<TranslationResult>> TranslateCoreAsync(
        TranslationRequest request,
        int generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var correlation = request.Metadata.CorrelationId;
        if (!await _consent.IsGrantedAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<TranslationResult>(FailureCode.ConsentRequired,
                "Enable remote text processing before sending translation requests.", correlation, false);
        }

        if (request.Text.Length > Capabilities.MaximumInputCharacters ||
            request.Context.Count > 8 || request.Context.Any(item => item is null || item.Length > 1_000) ||
            request.Glossary.Count > 100 ||
            request.Glossary.Any(item => item is null || string.IsNullOrWhiteSpace(item.Source) ||
                string.IsNullOrWhiteSpace(item.Target) || item.Source.Length > 100 || item.Target.Length > 100))
        {
            return Failure<TranslationResult>(FailureCode.Validation,
                "Translation text, context, or glossary exceeds provider limits.", correlation, false);
        }

        var validation = await ValidateConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return Failure<TranslationResult>(FailureCode.Validation,
                validation.Failure!.Message, correlation, false);
        }

        var stopwatch = Stopwatch.StartNew();
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WaitForRateSlotAsync(cancellationToken).ConfigureAwait(false);
            if (!_circuit.IsCurrent(generation))
            {
                return Failure<TranslationResult>(FailureCode.ProviderUnavailable,
                    "Translation is temporarily paused after repeated provider failures. Retry shortly.",
                    correlation, true);
            }
            HttpRequestMessage createdMessage;
            try
            {
                createdMessage = _protocol.CreateRequest(request);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            {
                return Failure<TranslationResult>(FailureCode.Validation,
                    "Translation prompt data is invalid. Check the source and glossary terms.", correlation, false);
            }

            using var message = createdMessage;
            if (message.RequestUri is null || !IsAllowedEndpoint(message.RequestUri))
            {
                return Failure<TranslationResult>(FailureCode.Validation,
                    "Translation request endpoint is outside the configured provider origin.", correlation, false);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_policy.TimeoutMilliseconds);
            try
            {
                using var response = await _client.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var classification = Classify(response.StatusCode);
                    if (classification.Transient && attempt < _policy.MaximumRetries)
                    {
                        await BackoffAsync(attempt, response.Headers.RetryAfter, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    return Failure<TranslationResult>(classification.Code, classification.Message,
                        correlation, classification.Transient);
                }

                if (response.Content.Headers.ContentLength > _policy.MaximumResponseBytes)
                {
                    return Failure<TranslationResult>(FailureCode.ResponseTooLarge,
                        "The provider response exceeded the configured size limit.", correlation, false);
                }

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !_protocol.AcceptedResponseMediaTypes.Contains(mediaType))
                {
                    return Failure<TranslationResult>(FailureCode.ResponseMalformed,
                        "The provider returned an unsupported response media type.", correlation, false);
                }

                var bytes = await ReadBoundedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                if (bytes is null)
                {
                    return Failure<TranslationResult>(FailureCode.ResponseTooLarge,
                        "The provider response exceeded the configured size limit.", correlation, false);
                }

                TranslationHttpPayload payload;
                try
                {
                    payload = _protocol.ParseResponse(bytes);
                }
                catch (Exception exception) when (exception is FormatException or System.Text.Json.JsonException)
                {
                    return Failure<TranslationResult>(FailureCode.ResponseMalformed,
                        "The provider returned an unreadable translation response.", correlation, false);
                }

                var text = PlainTextOutputSanitizer.Sanitize(
                    payload.Text,
                    _policy.MaximumOutputCharacters,
                    _policy.MaximumOutputLines);
                if (text.Length == 0 || payload.Usage is { IsValid: false })
                {
                    return Failure<TranslationResult>(FailureCode.ResponseMalformed,
                        "The provider returned no usable translation text.", correlation, false);
                }

                return OperationResult.Success(new TranslationResult(
                    request.Metadata, request.Text, text, Id, request.Model,
                    stopwatch.Elapsed, false, true, payload.Usage));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (attempt >= _policy.MaximumRetries)
                {
                    return Failure<TranslationResult>(FailureCode.Timeout,
                        "The translation request timed out.", correlation, true);
                }
            }
            catch (HttpRequestException)
            {
                if (attempt >= _policy.MaximumRetries)
                {
                    return Failure<TranslationResult>(FailureCode.ProviderUnavailable,
                        "The translation provider could not be reached.", correlation, true);
                }
            }

            await BackoffAsync(attempt, null, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _concurrency.Dispose();
            _rateGate.Dispose();
        }
    }

    private async Task WaitForRateSlotAsync(CancellationToken cancellationToken)
    {
        await _rateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _nextRequestAt - _clock.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await _delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            }

            _nextRequestAt = _clock.UtcNow.AddMilliseconds(_policy.MinimumRequestIntervalMilliseconds);
        }
        finally
        {
            _rateGate.Release();
        }
    }

    private async Task BackoffAsync(int attempt, RetryConditionHeaderValue? retryAfter, CancellationToken cancellationToken)
    {
        var seconds = retryAfter?.Delta?.TotalSeconds ??
            (retryAfter?.Date - _clock.UtcNow)?.TotalSeconds ??
            (Math.Pow(2, attempt) * (0.5 + _jitter.NextUnitInterval()));
        await _delay.DelayAsync(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 30)), cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsAllowedEndpoint(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, _protocol.BaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.Host, _protocol.BaseUri.Host, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == _protocol.BaseUri.Port;

    private async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return destination.ToArray();
            }

            if (destination.Length + read > _policy.MaximumResponseBytes)
            {
                return null;
            }

            destination.Write(buffer, 0, read);
        }
    }

    internal static (FailureCode Code, string Message, bool Transient) Classify(HttpStatusCode statusCode) =>
        statusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Conflict or
                HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnprocessableEntity =>
                (FailureCode.Validation, "The translation provider rejected the request.", false),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                (FailureCode.AuthenticationRejected, "The translation provider rejected the configured credentials.", false),
            HttpStatusCode.RequestTimeout =>
                (FailureCode.Timeout, "The translation provider timed out.", true),
            HttpStatusCode.TooManyRequests =>
                (FailureCode.RateLimited, "The translation provider rate limit was reached.", true),
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                (FailureCode.ProviderUnavailable, "The translation provider is temporarily unavailable.", true),
            _ => (FailureCode.ProviderUnavailable, "The translation provider returned an unexpected HTTP status.", false)
        };

    private static OperationResult<T> Failure<T>(FailureCode code, string message, CorrelationId correlation, bool transient) =>
        OperationResult.Failed<T>(new PipelineFailure(code, message, correlation, transient));
}

using System.Net;
using System.Text;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Translation;

internal static class TranslationFoundationTests
{
    public static async Task RunAsync()
    {
        await TestConsentAndSuccessAsync().ConfigureAwait(false);
        await TestStatusMatrixAsync().ConfigureAwait(false);
        await TestTimeoutDisconnectOversizeAndMalformedAsync().ConfigureAwait(false);
        await TestCancellationAndConcurrencyAsync().ConfigureAwait(false);
        await TestRateLimitAndMediaTypeAsync().ConfigureAwait(false);
        TestOutputSanitizer();
        await TestCircuitAsync().ConfigureAwait(false);
        await TestCircuitQueuedAndStaleAsync().ConfigureAwait(false);
    }

    private static async Task TestConsentAndSuccessAsync()
    {
        var clock = new FakeClock();
        var handler = new ScriptedHandler((_, _) => Response(HttpStatusCode.OK, " translated\u0000 text\nnext "));
        var consent = new MutableConsent(false);
        using var provider = CreateProvider(handler, consent, clock);
        var request = Request();
        var denied = await provider.TranslateAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert(denied.Failure?.Code == FailureCode.ConsentRequired && handler.Calls == 0,
            "Remote consent must block before any HTTP request.");
        consent.Granted = true;
        var valid = await provider.ValidateConfigurationAsync(CancellationToken.None).ConfigureAwait(false);
        Assert(valid.IsSuccess, "HTTPS provider configuration should validate.");
        var result = await provider.TranslateAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert(result.IsSuccess && result.Value?.TranslatedText == "translated text\nnext",
            "Successful text must be sanitized and returned.");
        Assert(result.Value?.Metadata == request.Metadata && handler.Calls == 1,
            "The result must retain correlation/session metadata and send one request.");
        Assert(handler.LastRequestBody?.Contains("subtitle", StringComparison.Ordinal) == true,
            "Only the translation text should be serialized into the wire request.");
    }

    private static async Task TestStatusMatrixAsync()
    {
        var cases = new[]
        {
            (HttpStatusCode.BadRequest, FailureCode.Validation, 1),
            (HttpStatusCode.Unauthorized, FailureCode.AuthenticationRejected, 1),
            (HttpStatusCode.Forbidden, FailureCode.AuthenticationRejected, 1),
            (HttpStatusCode.RequestTimeout, FailureCode.Timeout, 3),
            (HttpStatusCode.TooManyRequests, FailureCode.RateLimited, 3),
            (HttpStatusCode.InternalServerError, FailureCode.ProviderUnavailable, 3),
            (HttpStatusCode.BadGateway, FailureCode.ProviderUnavailable, 3),
            (HttpStatusCode.ServiceUnavailable, FailureCode.ProviderUnavailable, 3),
        };
        foreach (var (status, expected, calls) in cases)
        {
            var clock = new FakeClock();
            var handler = new ScriptedHandler((_, _) => new HttpResponseMessage(status));
            using var provider = CreateProvider(handler, new MutableConsent(true), clock);
            var result = await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
            Assert(result.Failure?.Code == expected && handler.Calls == calls,
                $"HTTP {(int)status} must map to {expected} with {calls} attempt(s).");
        }

        var retryClock = new FakeClock();
        var retryDelay = new FakeDelay(retryClock);
        var retryHandler = new ScriptedHandler((call, _) =>
        {
            if (call == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(4));
                return response;
            }

            return Response(HttpStatusCode.OK, "done");
        });
        using var retryProvider = CreateProvider(retryHandler, new MutableConsent(true), retryClock, retryDelay);
        var retryResult = await retryProvider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(retryResult.IsSuccess && retryHandler.Calls == 2 &&
            retryDelay.Delays.Any(delay => delay == TimeSpan.FromSeconds(4)),
            "429 Retry-After must be honored before a bounded retry.");
    }

    private static async Task TestTimeoutDisconnectOversizeAndMalformedAsync()
    {
        var clock = new FakeClock();
        var disconnect = new ScriptedHandler((Func<int, CancellationToken, HttpResponseMessage>)
            ((_, _) => throw new HttpRequestException("private connection detail")));
        using (var provider = CreateProvider(disconnect, new MutableConsent(true), clock))
        {
            var result = await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
            Assert(result.Failure?.Code == FailureCode.ProviderUnavailable && disconnect.Calls == 3 &&
                !result.Failure.Message.Contains("private", StringComparison.Ordinal),
                "Disconnects must retry within bounds and return a safe neutral error.");
        }

        var malformed = new ScriptedHandler((_, _) => Response(HttpStatusCode.OK, "MALFORMED"));
        using (var provider = CreateProvider(malformed, new MutableConsent(true), new FakeClock()))
        {
            var result = await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
            Assert(result.Failure?.Code == FailureCode.ResponseMalformed && malformed.Calls == 1,
                "Malformed success responses must not retry.");
        }

        var oversize = new ScriptedHandler((_, _) => Response(HttpStatusCode.OK, new string('x', 600)));
        using (var provider = CreateProvider(oversize, new MutableConsent(true), new FakeClock(),
            policy: new TranslationHttpPolicy { MaximumResponseBytes = 256 }))
        {
            var result = await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
            Assert(result.Failure?.Code == FailureCode.ResponseTooLarge && oversize.Calls == 1,
                "Oversize responses must be rejected before parsing.");
        }

        var slow = new ScriptedHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return Response(HttpStatusCode.OK, "late");
        });
        using (var provider = CreateProvider(slow, new MutableConsent(true), new FakeClock(),
            policy: new TranslationHttpPolicy { TimeoutMilliseconds = 100, MaximumRetries = 0 }))
        {
            var result = await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
            Assert(result.Failure?.Code == FailureCode.Timeout && slow.Calls == 1,
                "A slow provider must return a typed per-request timeout.");
        }
    }

    private static async Task TestCancellationAndConcurrencyAsync()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var handler = new ScriptedHandler((_, _) => Response(HttpStatusCode.OK, "done"));
        using (var provider = CreateProvider(handler, new MutableConsent(true), new FakeClock()))
        {
            await AssertThrowsAsync<OperationCanceledException>(() =>
                provider.TranslateAsync(Request(), cancelled.Token)).ConfigureAwait(false);
            Assert(handler.Calls == 0, "Cancellation before admission must not send a request.");
        }

        using var started = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var concurrent = new ScriptedHandler(async (call, token) =>
        {
            if (call <= 2)
            {
                started.Signal();
            }
            await Task.Run(() => release.Wait(token), token).ConfigureAwait(false);
            return Response(HttpStatusCode.OK, "done");
        });
        using var limited = CreateProvider(concurrent, new MutableConsent(true), new FakeClock(),
            policy: new TranslationHttpPolicy { MaximumConcurrentRequests = 2, MinimumRequestIntervalMilliseconds = 0 });
        var pending = Enumerable.Range(0, 5)
            .Select(_ => limited.TranslateAsync(Request(), CancellationToken.None))
            .ToArray();
        Assert(started.Wait(TimeSpan.FromSeconds(5)), "Two concurrent requests did not start.");
        Assert(concurrent.MaximumActive == 2 && concurrent.Calls == 2,
            "The provider must hold later requests behind the concurrency limit.");
        release.Set();
        var results = await Task.WhenAll(pending).ConfigureAwait(false);
        Assert(results.All(item => item.IsSuccess) && concurrent.MaximumActive == 2,
            "Queued requests must finish without exceeding the concurrency limit.");
    }

    private static async Task TestCircuitAsync()
    {
        var clock = new FakeClock();
        var recovering = false;
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, token) =>
        {
            if (!recovering)
            { return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable); }
            probeStarted.TrySetResult();
            await releaseProbe.Task.WaitAsync(token).ConfigureAwait(false);
            return Response(HttpStatusCode.OK, "recovered");
        });
        using var provider = CreateProvider(handler, new MutableConsent(true), clock,
            policy: new TranslationHttpPolicy { MaximumRetries = 0, MinimumRequestIntervalMilliseconds = 0 });
        for (var i = 0; i < 3; i++)
        {
            Assert((await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false)).Failure?.IsTransient == true,
                "Service outage must remain transient.");
        }
        var blockedRequest = Request();
        var blocked = await provider.TranslateAsync(blockedRequest, CancellationToken.None).ConfigureAwait(false);
        Assert(handler.Calls == 3 && blocked.Failure?.CorrelationId == blockedRequest.Metadata.CorrelationId,
            "Open circuit must preserve correlation and suppress HTTP.");
        clock.Advance(TimeSpan.FromMilliseconds(9_999));
        await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(handler.Calls == 3, "Circuit must remain open before its exact deadline.");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        // Failed half-open probe opens a fresh cooldown.
        await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(handler.Calls == 4, "Failed recovery probe must reopen the circuit.");
        clock.Advance(TimeSpan.FromSeconds(10));
        recovering = true;
        using var cancellation = new CancellationTokenSource();
        var probe = provider.TranslateAsync(Request(), cancellation.Token);
        await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(handler.Calls == 5, "Half-open circuit permits only one probe.");
        cancellation.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => probe).ConfigureAwait(false);
        releaseProbe.SetResult();
        Assert((await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false)).IsSuccess,
            "Cancelled probe must release ownership without another outage penalty.");
        Assert((await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false)).IsSuccess && handler.Calls == 7,
            "Successful probe must restore normal admission.");
        foreach (var policy in new[]
        {
            new TranslationHttpPolicy { CircuitFailureThreshold = 0 },
            new TranslationHttpPolicy { CircuitFailureThreshold = 101 },
            new TranslationHttpPolicy { CircuitBreakMilliseconds = 99 },
            new TranslationHttpPolicy { CircuitBreakMilliseconds = 60_001 }
        })
        {
            await AssertThrowsAsync<ArgumentOutOfRangeException>(() =>
            {
                policy.Validate();
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
    }

    private static void TestOutputSanitizer()
    {
        var sanitized = PlainTextOutputSanitizer.Sanitize("  A\u0000  B\r\nC\nD\nE\u200B", 8, 2);
        Assert(sanitized == "A B\nC", $"Output sanitizer changed: '{sanitized}'.");
        Assert(PlainTextOutputSanitizer.Sanitize("\u0000\u0001", 100, 3).Length == 0,
            "Control-only provider output must become empty.");
    }

    private static async Task TestCircuitQueuedAndStaleAsync()
    {
        var clock = new FakeClock();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new ScriptedHandler(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var policy = new TranslationHttpPolicy
        {
            MaximumConcurrentRequests = 1,
            MaximumRetries = 0,
            MinimumRequestIntervalMilliseconds = 0,
            CircuitFailureThreshold = 1
        };
        using (var provider = CreateProvider(handler, new MutableConsent(true), clock, policy: policy))
        {
            var first = provider.TranslateAsync(Request(), CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            var queued = Enumerable.Range(0, 8).Select(_ => provider.TranslateAsync(Request(), CancellationToken.None)).ToArray();
            using var cancellation = new CancellationTokenSource();
            var cancelled = provider.TranslateAsync(Request(), cancellation.Token);
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() => cancelled).ConfigureAwait(false);
            release.SetResult();
            await first.ConfigureAwait(false);
            var results = await Task.WhenAll(queued).ConfigureAwait(false);
            Assert(handler.Calls == 1 && results.All(result => result.Failure?.IsTransient == true),
                "Circuit completion must precede releasing queued requests; none may send HTTP.");
        }

        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleHandler = new ScriptedHandler(async (call, token) =>
        {
            if (call == 1)
            {
                oldStarted.SetResult();
                await releaseOld.Task.WaitAsync(token).ConfigureAwait(false);
                return Response(HttpStatusCode.OK, "old success");
            }
            return new HttpResponseMessage(call == 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Unauthorized);
        });
        using var staleProvider = CreateProvider(staleHandler, new MutableConsent(true), clock,
            policy: policy with { MaximumConcurrentRequests = 2 });
        var old = staleProvider.TranslateAsync(Request(), CancellationToken.None);
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await staleProvider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        releaseOld.SetResult();
        Assert((await old.ConfigureAwait(false)).IsSuccess, "Already in-flight response remains valid for its caller.");
        await staleProvider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(staleHandler.Calls == 2, "Old success must not close the newer circuit generation.");
        clock.Advance(TimeSpan.FromSeconds(10));
        var denied = await staleProvider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(denied.Failure?.Code == FailureCode.AuthenticationRejected && denied.Failure.IsTransient == false,
            "Permanent probe error must preserve its actionable classification.");
        await staleProvider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(staleHandler.Calls == 4, "Permanent provider response must clear the temporary outage circuit.");
    }

    private static async Task TestRateLimitAndMediaTypeAsync()
    {
        var clock = new FakeClock();
        var delay = new FakeDelay(clock);
        var handler = new ScriptedHandler((_, _) => Response(HttpStatusCode.OK, "done"));
        using (var provider = CreateProvider(handler, new MutableConsent(true), clock, delay,
            new TranslationHttpPolicy { MinimumRequestIntervalMilliseconds = 250 }))
        {
            var first = await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
            var second = await provider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
            Assert(first.IsSuccess && second.IsSuccess &&
                delay.Delays.Contains(TimeSpan.FromMilliseconds(250)),
                "A second request must wait for the configured rate slot.");
        }

        var mediaHandler = new ScriptedHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not a translation</html>", Encoding.UTF8, "text/html")
        });
        using var mediaProvider = CreateProvider(mediaHandler, new MutableConsent(true), new FakeClock());
        var mediaResult = await mediaProvider.TranslateAsync(Request(), CancellationToken.None).ConfigureAwait(false);
        Assert(mediaResult.Failure?.Code == FailureCode.ResponseMalformed && mediaHandler.Calls == 1,
            "An unexpected response media type must be rejected without retry.");
    }

    private static HttpTranslationProvider CreateProvider(
        ScriptedHandler handler,
        MutableConsent consent,
        FakeClock clock,
        FakeDelay? delay = null,
        TranslationHttpPolicy? policy = null) => new(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            new FakeProtocol(),
            consent,
            clock,
            delay ?? new FakeDelay(clock),
            new FixedJitter(),
            policy ?? new TranslationHttpPolicy { MinimumRequestIntervalMilliseconds = 0 });

    private static TranslationRequest Request() => new(
        new PipelineMetadata(SessionId.New(), new SequenceNumber(1), DateTimeOffset.UnixEpoch, CorrelationId.New()),
        "ja", "en", "subtitle", null, null, "fake-model");

    private static HttpResponseMessage Response(HttpStatusCode status, string text) => new(status)
    {
        Content = new StringContent(text, Encoding.UTF8, "text/plain")
    };

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

    private sealed class MutableConsent(bool granted) : IRemoteTextConsent
    {
        public bool Granted { get; set; } = granted;

        public ValueTask<bool> IsGrantedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Granted);
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;

        public void Advance(TimeSpan duration) => UtcNow += duration;
    }

    private sealed class FakeDelay(FakeClock clock) : ITranslationDelay
    {
        public List<TimeSpan> Delays { get; } = new();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            clock.Advance(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedJitter : IRetryJitter
    {
        public double NextUnitInterval() => 0;
    }

    private sealed class FakeProtocol : ITranslationHttpProtocol
    {
        public string Id => "fake-http";

        public Uri BaseUri { get; } = new("https://translator.invalid/");

        public TranslationProviderCapabilities Capabilities { get; } = new(true, false, 4_000);

        public IReadOnlySet<string> AcceptedResponseMediaTypes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "text/plain"
        };

        public HttpRequestMessage CreateRequest(TranslationRequest request) => new(HttpMethod.Post,
            new Uri(BaseUri, "translate"))
        {
            Content = new StringContent(request.Text, Encoding.UTF8, "text/plain")
        };

        public HttpRequestMessage? CreateHealthRequest() => null;

        public TranslationHttpPayload ParseResponse(ReadOnlyMemory<byte> response)
        {
            var text = Encoding.UTF8.GetString(response.Span);
            if (text == "MALFORMED")
            {
                throw new FormatException("synthetic malformed response");
            }

            return new TranslationHttpPayload(text, new TranslationUsage(12, 8));
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _handle;
        private int _calls;
        private int _active;
        private int _maximumActive;

        public ScriptedHandler(Func<int, CancellationToken, HttpResponseMessage> handle)
            : this((call, token) => Task.FromResult(handle(call, token)))
        {
        }

        public ScriptedHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> handle) => _handle = handle;

        public int Calls => Volatile.Read(ref _calls);

        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var call = Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                return await _handle(call, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int value)
        {
            var current = Volatile.Read(ref _maximumActive);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref _maximumActive, value, current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.Infrastructure.Translation.Ollama;

public enum OllamaModelState
{
    Unknown,
    Ready,
    Missing,
    Loading,
    Unavailable
}

/// <summary>Local-first Ollama text translator; it never pulls or installs models.</summary>
public sealed class OllamaProvider : ITranslationProvider, IDisposable
{
    private const int MaximumTagsBytes = 64 * 1024;
    private readonly ProviderOptions _options;
    private readonly HttpClient _client;
    private readonly HttpTranslationProvider _foundation;
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly IClock _clock;
    private readonly ITranslationDelay _delay;
    private readonly IRetryJitter _jitter;
    private int _discoveryFailures;
    private DateTimeOffset _nextDiscoveryAt;

    public OllamaProvider(
        ProviderOptions options,
        IRemoteTextConsent remoteConsent,
        IClock clock,
        ITranslationDelay delay,
        IRetryJitter jitter,
        HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _jitter = jitter ?? throw new ArgumentNullException(nameof(jitter));
        var baseUri = options.BaseUri ?? new Uri("http://127.0.0.1:11434/");
        if (options.Kind != ProviderKind.Ollama || string.IsNullOrWhiteSpace(options.Id) ||
            string.IsNullOrWhiteSpace(options.Model) || options.TimeoutSeconds is < 1 or > 120 ||
            !double.IsFinite(options.Temperature) || options.Temperature is < 0 or > 2 ||
            options.MaximumOutputTokens is < 1 or > 4_096 ||
            !baseUri.IsAbsoluteUri ||
            (baseUri.Scheme != Uri.UriSchemeHttps &&
             !(baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback)) ||
            baseUri.UserInfo.Length != 0 || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 ||
            baseUri.AbsolutePath != "/")
        {
            throw new ArgumentException("The Ollama endpoint or model configuration is invalid.", nameof(options));
        }

        _options = options with { BaseUri = baseUri };
        _client = new HttpClient(transport ?? new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var consent = baseUri.Scheme == Uri.UriSchemeHttp && baseUri.IsLoopback
            ? new LocalTextConsent() : remoteConsent;
        _foundation = new HttpTranslationProvider(_client, new OllamaGenerateProtocol(_options), consent,
            clock, delay, jitter, new TranslationHttpPolicy
            {
                TimeoutMilliseconds = options.TimeoutSeconds * 1_000
            });
    }

    public string Id => _foundation.Id;

    public TranslationProviderCapabilities Capabilities => _foundation.Capabilities;

    public OllamaModelState ModelState { get; private set; } = OllamaModelState.Unknown;

    public ValueTask<OperationResult<bool>> ValidateConfigurationAsync(CancellationToken cancellationToken) =>
        _foundation.ValidateConfigurationAsync(cancellationToken);

    public async ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var models = await DiscoverModelsAsync(cancellationToken).ConfigureAwait(false);
        return new ProviderHealth(models.IsSuccess ? ProviderHealthStatus.Available : ProviderHealthStatus.Unavailable,
            models.Failure);
    }

    /// <summary>Lists installed local models via GET /api/tags; no model pull is attempted.</summary>
    public async Task<OperationResult<IReadOnlyList<string>>> DiscoverModelsAsync(CancellationToken cancellationToken)
    {
        await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Discovery precedes the shared translation policy; retain cooldown across calls
            // so new subtitle work cannot repeatedly probe an unavailable service.
            var wait = _nextDiscoveryAt - _clock.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await _delay.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = await DiscoverModelsCoreAsync(cancellationToken).ConfigureAwait(false);
            if (result.Failure is { IsTransient: true })
            {
                _discoveryFailures = Math.Min(_discoveryFailures + 1, 6);
                var seconds = Math.Pow(2, _discoveryFailures - 1) * (1 + 0.5 * _jitter.NextUnitInterval());
                _nextDiscoveryAt = _clock.UtcNow.AddSeconds(Math.Min(30, seconds));
            }
            else
            {
                _discoveryFailures = 0;
                _nextDiscoveryAt = default;
            }
            return result;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task<OperationResult<IReadOnlyList<string>>> DiscoverModelsCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri!, "api/tags"));
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                ModelState = OllamaModelState.Unavailable;
                var classification = HttpTranslationProvider.Classify(response.StatusCode);
                return Failure<IReadOnlyList<string>>(classification.Code,
                    classification.Message, classification.Transient);
            }

            if (response.Content.Headers.ContentLength > MaximumTagsBytes)
            {
                return Failure<IReadOnlyList<string>>(FailureCode.ResponseTooLarge,
                    "Ollama returned an oversized model list.", false);
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Failure<IReadOnlyList<string>>(FailureCode.ResponseMalformed,
                    "Ollama returned invalid model-list metadata.", false);
            }

            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var destination = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (destination.Length + read > MaximumTagsBytes)
                {
                    return Failure<IReadOnlyList<string>>(FailureCode.ResponseTooLarge,
                        "Ollama returned an oversized model list.", false);
                }

                destination.Write(buffer, 0, read);
            }

            using var json = JsonDocument.Parse(destination.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array || models.GetArrayLength() > 1_000)
            {
                return Failure<IReadOnlyList<string>>(FailureCode.ResponseMalformed,
                    "Ollama returned an unreadable model list.", false);
            }

            var names = new List<string>(models.GetArrayLength());
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object ||
                    !model.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                    name.GetString() is not { Length: > 0 and <= 256 } value)
                {
                    return Failure<IReadOnlyList<string>>(FailureCode.ResponseMalformed,
                        "Ollama returned an unreadable model name.", false);
                }

                names.Add(value);
            }

            ModelState = names.Contains(_options.Model, StringComparer.OrdinalIgnoreCase)
                ? OllamaModelState.Ready : OllamaModelState.Missing;
            return OperationResult.Success<IReadOnlyList<string>>(names.AsReadOnly());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            ModelState = OllamaModelState.Unavailable;
            return Failure<IReadOnlyList<string>>(FailureCode.Timeout,
                "Ollama model discovery timed out. Check whether the service is loading.", true);
        }
        catch (HttpRequestException)
        {
            ModelState = OllamaModelState.Unavailable;
            return Failure<IReadOnlyList<string>>(FailureCode.ProviderUnavailable,
                "Ollama is unavailable. Start the local service and retry.", true);
        }
        catch (JsonException)
        {
            return Failure<IReadOnlyList<string>>(FailureCode.ResponseMalformed,
                "Ollama returned an unreadable model list.", false);
        }
    }

    public async Task<OperationResult<TranslationResult>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var models = await DiscoverModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!models.IsSuccess)
        {
            return OperationResult.Failed<TranslationResult>(models.Failure!);
        }

        if (ModelState == OllamaModelState.Missing)
        {
            return Failure<TranslationResult>(FailureCode.ModelMissing,
                "The configured Ollama model is not installed. Install it explicitly in Ollama before retrying.", false);
        }

        ModelState = OllamaModelState.Loading;
        try
        {
            var configuredRequest = new TranslationRequest(request.Metadata, request.SourceLanguage,
                request.TargetLanguage, request.Text, request.Context, request.Glossary, _options.Model,
                request.PromptOptions, request.GlossaryRevision);
            var result = await _foundation.TranslateAsync(configuredRequest, cancellationToken).ConfigureAwait(false);
            ModelState = result.IsSuccess ? OllamaModelState.Ready : OllamaModelState.Unavailable;
            return result;
        }
        catch (OperationCanceledException)
        {
            ModelState = OllamaModelState.Unknown;
            throw;
        }
    }

    public void Dispose()
    {
        _foundation.Dispose();
        _discoveryGate.Dispose();
        _client.Dispose();
    }

    private static OperationResult<T> Failure<T>(FailureCode code, string message, bool transient) =>
        OperationResult.Failed<T>(new PipelineFailure(code, message, CorrelationId.New(), transient));

    private sealed class LocalTextConsent : IRemoteTextConsent
    {
        public ValueTask<bool> IsGrantedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(true);
        }
    }
}

internal sealed class OllamaGenerateProtocol(ProviderOptions options) : ITranslationHttpProtocol
{
    public string Id => options.Id;

    public Uri BaseUri => options.BaseUri!;

    public TranslationProviderCapabilities Capabilities { get; } =
        new(!options.BaseUri!.IsLoopback, true, 4_000, true);

    public IReadOnlySet<string> AcceptedResponseMediaTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" };

    public HttpRequestMessage CreateRequest(TranslationRequest request)
    {
        var plan = TranslationPromptBuilder.Build(request);
        return new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "api/generate"))
        {
            Content = JsonContent.Create(new
            {
                model = options.Model,
                system = plan.SystemMessage,
                prompt = plan.UserDataJson,
                stream = false,
                options = new { num_predict = options.MaximumOutputTokens, temperature = options.Temperature }
            })
        };
    }

    public HttpRequestMessage CreateHealthRequest() => new(HttpMethod.Get, new Uri(BaseUri, "api/tags"));

    public TranslationHttpPayload ParseResponse(ReadOnlyMemory<byte> response)
    {
        using var json = JsonDocument.Parse(response);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("response", out var content) || content.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True)
        {
            throw new FormatException("Invalid non-streaming Ollama response.");
        }

        return new TranslationHttpPayload(content.GetString() ?? string.Empty,
            new TranslationUsage(ReadCount(root, "prompt_eval_count"), ReadCount(root, "eval_count")));
    }

    private static int? ReadCount(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var count))
        {
            throw new FormatException("Invalid Ollama token count.");
        }

        return count;
    }
}

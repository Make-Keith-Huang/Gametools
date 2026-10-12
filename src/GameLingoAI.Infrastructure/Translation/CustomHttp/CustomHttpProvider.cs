using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.Infrastructure.Translation.CustomHttp;

public sealed record CustomHttpRequestPreview(string Method, string Endpoint, string BodyJson,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>Declarative JSON-only provider with destination and credential boundaries.</summary>
public sealed class CustomHttpProvider : ITranslationProvider, IDisposable
{
    private readonly ProviderOptions _options;
    private readonly IRemoteTextConsent _consent;
    private readonly CustomHttpDestinationPolicy _destinations;
    private readonly HttpClient _client;
    private readonly HttpTranslationProvider _foundation;

    public CustomHttpProvider(
        ProviderOptions options,
        ISecretStore secrets,
        IRemoteTextConsent consent,
        IClock clock,
        ITranslationDelay delay,
        IRetryJitter jitter,
        ICustomHttpDnsResolver? resolver = null,
        HttpMessageHandler? transport = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _consent = consent ?? throw new ArgumentNullException(nameof(consent));
        ArgumentNullException.ThrowIfNull(secrets);
        var validation = AppSettingsValidator.Validate(new AppSettings { Providers = new[] { options } });
        if (options.Kind != ProviderKind.CustomHttp || !validation.IsValid || options.BaseUri is null ||
            options.CustomHttp is null)
        {
            throw new ArgumentException("The custom HTTP mapping or endpoint is invalid.", nameof(options));
        }

        _destinations = new CustomHttpDestinationPolicy(resolver ?? new SystemCustomHttpDnsResolver(),
            options.CustomHttp.LocalOnly);
        var inner = transport ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = _destinations.ConnectAsync
        };
        _client = new HttpClient(new CustomHttpGuardHandler(options, secrets, _destinations)
        {
            InnerHandler = inner
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _foundation = new HttpTranslationProvider(_client, new CustomHttpProtocol(options), consent,
            clock, delay, jitter, new TranslationHttpPolicy
            {
                TimeoutMilliseconds = options.TimeoutSeconds * 1_000
            });
    }

    public string Id => _foundation.Id;

    public TranslationProviderCapabilities Capabilities => _foundation.Capabilities;

    public async ValueTask<OperationResult<bool>> ValidateConfigurationAsync(CancellationToken cancellationToken)
    {
        var validation = await _foundation.ValidateConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return validation;
        }

        try
        {
            await _destinations.ResolveAllowedAsync(_options.BaseUri!, cancellationToken).ConfigureAwait(false);
            return validation;
        }
        catch (Exception exception) when (exception is CustomHttpDestinationRejectedException or
            System.Net.Sockets.SocketException)
        {
            return Failure<bool>(FailureCode.Validation,
                "Custom HTTP destination is not an allowed public endpoint or explicit loopback target.");
        }
    }

    public ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        _foundation.CheckHealthAsync(cancellationToken);

    public async Task<OperationResult<TranslationResult>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await _consent.IsGrantedAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<TranslationResult>(FailureCode.ConsentRequired,
                "Enable remote text processing before using custom HTTP translation.",
                request.Metadata.CorrelationId);
        }

        var validation = await ValidateConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return Failure<TranslationResult>(validation.Failure!.Code, validation.Failure.Message,
                request.Metadata.CorrelationId);
        }

        try
        {
            return await _foundation.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (CustomHttpDestinationRejectedException)
        {
            return Failure<TranslationResult>(FailureCode.Validation,
                "Custom HTTP destination changed to a disallowed address.",
                request.Metadata.CorrelationId);
        }
    }

    /// <summary>Shows structure only; no stored secret or captured text is resolved.</summary>
    public CustomHttpRequestPreview Preview()
    {
        var metadata = new PipelineMetadata(SessionId.New(), new SequenceNumber(0),
            DateTimeOffset.UnixEpoch, CorrelationId.New());
        var request = new TranslationRequest(metadata, "source", "target", "<redacted OCR text>",
            Array.Empty<string>(), Array.Empty<GlossaryTerm>(), "<model>");
        var headers = _options.CustomHttp!.Headers.ToDictionary(header => header.Name,
            _ => "<redacted>", StringComparer.OrdinalIgnoreCase);
        return new CustomHttpRequestPreview(_options.CustomHttp.Method.ToString().ToUpperInvariant(),
            _options.BaseUri!.AbsoluteUri, CustomHttpProtocol.BuildBody(_options.CustomHttp, request), headers);
    }

    public void Dispose()
    {
        _foundation.Dispose();
        _client.Dispose();
    }

    private static OperationResult<T> Failure<T>(FailureCode code, string message,
        CorrelationId? correlation = null) =>
        OperationResult.Failed<T>(new PipelineFailure(code, message, correlation ?? CorrelationId.New(), false));
}

internal sealed class CustomHttpGuardHandler(
    ProviderOptions options,
    ISecretStore secrets,
    CustomHttpDestinationPolicy destinations) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        var configured = options.BaseUri!;
        if (uri is null || uri.Scheme != configured.Scheme ||
            !string.Equals(uri.IdnHost, configured.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != configured.Port)
        {
            throw new CustomHttpDestinationRejectedException();
        }

        await destinations.ResolveAllowedAsync(uri, cancellationToken).ConfigureAwait(false);
        foreach (var header in options.CustomHttp!.Headers)
        {
            string? value;
            if (header.SecretReference is { } reference)
            {
                try
                {
                    value = await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
                if (string.IsNullOrWhiteSpace(value) || value.Length > 2_560 ||
                    value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }
            }
            else
            {
                value = header.Value;
            }

            if (header.Name == "Authorization")
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(header.Name, value);
            }
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CustomHttpProtocol(ProviderOptions options) : ITranslationHttpProtocol
{
    public string Id => options.Id;

    public Uri BaseUri => options.BaseUri!;

    public TranslationProviderCapabilities Capabilities { get; } = new(true, false, 4_000);

    public IReadOnlySet<string> AcceptedResponseMediaTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" };

    public HttpRequestMessage CreateRequest(TranslationRequest request) => new(
        options.CustomHttp!.Method == CustomHttpMethod.Post ? HttpMethod.Post : HttpMethod.Put,
        options.BaseUri)
    {
        Content = JsonContent.Create(JsonNode.Parse(BuildBody(options.CustomHttp, request)))
    };

    public HttpRequestMessage? CreateHealthRequest() => null;

    public TranslationHttpPayload ParseResponse(ReadOnlyMemory<byte> response)
    {
        using var json = JsonDocument.Parse(response);
        var current = json.RootElement;
        var path = options.CustomHttp!.ResponsePath;
        for (var offset = 1; offset < path.Length;)
        {
            if (path[offset] == '.')
            {
                var start = ++offset;
                while (offset < path.Length && path[offset] is not '.' and not '[')
                {
                    offset++;
                }

                var name = path[start..offset];
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    throw new FormatException("Configured response path is absent.");
                }
            }
            else
            {
                var end = path.IndexOf(']', offset);
                var index = int.Parse(path[(offset + 1)..end], System.Globalization.CultureInfo.InvariantCulture);
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    throw new FormatException("Configured response path is absent.");
                }

                current = current[index];
                offset = end + 1;
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("Configured response value is not text.");
        }

        return new TranslationHttpPayload(current.GetString() ?? string.Empty);
    }

    public static string BuildBody(CustomHttpOptions mapping, TranslationRequest request)
    {
        var plan = TranslationPromptBuilder.Build(request);
        var root = JsonNode.Parse(mapping.BodyJsonTemplate)!;
        return Replace(root, request, plan)!.ToJsonString();
    }

    private static JsonNode? Replace(JsonNode? node, TranslationRequest request, TranslationPromptPlan plan)
    {
        if (node is JsonObject objectNode)
        {
            var result = new JsonObject();
            foreach (var pair in objectNode)
            {
                result[pair.Key] = Replace(pair.Value, request, plan);
            }

            return result;
        }

        if (node is JsonArray arrayNode)
        {
            var result = new JsonArray();
            foreach (var item in arrayNode)
            {
                result.Add(Replace(item, request, plan));
            }

            return result;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var token))
        {
            return token switch
            {
                "{{text}}" => JsonValue.Create(plan.Data.Text),
                "{{sourceLanguage}}" => JsonValue.Create(plan.Data.SourceLanguage),
                "{{targetLanguage}}" => JsonValue.Create(plan.Data.TargetLanguage),
                "{{model}}" => JsonValue.Create(request.Model ?? string.Empty),
                "{{context}}" => JsonSerializer.SerializeToNode(plan.Data.Context),
                "{{glossary}}" => JsonSerializer.SerializeToNode(plan.SelectedTerms),
                "{{systemPrompt}}" => JsonValue.Create(plan.SystemMessage),
                "{{promptData}}" => JsonValue.Create(plan.UserDataJson),
                _ => value.DeepClone()
            };
        }

        return node?.DeepClone();
    }
}

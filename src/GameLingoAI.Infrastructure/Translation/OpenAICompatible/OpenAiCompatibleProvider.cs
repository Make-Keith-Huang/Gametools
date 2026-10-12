using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

namespace GameLingoAI.Infrastructure.Translation.OpenAICompatible;

/// <summary>Creates a single explicitly configured text-only OpenAI-compatible provider.</summary>
public sealed class OpenAiCompatibleProvider : ITranslationProvider, IDisposable
{
    private readonly ProviderOptions _options;
    private readonly HttpClient _client;
    private readonly HttpTranslationProvider _foundation;

    public OpenAiCompatibleProvider(
        ProviderOptions options,
        ISecretStore secrets,
        IRemoteTextConsent consent,
        IClock clock,
        ITranslationDelay delay,
        IRetryJitter jitter,
        HttpMessageHandler? transport = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentNullException.ThrowIfNull(secrets);
        var validation = AppSettingsValidator.Validate(new AppSettings { Providers = new[] { options } });
        if (options.Kind != ProviderKind.OpenAiCompatible || !validation.IsValid ||
            options.BaseUri is null || options.BaseUri.UserInfo.Length != 0 ||
            options.BaseUri.Query.Length != 0 || options.BaseUri.Fragment.Length != 0 ||
            !options.BaseUri.AbsolutePath.EndsWith('/'))
        {
            throw new ArgumentException("The OpenAI-compatible provider configuration is invalid.", nameof(options));
        }

        var auth = new OpenAiAuthenticationHandler(options.BaseUri, options.SecretReference!, secrets)
        {
            InnerHandler = transport ?? new HttpClientHandler { AllowAutoRedirect = false }
        };
        _client = new HttpClient(auth) { Timeout = Timeout.InfiniteTimeSpan };
        _foundation = new HttpTranslationProvider(_client, new OpenAiChatProtocol(options), consent, clock,
            delay, jitter, new TranslationHttpPolicy { TimeoutMilliseconds = options.TimeoutSeconds * 1_000 });
    }

    public string Id => _foundation.Id;

    public TranslationProviderCapabilities Capabilities => _foundation.Capabilities;

    public ValueTask<OperationResult<bool>> ValidateConfigurationAsync(CancellationToken cancellationToken) =>
        _foundation.ValidateConfigurationAsync(cancellationToken);

    public ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken) =>
        _foundation.CheckHealthAsync(cancellationToken);

    /// <summary>A user-invoked connection probe; sends only GET /models, never OCR or game text.</summary>
    public ValueTask<ProviderHealth> TestConnectionAsync(CancellationToken cancellationToken) =>
        CheckHealthAsync(cancellationToken);

    public Task<OperationResult<TranslationResult>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var configuredRequest = new TranslationRequest(request.Metadata, request.SourceLanguage,
            request.TargetLanguage, request.Text, request.Context, request.Glossary, _options.Model,
            request.PromptOptions, request.GlossaryRevision);
        return _foundation.TranslateAsync(configuredRequest, cancellationToken);
    }

    public void Dispose()
    {
        _foundation.Dispose();
        _client.Dispose();
    }
}

internal sealed class OpenAiAuthenticationHandler(
    Uri origin,
    string secretReference,
    ISecretStore secrets) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) ||
            uri.Port != origin.Port)
        {
            throw new HttpRequestException("Provider request origin is not configured.");
        }

        string? secret;
        try
        {
            secret = await secrets.GetAsync(secretReference, cancellationToken).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        }

        if (string.IsNullOrWhiteSpace(secret) || secret.Any(char.IsControl))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class OpenAiChatProtocol(ProviderOptions options) : ITranslationHttpProtocol
{
    public string Id => options.Id;

    public Uri BaseUri => options.BaseUri!;

    public TranslationProviderCapabilities Capabilities { get; } = new(true, true, 4_000, true);

    public IReadOnlySet<string> AcceptedResponseMediaTypes { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/json" };

    public HttpRequestMessage CreateRequest(TranslationRequest request)
    {
        var plan = TranslationPromptBuilder.Build(request);
        var content = new
        {
            model = options.Model,
            temperature = options.Temperature,
            max_completion_tokens = options.MaximumOutputTokens,
            stream = false,
            messages = new object[]
            {
                new { role = "system", content = plan.SystemMessage },
                new { role = "user", content = plan.UserDataJson }
            }
        };
        return new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "chat/completions"))
        {
            Content = JsonContent.Create(content)
        };
    }

    public HttpRequestMessage CreateHealthRequest() => new(HttpMethod.Get, new Uri(BaseUri, "models"));

    public TranslationHttpPayload ParseResponse(ReadOnlyMemory<byte> response)
    {
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0 ||
            choices[0].ValueKind != JsonValueKind.Object ||
            !choices[0].TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("Missing chat completion content.");
        }

        TranslationUsage? usage = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = new TranslationUsage(ReadOptionalCount(usageElement, "prompt_tokens"),
                ReadOptionalCount(usageElement, "completion_tokens"));
        }

        return new TranslationHttpPayload(content.GetString() ?? string.Empty, usage);
    }

    private static int? ReadOptionalCount(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var count))
        {
            return null;
        }

        if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var parsed))
        {
            throw new FormatException("Invalid token usage.");
        }

        return parsed;
    }
}

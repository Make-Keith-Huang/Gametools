using System.Windows.Input;
using GameLingoAI.App.Commands;
using GameLingoAI.App.Services;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Translation;
using GameLingoAI.Infrastructure.Translation.CustomHttp;
using GameLingoAI.Infrastructure.Translation.Ollama;
using GameLingoAI.Infrastructure.Translation.OpenAICompatible;

namespace GameLingoAI.App.ViewModels;

public sealed class ProviderConnectionViewModel : ObservableObject, IDisposable
{
    private readonly IConfigurationStore _configuration;
    private readonly ISecretStore _secrets;
    private readonly IRemoteTextConsent _consent;
    private readonly IClock _clock;
    private readonly ITranslationDelay _delay;
    private readonly IRetryJitter _jitter;
    private string _status = "Select Test connection to probe the configured provider and installed models. No game text is sent.";
    private string _preview = string.Empty;

    public ProviderConnectionViewModel(
        IConfigurationStore configuration,
        ISecretStore secrets,
        IRemoteTextConsent consent,
        IClock clock,
        ITranslationDelay delay,
        IRetryJitter jitter,
        IErrorReporter errorReporter)
    {
        _configuration = configuration;
        _secrets = secrets;
        _consent = consent;
        _clock = clock;
        _delay = delay;
        _jitter = jitter;
        TestConnectionCommand = new AsyncRelayCommand(TestConnectionAsync, errorReporter);
        PreviewCustomRequestCommand = new AsyncRelayCommand(PreviewCustomRequestAsync, errorReporter);
    }

    public ICommand TestConnectionCommand { get; }

    public ICommand PreviewCustomRequestCommand { get; }

    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public void Dispose()
    {
        ((AsyncRelayCommand)TestConnectionCommand).Dispose();
        ((AsyncRelayCommand)PreviewCustomRequestCommand).Dispose();
    }

    private async Task PreviewCustomRequestAsync(CancellationToken cancellationToken)
    {
        var settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(true);
        var selected = settings.Value?.Providers.FirstOrDefault(provider => provider.Kind == ProviderKind.CustomHttp);
        if (!settings.IsSuccess || selected is null)
        {
            Preview = "No valid custom HTTP provider is configured.";
            return;
        }

        try
        {
            using var provider = new CustomHttpProvider(selected, _secrets, _consent,
                _clock, _delay, _jitter);
            var preview = provider.Preview();
            Preview = $"{preview.Method} {preview.Endpoint}\n" +
                string.Join("\n", preview.Headers.Select(header => $"{header.Key}: {header.Value}")) +
                $"\n{preview.BodyJson}";
        }
        catch (ArgumentException)
        {
            Preview = "Custom HTTP mapping is invalid. Correct its endpoint, headers, template, or response path.";
        }
    }

    private async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var settings = await _configuration.LoadAsync(cancellationToken).ConfigureAwait(true);
        if (!settings.IsSuccess)
        {
            Status = "Settings could not be loaded. Correct the provider configuration and retry.";
            return;
        }

        var selected = settings.Value!.Providers.FirstOrDefault(provider =>
            provider.Kind is ProviderKind.OpenAiCompatible or ProviderKind.Ollama);
        if (selected is null)
        {
            Status = "No OpenAI-compatible or Ollama provider is configured.";
            return;
        }

        try
        {
            if (selected.Kind == ProviderKind.Ollama)
            {
                using var provider = new OllamaProvider(selected, _consent, _clock, _delay, _jitter);
                var models = await provider.DiscoverModelsAsync(cancellationToken).ConfigureAwait(true);
                Status = !models.IsSuccess
                    ? $"{selected.Id}: {models.Failure!.Message}"
                    : provider.ModelState == OllamaModelState.Missing
                        ? $"{selected.Id}: configured model is missing. Install it explicitly in Ollama."
                        : $"{selected.Id}: model ready. No game text was sent.";
            }
            else
            {
                using var provider = new OpenAiCompatibleProvider(selected, _secrets, _consent,
                    _clock, _delay, _jitter);
                var health = await provider.TestConnectionAsync(cancellationToken).ConfigureAwait(true);
                Status = health.Status == ProviderHealthStatus.Available
                    ? $"{selected.Id}: connection available. No game text was sent."
                    : $"{selected.Id}: connection unavailable. Check the endpoint and credential.";
            }
        }
        catch (ArgumentException)
        {
            Status = "Provider settings are invalid. Check the endpoint, model, dialect, and secret reference.";
        }
    }
}

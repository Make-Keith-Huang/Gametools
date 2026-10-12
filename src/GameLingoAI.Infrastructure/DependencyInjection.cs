using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Cache;
using GameLingoAI.Infrastructure.Capture;
using GameLingoAI.Infrastructure.Configuration;
using GameLingoAI.Infrastructure.Ocr;
using GameLingoAI.Infrastructure.Persistence;
using GameLingoAI.Infrastructure.Preprocessing;
using GameLingoAI.Infrastructure.Security;
using GameLingoAI.Infrastructure.Translation;
using Microsoft.Extensions.DependencyInjection;

namespace GameLingoAI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddGameLingoInfrastructure(this IServiceCollection services, string? databasePath = null)
    {
        services.AddSingleton<IConfigurationStore>(_ =>
            new JsonConfigurationStore(ConfigurationPaths.GetDefaultSettingsPath()));
        services.AddSingleton<ISecretStore, WindowsCredentialManagerSecretStore>();
        services.AddSingleton<IWindowDiscovery, WindowsWindowDiscovery>();
        services.AddSingleton<IWindowsGraphicsCaptureSourceFactory, WindowsGraphicsCaptureSourceFactory>();
        services.AddSingleton<IPixelBufferPool, SharedPixelBufferPool>();
        services.AddSingleton<IPreprocessingDiagnosticSink, DisabledPreprocessingDiagnosticSink>();
        services.AddSingleton<IFramePreprocessor, FramePreprocessor>();
        services.AddHttpClient("OcrModelDownloads", client => client.Timeout = TimeSpan.FromMinutes(15));
        services.AddSingleton(PaddleOcrModelCatalog.PpOcrV5Mobile);
        services.AddSingleton(serviceProvider => new OcrModelProvisioner(
            serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("OcrModelDownloads"),
            ConfigurationPaths.GetDefaultOcrModelsPath()));
        services.AddSingleton<IPaddleOcrRuntimeFactory, SdcbPaddleOcrRuntimeFactory>();
        services.AddSingleton<IOcrEngine>(serviceProvider => new PaddleOcrEngine(
            serviceProvider.GetRequiredService<OcrModelProvisioner>(),
            serviceProvider.GetRequiredService<IPaddleOcrRuntimeFactory>(),
            serviceProvider.GetRequiredService<OcrModelPackage>()));
        services.AddHttpClient<HttpTranslationProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<IRemoteTextConsent, ConfigurationRemoteTextConsent>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(_ => new SqliteDatabase(databasePath));
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IGlossaryRepository, SqliteGlossaryRepository>();
        services.AddSingleton<ITranslationHistoryRepository, SqliteHistoryRepository>();
        services.AddSingleton<ITranslationCacheRepository, SqliteTranslationCacheRepository>();
        services.AddSingleton<TranslationMemoryFactory>();
        services.AddSingleton<ISessionResourcesFactory, SessionResourcesFactory>();
        services.AddSingleton<ITranslationDelay, SystemTranslationDelay>();
        services.AddSingleton<IRetryJitter, RandomRetryJitter>();
        services.AddSingleton(new TranslationHttpPolicy());
        services.AddSingleton<ITranslationProvider, TranslationProviderPlaceholder>();
        return services;
    }
}

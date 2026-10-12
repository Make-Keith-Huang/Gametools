using GameLingoAI.App.Overlay;
using GameLingoAI.App.Services;
using GameLingoAI.App.ViewModels;
using GameLingoAI.Core.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace GameLingoAI.App.Composition;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddGameLingoApplicationShell(this IServiceCollection services)
    {
        services.AddSingleton<IErrorReporter, ErrorReporter>();
        services.AddSingleton<SessionStatusViewModel>();
        services.AddSingleton<WindowSelectionViewModel>();
        services.AddSingleton<IRegionDraftStore>(_ => new JsonRegionDraftStore(RegionDraftPaths.GetDefaultPath()));
        services.AddSingleton<ILocalRegionPreview, WgcLocalRegionPreview>();
        services.AddSingleton<RegionEditorViewModel>();
        services.AddSingleton<OcrModelViewModel>();
        services.AddSingleton<ProviderConnectionViewModel>();
        services.AddSingleton<GlossaryViewModel>();
        services.AddSingleton<CacheViewModel>();
        services.AddSingleton<SessionDisplay>();
        services.AddSingleton<IOverlayTargetProbe, WindowsOverlayTargetProbe>();
        services.AddSingleton<OverlayController>();
        services.AddSingleton<OverlaySettingsViewModel>();
        services.AddSingleton<SessionViewModel>();
        services.AddSingleton<IStoppedSessionMaintenance>(provider => provider.GetRequiredService<SessionViewModel>());
        services.AddSingleton<DatabaseViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<IHotkeyService, GlobalHotkeyHost>();
        services.AddSingleton<HotkeysViewModel>();
        services.AddSingleton<ApplicationDiagnostics>();
        services.AddSingleton(provider => new DiagnosticsViewModel(
            token => provider.GetRequiredService<ApplicationDiagnostics>().SnapshotMetricsAsync(
                provider.GetRequiredService<SessionViewModel>().GetDiagnosticMetrics(), token),
            provider.GetRequiredService<ApplicationDiagnostics>().SnapshotRecent,
            provider.GetRequiredService<IErrorReporter>()));
        services.AddSingleton<ShellViewModel>();
        services.AddScoped<SessionContext>();
        services.AddTransient<PlaceholderPageViewModel>();
        services.AddTransient<MainWindow>();
        return services;
    }
}

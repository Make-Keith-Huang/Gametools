using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace GameLingoAI.Tests;

/// <summary>Named xUnit cases retain every existing assertion; process-wide Console/WPF state is serialized.</summary>
public sealed class P0Tests
{
    [Fact, Trait("Suite", "Default")]
    public Task Configuration() => ConfigurationTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task HotkeyConfiguration() => HotkeyConfigurationTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public void HotkeyOwnershipAndFailures() => HotkeyRegistrationTests.TestOwnershipAndFailures();

    [PlatformFact, Trait("Suite", "Platform")]
    public Task NativeHotkeyDispatch() => HotkeyRegistrationTests.TestNativeAsync();

    [Fact, Trait("Suite", "Default")]
    public Task HotkeysViewModel() => HotkeysViewModelTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task DiagnosticRedaction() => DiagnosticRedactionTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task BufferedDiagnostic() => BufferedDiagnosticTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task DiagnosticFile() => DiagnosticFileTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task ApplicationDiagnostics() => ApplicationDiagnosticsTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task DiagnosticMetrics() => DiagnosticMetricsTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task DiagnosticsViewModel() => DiagnosticsViewModelTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task SupportBundle() => SupportBundleTests.RunAsync();

    [PlatformFact, Trait("Suite", "Platform")]
    public Task UiShell() => UiShellTests.RunAsync();

    [PlatformFact, Trait("Suite", "Platform")]
    public Task WindowDiscovery() => WindowDiscoveryTests.RunAsync();

    [PlatformFact, Trait("Suite", "Platform")]
    public Task Capture() => CaptureTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task RegionEditor() => RegionEditorTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task Preprocessing() => PreprocessingTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    [Trait("Speed", "Slow")]
    public Task Ocr() => OcrTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task Stabilization() => StabilizationTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task TranslationFoundation() => TranslationFoundationTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task OpenAiCompatible() => OpenAiCompatibleTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task Ollama() => OllamaTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task CustomHttp() => CustomHttpTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task GlossaryPrompt() => GlossaryPromptTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task TranslationCache() => TranslationCacheTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    [Trait("Speed", "Slow")]
    public Task SessionPipeline() => SessionPipelineTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task Overlay() => OverlayTests.RunAsync();

    [PlatformFact, Trait("Suite", "Platform")]
    public Task OverlayWindow() => OverlayWindowTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task DatabaseMigration() => DatabaseMigrationTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task ProfilePersistence() => ProfilePersistenceTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task HistoryPersistence() => HistoryPersistenceTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task SqliteCache() => SqliteCacheTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task DatabaseMaintenance() => DatabaseMaintenanceTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task GlossaryPersistenceUi() => GlossaryPersistenceUiTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task HistoryRecording() => HistoryRecordingTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task DatabaseAdmin() => DatabaseAdminTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task SessionMaintenance() => SessionMaintenanceTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task ProfileJson() => ProfileJsonTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task ProfilesViewModel() => ProfilesViewModelTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public Task ProfileSessionSettings() => ProfileSessionSettingsTests.RunAsync();

    [Fact, Trait("Suite", "Default")]
    public void RegionCoordinates() => RegionCoordinateTests.Run();

    [NativeOcrFact, Trait("Suite", "OptIn")]
    public Task NativeOcrFixture() => OcrTests.RunOptInNativeIntegrationAsync();
}

/// <summary>Explicit environment gate protects interactive desktops even if a caller omits the settings filter.</summary>
public sealed class PlatformFactAttribute : FactAttribute
{
    public PlatformFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMELINGOAI_RUN_PLATFORM_TESTS") != "1")
        { Skip = "Requires an interactive Windows desktop; use tests/platform.runsettings."; }
    }
}

public sealed class NativeOcrFactAttribute : FactAttribute
{
    public NativeOcrFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMELINGOAI_RUN_NATIVE_OCR_TESTS") != "1")
        { Skip = "Requires an explicitly provisioned local model and licensed/synthetic fixture."; }
    }
}

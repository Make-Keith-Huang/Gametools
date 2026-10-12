using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;

internal static class ProfileSessionSettingsTests
{
    public static Task RunAsync()
    {
        var regions = new List<CaptureRegion> { new(Guid.NewGuid(), "Dialogue", new NormalizedRegion(0, 0, 1, 1), 0) };
        var profile = new GameProfile(ProfileId.New(), "Saved", "ja", "en", 12, regions, true,
            PreprocessingOptions.OutlinedSubtitle, new ProfileSettings
            {
                ProviderId = "local",
                Model = "profile-model",
                OcrLanguageModel = "japan",
                OcrMinimumConfidence = .8,
                Cache = new CacheOptions { MaximumRows = 42 },
                Overlay = new OverlayOptions { FontSize = 41 }
            });
        var local = new AppSettings
        {
            Providers = new[] { new ProviderOptions { Id = "local", Kind = ProviderKind.Ollama, Model = "default-model" } },
            Privacy = new PrivacyOptions { RemoteTextProcessingConsent = false }
        };
        var resolved = ProfileSessionSettings.Resolve(profile, local);
        Assert(resolved.Provider.Model == "profile-model" && resolved.Settings.Ocr.LanguageModel == "ja" &&
            resolved.Settings.Ocr.MinimumConfidence == .8 && resolved.Settings.Capture.FramesPerSecond == 12 &&
            resolved.Settings.Cache.MaximumRows == 42 && resolved.Settings.Overlay.FontSize == 41 &&
            resolved.Settings.Privacy.HistoryEnabled && !resolved.Settings.Privacy.RemoteTextProcessingConsent,
            "Profile choices did not resolve or silently enabled remote consent.");
        Assert(local.Providers[0].Model == "default-model" && !local.Privacy.HistoryEnabled, "Resolving mutated local configuration.");
        regions.Clear();
        Assert(resolved.Profile.Regions.Count == 1, "Resolved region snapshot retained caller mutation.");
        var snapshot = resolved.Profile;
        Throws(() => ProfileSessionSettings.Resolve(snapshot, local with { Providers = Array.Empty<ProviderOptions>() }));
        Throws(() => ProfileSessionSettings.Resolve(snapshot, local with { Providers = new[] { local.Providers[0], local.Providers[0] } }));
        var remote = local with
        {
            Providers = new[] { new ProviderOptions
        {
            Id = "local", Model = "configured", Kind = ProviderKind.OpenAiCompatible,
            BaseUri = new Uri("https://example.invalid/v1/"), EndpointDialect = OpenAiEndpointDialect.ChatCompletions,
            SecretReference = "not-implicitly-selected"
        } }
        };
        Throws(() => ProfileSessionSettings.Resolve(snapshot, remote));
        var configured = new GameProfile(snapshot.Id, snapshot.Name, "ja", "en", 12, snapshot.Regions, true,
            snapshot.Preprocessing, snapshot.Settings with { SecretReference = "explicit-local-reference" });
        var remoteResolved = ProfileSessionSettings.Resolve(configured, remote);
        Assert(remoteResolved.Provider.SecretReference == "explicit-local-reference" && !remoteResolved.Settings.Privacy.RemoteTextProcessingConsent,
            "Explicit local reference or consent boundary was lost.");
        Throws(() => ProfileSessionSettings.NormalizeOcrLanguage("unsupported"));
        Assert(ProfileSessionSettings.NormalizeOcrLanguage("ZH-TW") == "zh-hant", "OCR alias normalization failed.");
        var hinted = new GameProfile(snapshot.Id, snapshot.Name, "ja", "en", 12, snapshot.Regions, false,
            settings: snapshot.Settings with { ProcessHint = "fixture", WindowTitlePattern = "^Example" });
        var window = new CaptureWindow(new WindowIdentity(new WindowId(1), 2, 3), "Example game", "FIXTURE", new PixelBounds(0, 0, 100, 100), 96, false);
        var suggestions = ProfileWindowHints.Find(hinted, new[] { window }, CancellationToken.None);
        Assert(suggestions.Windows.Single() == window && !suggestions.LimitReached, "Metadata hint matching failed.");
        Assert(ProfileWindowHints.Find(snapshot, new[] { window }, CancellationToken.None).Windows.Count == 0,
            "Empty hints suggested arbitrary windows.");
        Assert(ProfileWindowHints.Find(hinted, Enumerable.Repeat(window, 300).ToArray(), CancellationToken.None) is { LimitReached: true, Windows.Count: 20 },
            "Window hint workload/result bounds failed.");
        try
        { ProfileWindowHints.Find(hinted, new[] { window }, new CancellationToken(true)); throw new InvalidOperationException("Canceled hint matching ran."); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Profile session settings: local provider resolution, settings snapshot, OCR aliases, explicit credential reference and unchanged consent passed.");
        return Task.CompletedTask;
    }
    private static void Throws(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid profile session configuration was accepted."); }
    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

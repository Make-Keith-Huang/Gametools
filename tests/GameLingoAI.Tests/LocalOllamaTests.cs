using System.Diagnostics;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Translation;
using GameLingoAI.Infrastructure.Translation.Ollama;
using Xunit;
using Xunit.Abstractions;

namespace GameLingoAI.Tests;

public sealed class LocalOllamaTests(ITestOutputHelper output)
{
    [LocalOllamaFact, Trait("Suite", "OptIn")]
    public async Task InstalledLocalModelTranslatesSyntheticSubtitles()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var provider = new OllamaProvider(new ProviderOptions
        {
            Id = "local-acceptance",
            Kind = ProviderKind.Ollama,
            BaseUri = new Uri("http://127.0.0.1:11434/"),
            Model = "qwen2.5:3b",
            TimeoutSeconds = 60,
            MaximumOutputTokens = 96
        }, new DeniedRemoteConsent(), new SystemClock(), new SystemTranslationDelay(), new RandomRetryJitter());
        var discovered = await provider.DiscoverModelsAsync(timeout.Token);
        Assert.True(discovered.IsSuccess, "Local model discovery failed; do not download automatically.");
        Assert.Equal(OllamaModelState.Ready, provider.ModelState);
        Assert.False(provider.Capabilities.RequiresNetwork);

        var sequence = 0;
        foreach (var fixture in new[]
        {
            (Text: "Hello, world!", Required: new[] { "你好", "世界" }),
            (Text: "The door is locked.", Required: new[] { "门", "锁" })
        })
        {
            var metadata = new PipelineMetadata(SessionId.New(), new SequenceNumber(++sequence),
                DateTimeOffset.UtcNow, CorrelationId.New());
            var request = new TranslationRequest(metadata, "en", "zh-Hans", fixture.Text, null, [], "qwen2.5:3b");
            var clock = Stopwatch.StartNew();
            var result = await provider.TranslateAsync(request, timeout.Token);
            Assert.True(result.IsSuccess, "Local synthetic translation failed.");
            var translated = result.Value!;
            output.WriteLine($"Synthetic case {sequence}: {translated.TranslatedText}; elapsedMs={clock.ElapsedMilliseconds}");
            Assert.Equal(metadata, translated.Metadata);
            Assert.InRange(translated.TranslatedText.Length, 1, 1000);
            Assert.Contains(translated.TranslatedText, character => character is >= '\u4e00' and <= '\u9fff');
            Assert.All(fixture.Required, term => Assert.Contains(term, translated.TranslatedText, StringComparison.Ordinal));
            Assert.DoesNotContain("```", translated.TranslatedText, StringComparison.Ordinal);
            Assert.True(translated.Usage?.IsValid);
        }
    }

    private sealed class DeniedRemoteConsent : IRemoteTextConsent
    {
        public ValueTask<bool> IsGrantedAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}

public sealed class LocalOllamaFactAttribute : FactAttribute
{
    public LocalOllamaFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMELINGOAI_RUN_LOCAL_OLLAMA_TESTS") != "1")
        { Skip = "Explicit local Ollama acceptance opt-in is required."; }
    }
}

using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

internal static class ProfilePersistenceTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-ProfileTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profiles.db");
            using var database = new SqliteDatabase(path);
            var clock = new Clock();
            var profiles = new SqliteProfileRepository(database, clock);
            var glossary = new SqliteGlossaryRepository(database, clock);
            var region = new CaptureRegion(Guid.NewGuid(), "Dialogue '); DROP TABLE game_profiles; --",
                new NormalizedRegion(.1, .7, .8, .2), 2);
            var profile = new GameProfile(ProfileId.New(), "日本語 '); DROP TABLE game_profiles; --", "ja", "zh-Hans", 12,
                new[] { region }, false, PreprocessingOptions.OutlinedSubtitle, new ProfileSettings
                {
                    ProcessHint = "example-game",
                    WindowTitlePattern = "Example.*",
                    ProviderId = "ollama",
                    Model = "test-model",
                    SecretReference = "credential.lookup.only",
                    OcrLanguageModel = "japan",
                    OcrMinimumConfidence = .8,
                    CaptureQueueCapacity = 1,
                    RetainedLogFileCount = 3,
                    Overlay = new OverlayOptions
                    {
                        FontSize = 42,
                        Bold = false,
                        Foreground = "#FFAA00",
                        OutlineColor = "#222222",
                        OutlineThickness = 2,
                        MaximumLines = 4,
                        BackgroundOpacity = .3,
                        HorizontalOffsetDips = 20,
                        VerticalOffsetDips = -50
                    },
                    Cache = new CacheOptions
                    {
                        MaximumRows = 42,
                        MaximumMegabytes = 12,
                        MaximumAgeDays = 7,
                        PruneBatchSize = 5,
                        MaximumInFlightRequests = 2,
                        MaximumWaitersPerKey = 3
                    },
                    Stabilization = new StabilizationOptions { RequiredObservations = 3, SimilarityThreshold = .9 }
                });
            await profiles.SaveAsync(profile, CancellationToken.None);
            foreach (var invalidSettings in new[]
            {
                profile.Settings with { OcrMinimumConfidence = double.NaN },
                profile.Settings with { WindowTitlePattern = "[" },
                profile.Settings with { ProcessHint = @"C:\private\game.exe" },
                profile.Settings with { SecretReference = "Bearer not-a-reference" },
                profile.Settings with { Overlay = new OverlayOptions { FontSize = -1 } },
                profile.Settings with { Cache = new CacheOptions { MaximumRows = -1 } },
                profile.Settings with { Overlay = null! }
            })
            {
                try
                {
                    await profiles.SaveAsync(new GameProfile(profile.Id, profile.Name, "ja", "en", 8,
                        profile.Regions, false, settings: invalidSettings), CancellationToken.None);
                    throw new InvalidOperationException("Invalid detailed settings were persisted.");
                }
                catch (ArgumentException) { }
            }
            foreach (var invalid in new[]
            {
                new GameProfile(profile.Id, new string('x', 201), "en", "ja", 8, new[] { region }, false),
                new GameProfile(profile.Id, "invalid", "en", "ja", 8, new[] { region, region }, false),
                new GameProfile(profile.Id, "invalid", "en", "ja", 8,
                    new[] { new CaptureRegion(Guid.NewGuid(), "empty geometry", default, 0) }, false),
                new GameProfile(profile.Id, "invalid", "en", "ja", 8, new[] { region }, false,
                    PreprocessingOptions.PlainSubtitle with { Scale = double.NaN })
            })
            {
                try
                {
                    await profiles.SaveAsync(invalid, CancellationToken.None);
                    throw new InvalidOperationException("Invalid profile was persisted.");
                }
                catch (ArgumentException) { }
            }
            var loaded = await profiles.GetAsync(profile.Id, CancellationToken.None);
            Assert(loaded is not null && loaded.Name == profile.Name && loaded.SourceLanguage == "ja" &&
                loaded.TargetLanguage == "zh-Hans" && loaded.CaptureFramesPerSecond == 12 && !loaded.HistoryEnabled &&
                loaded.Preprocessing == profile.Preprocessing && loaded.Settings == profile.Settings &&
                loaded.Regions.SequenceEqual(profile.Regions), "Profile/region/settings round-trip failed.");

            using (var reopened = new SqliteDatabase(path))
            {
                var persisted = await new SqliteProfileRepository(reopened, clock).GetAsync(profile.Id, CancellationToken.None);
                Assert(persisted?.Name == profile.Name && persisted.Settings == profile.Settings,
                    "Profile settings did not survive repository/database recreation.");
            }
            var global = new GlossaryEntry(Guid.NewGuid(), null, "ja", "zh-Hans", "勇者", "英雄");
            var scoped = new GlossaryEntry(Guid.NewGuid(), profile.Id, "ja", "zh-Hans", "'); DROP TABLE game_profiles; --", "字面文本", true, false);
            await glossary.SaveAsync(global, CancellationToken.None);
            await glossary.SaveAsync(scoped, CancellationToken.None);
            Assert((await glossary.ListAsync(CancellationToken.None)).Contains(scoped), "Scoped glossary round-trip failed.");
            await MustFailAsync(() => glossary.SaveAsync(new GlossaryEntry(Guid.NewGuid(), ProfileId.New(), "ja", "en", "absent", "missing"), CancellationToken.None));
            Assert((await glossary.ListAsync(CancellationToken.None)).Count == 2, "Invalid glossary FK left data behind.");

            // A conflicting region key fails only after profile UPDATE and old-region DELETE.
            // The original profile and region must both survive the rollback.
            var otherRegion = new CaptureRegion(Guid.NewGuid(), "Other region", new NormalizedRegion(0, 0, 1, 1), 0);
            var other = new GameProfile(ProfileId.New(), "Other", "en", "ja", 8, new[] { otherRegion }, false);
            await profiles.SaveAsync(other, CancellationToken.None);
            var conflicting = new GameProfile(profile.Id, "Must not replace", "en", "ja", 8, new[] { otherRegion }, true);
            await MustFailAsync(() => profiles.SaveAsync(conflicting, CancellationToken.None));
            loaded = await profiles.GetAsync(profile.Id, CancellationToken.None);
            Assert(loaded?.Name == profile.Name && loaded.Settings == profile.Settings && loaded.Regions.Single().Id == region.Id && !loaded.HistoryEnabled,
                "Failed aggregate update changed last-known-good data.");

            var operations = Enumerable.Range(0, 12).Select(async index =>
            {
                var item = new GameProfile(ProfileId.New(), "Parallel " + index, "en", "ja", 8,
                    new[] { new CaptureRegion(Guid.NewGuid(), "Dialogue", new NormalizedRegion(0, 0, 1, 1), 0) }, false);
                await profiles.SaveAsync(item, CancellationToken.None);
                Assert((await profiles.GetAsync(item.Id, CancellationToken.None))?.Name == item.Name, "Concurrent save/read failed.");
            });
            await Task.WhenAll(operations);
            Assert((await profiles.ListAsync(CancellationToken.None)).Count == 14, "Concurrent writes lost profiles.");
            try
            {
                await profiles.DeleteAsync(profile.Id, new CancellationToken(true));
                throw new InvalidOperationException("Canceled delete succeeded.");
            }
            catch (OperationCanceledException) { }
            Assert(await profiles.GetAsync(profile.Id, CancellationToken.None) is not null, "Canceled delete changed data.");

            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, ForeignKeys = true }.ToString()))
            {
                connection.Open();
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO translation_history(session_id,profile_id,source_text,translated_text,observed_utc,latency_ms)
                    VALUES('synthetic',$profile,'source','translated','2026-01-01',1);
                    """;
                insert.Parameters.AddWithValue("$profile", profile.Id.Value.ToString());
                insert.ExecuteNonQuery();
            }
            await profiles.DeleteAsync(profile.Id, CancellationToken.None);
            Assert(await profiles.GetAsync(profile.Id, CancellationToken.None) is null, "Profile delete failed.");
            var remaining = await glossary.ListAsync(CancellationToken.None);
            Assert(remaining.Count == 1 && remaining[0] == global, "Profile glossary did not cascade or global glossary was deleted.");
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
            {
                connection.Open();
                using var check = connection.CreateCommand();
                check.CommandText = "SELECT COUNT(*) FROM capture_regions WHERE profile_id=$profile;";
                check.Parameters.AddWithValue("$profile", profile.Id.Value.ToString());
                Assert((long)check.ExecuteScalar()! == 0, "Regions did not cascade.");
                check.CommandText = "SELECT COUNT(*) FROM translation_history WHERE profile_id IS NULL;";
                Assert((long)check.ExecuteScalar()! == 1, "History must be retained with profile_id set null.");
            }
            await glossary.DeleteAsync(global.Id, CancellationToken.None);
            Assert((await glossary.ListAsync(CancellationToken.None)).Count == 0, "Glossary delete failed.");
            Console.WriteLine("SQLite profiles/glossary: round-trip, persistence, injection literals, atomic rollback, cancellation, concurrent access and FK cascades passed.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static async Task MustFailAsync(Func<Task> action)
    {
        try
        { await action(); }
        catch (DatabaseStorageException) { return; }
        throw new InvalidOperationException("Expected a classified storage failure.");
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }

    private sealed class Clock : IClock
    { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
}

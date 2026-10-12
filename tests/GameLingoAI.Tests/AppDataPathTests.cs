using GameLingoAI.App.Services;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Infrastructure.Configuration;
using GameLingoAI.Infrastructure.Persistence;

internal static class AppDataPathTests
{
    public static async Task RunAsync(string testRoot)
    {
        var previous = Environment.GetEnvironmentVariable(AppDataPaths.EnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AppDataPaths.EnvironmentVariable, null);
            Assert(AppDataPaths.GetRoot() == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameLingoAI"),
                "An unset override must preserve existing installations.");
            var root = Path.Combine(testRoot, "独立 data");
            Environment.SetEnvironmentVariable(AppDataPaths.EnvironmentVariable, root);
            Assert(AppDataPaths.Settings == Path.Combine(root, "settings.json") &&
                AppDataPaths.OcrModels == Path.Combine(root, "models", "ocr") &&
                AppDataPaths.Database == Path.Combine(root, "gamelingo.db") &&
                AppDataPaths.Logs == Path.Combine(root, "logs") &&
                RegionDraftPaths.GetDefaultPath() == Path.Combine(root, "region-draft.json"), "All owned data must share the selected root.");
            Assert(!Directory.Exists(root), "Resolving paths must not create directories.");
            await new JsonConfigurationStore(AppDataPaths.Settings).SaveAsync(new AppSettings(), CancellationToken.None);
            using (var database = new SqliteDatabase())
            {
                Assert((await database.InitializeAsync(CancellationToken.None)).IsWritable, "Relocated default database must initialize.");
            }
            Assert(File.Exists(AppDataPaths.Settings) && File.Exists(AppDataPaths.Database), "Real writes must reach the selected directory.");
            foreach (var invalid in new[] { "relative", " ", @"C:relative", @"\\server\share\private", Path.GetPathRoot(testRoot)!, AppDataPaths.Settings })
            {
                Environment.SetEnvironmentVariable(AppDataPaths.EnvironmentVariable, invalid);
                try
                { _ = AppDataPaths.GetRoot(); }
                catch (ArgumentException) { continue; }
                throw new InvalidOperationException("Invalid data root was accepted or silently replaced.");
            }
        }
        finally { Environment.SetEnvironmentVariable(AppDataPaths.EnvironmentVariable, previous); }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException(message); } }
}

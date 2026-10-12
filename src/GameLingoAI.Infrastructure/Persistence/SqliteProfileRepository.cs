using System.Globalization;
using System.Text.Json;
using GameLingoAI.Core.Abstractions;
using GameLingoAI.Core.Configuration;
using GameLingoAI.Core.Models;
using GameLingoAI.Core.Services;
using Microsoft.Data.Sqlite;

namespace GameLingoAI.Infrastructure.Persistence;

/// <summary>A profile and its regions form one transactional aggregate.</summary>
public sealed class SqliteProfileRepository(SqliteDatabase database, IClock clock) : IProfileRepository
{
    public const int MaximumProfiles = 1_000;
    public const int MaximumRegions = 32;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 8 };

    public Task<GameProfile?> GetAsync(ProfileId id, CancellationToken cancellationToken)
    {
        CheckId(id);
        return database.RunAsync(false, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            return Read(connection, transaction, id, token);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken cancellationToken) =>
        database.RunAsync<IReadOnlyList<GameProfile>>(false, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            var ids = new List<ProfileId>();
            using (var command = SqliteDatabase.Command(connection, transaction,
                "SELECT id FROM game_profiles ORDER BY name,id LIMIT $limit;", ("$limit", MaximumProfiles + 1)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (!Guid.TryParse(reader.GetString(0), out var id) || id == Guid.Empty)
                    { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); }
                    ids.Add(new ProfileId(id));
                }
            }
            if (ids.Count > MaximumProfiles)
            { throw new DatabaseStorageException(DatabaseFailureKind.Incompatible); }
            return ids.Select(id => Read(connection, transaction, id, token)!).ToArray();
        }, cancellationToken);

    public Task SaveAsync(GameProfile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        // Snapshot caller-owned lists before scheduling work so later edits cannot split a save.
        var snapshot = new GameProfile(profile.Id, profile.Name, profile.SourceLanguage, profile.TargetLanguage,
            profile.CaptureFramesPerSecond, profile.Regions.Take(MaximumRegions + 1).ToArray(), profile.HistoryEnabled, profile.Preprocessing, profile.Settings);
        GameProfileValidator.Validate(snapshot);
        var preprocessing = JsonSerializer.Serialize(snapshot.Preprocessing, JsonOptions);
        var settings = JsonSerializer.Serialize(snapshot.Settings, JsonOptions);
        var overlay = JsonSerializer.Serialize(snapshot.Settings.Overlay, JsonOptions);
        return database.RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            var exists = Convert.ToInt64(SqliteDatabase.Scalar(connection, transaction,
                "SELECT COUNT(*) FROM game_profiles WHERE id=$id;", ("$id", snapshot.Id.Value.ToString())), CultureInfo.InvariantCulture) != 0;
            if (!exists && Convert.ToInt64(SqliteDatabase.Scalar(connection, transaction,
                "SELECT COUNT(*) FROM game_profiles;"), CultureInfo.InvariantCulture) >= MaximumProfiles)
            { throw new InvalidOperationException("The profile limit has been reached."); }
            var utc = clock.UtcNow.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            SqliteDatabase.Execute(connection, transaction, """
                INSERT INTO game_profiles(id,name,source_language,target_language,provider_id,capture_fps,
                  overlay_json,created_utc,updated_utc,preprocessing_json,history_enabled,settings_json,process_hint,window_title_pattern)
                VALUES($id,$name,$source,$target,$provider,$fps,$overlay,$utc,$utc,$preprocessing,$history,$settings,$process,$title)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,source_language=excluded.source_language,
                  target_language=excluded.target_language,capture_fps=excluded.capture_fps,
                  updated_utc=excluded.updated_utc,preprocessing_json=excluded.preprocessing_json,history_enabled=excluded.history_enabled,
                  provider_id=excluded.provider_id,overlay_json=excluded.overlay_json,settings_json=excluded.settings_json,
                  process_hint=excluded.process_hint,window_title_pattern=excluded.window_title_pattern;
                """, ("$id", snapshot.Id.Value.ToString()), ("$name", snapshot.Name), ("$source", snapshot.SourceLanguage),
                ("$target", snapshot.TargetLanguage), ("$fps", snapshot.CaptureFramesPerSecond), ("$utc", utc),
                ("$preprocessing", preprocessing), ("$history", snapshot.HistoryEnabled), ("$settings", settings),
                ("$provider", snapshot.Settings.ProviderId), ("$overlay", overlay),
                ("$process", snapshot.Settings.ProcessHint), ("$title", snapshot.Settings.WindowTitlePattern));
            SqliteDatabase.Execute(connection, transaction, "DELETE FROM capture_regions WHERE profile_id=$id;", ("$id", snapshot.Id.Value.ToString()));
            foreach (var region in snapshot.Regions)
            {
                token.ThrowIfCancellationRequested();
                SqliteDatabase.Execute(connection, transaction, """
                    INSERT INTO capture_regions(id,profile_id,name,x,y,width,height,sort_order)
                    VALUES($id,$profile,$name,$x,$y,$width,$height,$order);
                    """, ("$id", region.Id.ToString()), ("$profile", snapshot.Id.Value.ToString()), ("$name", region.Name),
                    ("$x", region.Bounds.X), ("$y", region.Bounds.Y), ("$width", region.Bounds.Width),
                    ("$height", region.Bounds.Height), ("$order", region.SortOrder));
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    public Task DeleteAsync(ProfileId id, CancellationToken cancellationToken)
    {
        CheckId(id);
        return database.RunAsync(true, (connection, token) =>
        {
            using var transaction = connection.BeginTransaction();
            SqliteDatabase.Execute(connection, transaction, "DELETE FROM game_profiles WHERE id=$id;", ("$id", id.Value.ToString()));
            token.ThrowIfCancellationRequested();
            transaction.Commit();
            return true;
        }, cancellationToken);
    }

    private static GameProfile? Read(SqliteConnection connection, SqliteTransaction transaction, ProfileId id, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            string name, source, target, json;
            ProfileSettings settings;
            int fps;
            bool history;
            using (var command = SqliteDatabase.Command(connection, transaction, """
                SELECT name,source_language,target_language,capture_fps,history_enabled,preprocessing_json,
                  settings_json,process_hint,window_title_pattern,provider_id,overlay_json
                FROM game_profiles WHERE id=$id;
                """, ("$id", id.Value.ToString())))
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                { return null; }
                name = reader.GetString(0);
                source = reader.GetString(1);
                target = reader.GetString(2);
                fps = reader.GetInt32(3);
                history = reader.GetBoolean(4);
                json = reader.GetString(5);
                if (json.Length > 16_384)
                { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); }
                var settingsJson = reader.GetString(6);
                var overlayJson = reader.GetString(10);
                if (settingsJson.Length > 16_384 || overlayJson.Length > 16_384)
                { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); }
                settings = (JsonSerializer.Deserialize<ProfileSettings>(settingsJson, JsonOptions) ?? throw new JsonException()) with
                {
                    ProcessHint = reader.IsDBNull(7) ? null : reader.GetString(7),
                    WindowTitlePattern = reader.IsDBNull(8) ? null : reader.GetString(8),
                    ProviderId = reader.GetString(9),
                    Overlay = JsonSerializer.Deserialize<OverlayOptions>(overlayJson, JsonOptions) ?? throw new JsonException()
                };
            }
            var regions = new List<CaptureRegion>();
            using (var command = SqliteDatabase.Command(connection, transaction, """
                SELECT id,name,x,y,width,height,sort_order FROM capture_regions
                WHERE profile_id=$id ORDER BY sort_order,id LIMIT $limit;
                """, ("$id", id.Value.ToString()), ("$limit", MaximumRegions + 1)))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    regions.Add(new CaptureRegion(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                        new NormalizedRegion(reader.GetDouble(2), reader.GetDouble(3), reader.GetDouble(4), reader.GetDouble(5)), reader.GetInt32(6)));
                }
            }
            var profile = new GameProfile(id, name, source, target, fps, regions.AsReadOnly(), history,
                JsonSerializer.Deserialize<PreprocessingOptions>(json, JsonOptions) ?? throw new JsonException(), settings);
            GameProfileValidator.Validate(profile);
            return profile;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or JsonException or InvalidOperationException or OverflowException)
        { throw new DatabaseStorageException(DatabaseFailureKind.Corrupt); }
    }

    private static void CheckId(ProfileId id) => ArgumentOutOfRangeException.ThrowIfEqual(id.Value, Guid.Empty);
}

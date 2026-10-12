namespace GameLingoAI.Infrastructure.Persistence;

/// <summary>Trusted application SQL only; never construct migrations from imported profile data.</summary>
public sealed record DatabaseMigration(int Version, string Name, string UpSql, string CompatibilitySql,
    bool RequiresBackup = false);

public static class DatabaseMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } = Array.AsReadOnly(new[]
    {
        new DatabaseMigration(1, "initial-v1", """
            CREATE TABLE game_profiles (
              id TEXT PRIMARY KEY NOT NULL, name TEXT NOT NULL,
              process_hint TEXT NULL, window_title_pattern TEXT NULL,
              source_language TEXT NOT NULL, target_language TEXT NOT NULL, provider_id TEXT NOT NULL,
              capture_fps INTEGER NOT NULL DEFAULT 8 CHECK(capture_fps BETWEEN 1 AND 30),
              overlay_json TEXT NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL);
            CREATE TABLE capture_regions (
              id TEXT PRIMARY KEY NOT NULL,
              profile_id TEXT NOT NULL REFERENCES game_profiles(id) ON DELETE CASCADE,
              name TEXT NOT NULL, x REAL NOT NULL CHECK(x BETWEEN 0 AND 1),
              y REAL NOT NULL CHECK(y BETWEEN 0 AND 1),
              width REAL NOT NULL CHECK(width > 0 AND width <= 1 AND x + width <= 1),
              height REAL NOT NULL CHECK(height > 0 AND height <= 1 AND y + height <= 1),
              sort_order INTEGER NOT NULL DEFAULT 0 CHECK(sort_order >= 0));
            CREATE TABLE glossary_entries (
              id TEXT PRIMARY KEY NOT NULL,
              profile_id TEXT NULL REFERENCES game_profiles(id) ON DELETE CASCADE,
              source_language TEXT NOT NULL, target_language TEXT NOT NULL,
              source_term TEXT NOT NULL, target_term TEXT NOT NULL,
              case_sensitive INTEGER NOT NULL DEFAULT 0 CHECK(case_sensitive IN (0,1)),
              enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)), updated_utc TEXT NOT NULL);
            CREATE TABLE translation_cache (
              cache_key TEXT PRIMARY KEY NOT NULL, provider_id TEXT NOT NULL, model TEXT NOT NULL,
              source_language TEXT NOT NULL, target_language TEXT NOT NULL,
              glossary_revision TEXT NOT NULL, prompt_revision TEXT NOT NULL,
              source_hash TEXT NOT NULL, source_text TEXT NOT NULL, translated_text TEXT NOT NULL,
              created_utc TEXT NOT NULL, last_used_utc TEXT NOT NULL,
              hit_count INTEGER NOT NULL DEFAULT 0 CHECK(hit_count >= 0),
              size_bytes INTEGER NOT NULL CHECK(size_bytes > 0));
            CREATE TABLE cache_state (
              id INTEGER PRIMARY KEY CHECK(id = 1), generation INTEGER NOT NULL CHECK(generation >= 0));
            INSERT INTO cache_state(id, generation) VALUES(1,0);
            CREATE TABLE translation_history (
              id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL,
              profile_id TEXT NULL REFERENCES game_profiles(id) ON DELETE SET NULL,
              source_text TEXT NOT NULL, translated_text TEXT NOT NULL, observed_utc TEXT NOT NULL,
              latency_ms INTEGER NOT NULL CHECK(latency_ms >= 0));
            CREATE INDEX ix_regions_profile ON capture_regions(profile_id, sort_order);
            CREATE INDEX ix_glossary_lookup ON glossary_entries(profile_id, source_language, target_language, enabled);
            CREATE INDEX ix_cache_source ON translation_cache(source_hash, source_language, target_language);
            CREATE INDEX ix_cache_lru ON translation_cache(last_used_utc);
            CREATE INDEX ix_cache_age ON translation_cache(created_utc);
            CREATE INDEX ix_history_profile_time ON translation_history(profile_id, observed_utc DESC);
            """, """
            SELECT COUNT(*) = 6 FROM sqlite_master WHERE type='table' AND name IN
              ('game_profiles','capture_regions','glossary_entries','translation_cache','cache_state','translation_history');
            """),
        new DatabaseMigration(2, "profile-preprocessing-privacy", """
            ALTER TABLE game_profiles ADD COLUMN preprocessing_json TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE game_profiles ADD COLUMN history_enabled INTEGER NOT NULL DEFAULT 0 CHECK(history_enabled IN (0,1));
            """, "SELECT COUNT(*) = 2 FROM pragma_table_info('game_profiles') WHERE name IN ('preprocessing_json','history_enabled');"),
        new DatabaseMigration(3, "history-retention-index",
            "CREATE INDEX ix_history_age ON translation_history(observed_utc,id);",
            "SELECT COUNT(*) = 1 FROM sqlite_master WHERE type='index' AND name='ix_history_age';"),
        new DatabaseMigration(4, "cache-policy-accounting-order", """
            ALTER TABLE cache_state ADD COLUMN policy_json TEXT NOT NULL DEFAULT '{}';
            ALTER TABLE cache_state ADD COLUMN row_count INTEGER NOT NULL DEFAULT 0 CHECK(row_count >= 0);
            ALTER TABLE cache_state ADD COLUMN total_bytes INTEGER NOT NULL DEFAULT 0 CHECK(total_bytes >= 0);
            ALTER TABLE cache_state ADD COLUMN access_sequence INTEGER NOT NULL DEFAULT 0 CHECK(access_sequence >= 0);
            ALTER TABLE translation_cache ADD COLUMN access_order INTEGER NOT NULL DEFAULT 0;
            UPDATE translation_cache SET access_order=rowid;
            UPDATE cache_state SET row_count=(SELECT COUNT(*) FROM translation_cache),
              total_bytes=(SELECT COALESCE(SUM(size_bytes),0) FROM translation_cache),
              access_sequence=(SELECT COALESCE(MAX(access_order),0) FROM translation_cache) WHERE id=1;
            CREATE INDEX ix_cache_access ON translation_cache(access_order);
            CREATE TRIGGER cache_insert_count AFTER INSERT ON translation_cache BEGIN
              UPDATE cache_state SET row_count=row_count+1,total_bytes=total_bytes+new.size_bytes WHERE id=1;
            END;
            CREATE TRIGGER cache_delete_count AFTER DELETE ON translation_cache BEGIN
              UPDATE cache_state SET row_count=row_count-1,total_bytes=total_bytes-old.size_bytes WHERE id=1;
            END;
            CREATE TRIGGER cache_update_count AFTER UPDATE OF size_bytes ON translation_cache BEGIN
              UPDATE cache_state SET total_bytes=total_bytes-old.size_bytes+new.size_bytes WHERE id=1;
            END;
            """, "SELECT COUNT(*) = 3 FROM sqlite_master WHERE type='trigger' AND name IN ('cache_insert_count','cache_delete_count','cache_update_count');"),
        new DatabaseMigration(5, "profile-settings",
            "ALTER TABLE game_profiles ADD COLUMN settings_json TEXT NOT NULL DEFAULT '{}';",
            "SELECT COUNT(*) = 1 FROM pragma_table_info('game_profiles') WHERE name='settings_json';")
    });
}

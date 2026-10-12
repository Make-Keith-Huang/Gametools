# Database Design

## Principles

SQLite stores user configuration and optional text data under `%LOCALAPPDATA%/GameLingoAI`. Enable foreign keys and WAL mode. Run numbered migrations transactionally at startup. API keys never enter the database.

## Schema (target V1)

```sql
CREATE TABLE schema_migrations (
  version INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  applied_utc TEXT NOT NULL
);

CREATE TABLE game_profiles (
  id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  process_hint TEXT NULL,
  window_title_pattern TEXT NULL,
  source_language TEXT NOT NULL,
  target_language TEXT NOT NULL,
  provider_id TEXT NOT NULL,
  capture_fps INTEGER NOT NULL DEFAULT 8 CHECK(capture_fps BETWEEN 1 AND 30),
  overlay_json TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  updated_utc TEXT NOT NULL
);

CREATE TABLE capture_regions (
  id TEXT PRIMARY KEY,
  profile_id TEXT NOT NULL REFERENCES game_profiles(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  x REAL NOT NULL CHECK(x BETWEEN 0 AND 1),
  y REAL NOT NULL CHECK(y BETWEEN 0 AND 1),
  width REAL NOT NULL CHECK(width > 0 AND width <= 1),
  height REAL NOT NULL CHECK(height > 0 AND height <= 1),
  sort_order INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE glossary_entries (
  id TEXT PRIMARY KEY,
  profile_id TEXT NULL REFERENCES game_profiles(id) ON DELETE CASCADE,
  source_language TEXT NOT NULL,
  target_language TEXT NOT NULL,
  source_term TEXT NOT NULL,
  target_term TEXT NOT NULL,
  case_sensitive INTEGER NOT NULL DEFAULT 0,
  enabled INTEGER NOT NULL DEFAULT 1,
  updated_utc TEXT NOT NULL
);

CREATE TABLE translation_cache (
  cache_key TEXT PRIMARY KEY,
  provider_id TEXT NOT NULL,
  model TEXT NOT NULL,
  source_language TEXT NOT NULL,
  target_language TEXT NOT NULL,
  glossary_revision TEXT NOT NULL,
  prompt_revision TEXT NOT NULL,
  source_hash TEXT NOT NULL,
  source_text TEXT NOT NULL,
  translated_text TEXT NOT NULL,
  created_utc TEXT NOT NULL,
  last_used_utc TEXT NOT NULL,
  hit_count INTEGER NOT NULL DEFAULT 0,
  size_bytes INTEGER NOT NULL CHECK(size_bytes > 0)
);

CREATE TABLE cache_state (
  id INTEGER PRIMARY KEY CHECK(id = 1),
  generation INTEGER NOT NULL CHECK(generation >= 0)
);
INSERT INTO cache_state(id, generation) VALUES (1, 0);

CREATE TABLE translation_history (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id TEXT NOT NULL,
  profile_id TEXT NULL REFERENCES game_profiles(id) ON DELETE SET NULL,
  source_text TEXT NOT NULL,
  translated_text TEXT NOT NULL,
  observed_utc TEXT NOT NULL,
  latency_ms INTEGER NOT NULL
);

CREATE INDEX ix_regions_profile ON capture_regions(profile_id, sort_order);
CREATE INDEX ix_glossary_lookup ON glossary_entries(profile_id, source_language, target_language, enabled);
CREATE INDEX ix_cache_source ON translation_cache(source_hash, source_language, target_language);
CREATE INDEX ix_cache_lru ON translation_cache(last_used_utc);
CREATE INDEX ix_cache_age ON translation_cache(created_utc);
CREATE INDEX ix_history_profile_time ON translation_history(profile_id, observed_utc DESC);
CREATE INDEX ix_history_age ON translation_history(observed_utc, id);
```

## Cache key

SHA-256 over a canonical UTF-8 tuple: schema version, provider ID, configured model, provider-configuration fingerprint, source/target language, glossary revision, selected-term hash, prompt-template revision (including preservation options), normalized source text, and ordered normalized context. Normalize Unicode NFC and line endings only; preserve case, spacing and tags. Never use `GetHashCode()` for persisted keys. See `docs/TRANSLATION_CACHE.md` for the canonical version-1 fixture and contract.

## Retention

- History is disabled by default; when enabled, default retention is 30 days.
- Cache default cap is 100,000 rows or 500 MiB of accounted payload, pruned least-recently-used in small batches.
- Cache entries expire after 30 days from creation by default, independently of history. `size_bytes` is logical payload accounting; SQLite file-space reclamation is a separate maintenance concern.
- Cache-only clear increments `cache_state.generation` in the same transaction as deletion. Upsert batches must check their pre-translation expected generation in the write transaction, so a late response cannot repopulate a cleared cache.
- Profile deletion cascades regions and profile glossary entries. Cache is global and survives profile deletion.
- “Clear private data” removes history/cache and vacuums only after confirmation while the pipeline is stopped.

## Migration policy

Each migration has `Up`, a tested compatibility check, and a backup requirement flag. Copy the database before any destructive migration. On failure, roll back and start in read-only recovery mode with an actionable message.

## TASK-021 implementation

The foundation uses centrally pinned `Microsoft.Data.Sqlite` 8.0.30 (MIT), compatible with the existing .NET 8 application. Existing OCR package versions are unchanged. See the [package metadata](https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.30).

`Infrastructure/Persistence/SqliteDatabase` opens short-lived, non-pooled operation connections with foreign keys and a one-second busy timeout. An idle read-only connection retains WAL state between operations, without holding a read transaction; it is disposed after operation draining. Each instance admits at most 32 pending operations and runs one operation at a time on a worker thread. SQLite arbitrates writers across instances. This avoids executing SQLite's synchronous I/O on the WPF thread; its `Async` methods do not provide asynchronous I/O ([Microsoft documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async)). Callers must drain operations before disposing the database. Cancellation is checked before admission and between migration steps and before commit; a currently executing native statement/backup completes before cancellation can be observed.

Migration 1 creates the documented tables and indexes, additionally enforcing contained regions, nonnegative sort order/latency/hit counts and boolean values. Migration 2 adds `game_profiles.preprocessing_json` (default `{}`) and `history_enabled` (default `0`) so existing Core profiles round-trip without silently enabling history. Pending migrations and their records share one transaction. Existing migration names/order and compatibility probes are checked; unknown/unversioned databases are not silently adopted. Newer schema is detected before WAL/schema mutation. Integrity, busy, disk-full, unavailable and incompatible failures cross the boundary as safe typed errors without the original SQL/path/text.

Before a migration marked destructive, an SQLite online backup includes the committed WAL snapshot, rather than copying only the main file. Backup files use unique `.before-migration-<id>.db` names beside the source and are retained. A failed migration rolls back its SQL and version records. An explicit `ExportRecoveryCopyAsync` can copy a readable database (including a newer schema) without migrating or writing the source; it requires user action and refuses to overwrite an existing destination. Copies can contain private history/cache text, so users must treat them as local private files. A corrupt database may not be recoverable with the online backup API; no automatic deletion or reset is attempted.

Migration 3 adds `ix_history_age` for global history expiry and bounded text listing. Upgrade tests cover every prior supported numbered schema.

`SqliteProfileRepository` saves the current Core `GameProfile` plus all regions in one transaction, preserving the old aggregate if any region insert fails. Reads use a consistent deferred read transaction. It snapshots caller-owned region lists before scheduling and bounds profiles to 1,000, regions to 32 per profile, and each preprocessing/settings/overlay JSON field to 16 KiB on read. Names, languages, preprocessing and normalized geometry are validated before storage. SQL parameters preserve quote/injection-shaped input literally.

TASK-019 adds migration 5, `profile-settings`: an additive `game_profiles.settings_json TEXT NOT NULL DEFAULT '{}'` column. `ProfileSettings` captures model/credential lookup reference, OCR model/confidence, capture queue capacity, cache limits, stabilization and log retention. Window hints, provider identity and overlay style are written transactionally to their original columns too; those original columns are authoritative on read, preserving pre-v5 values. Old rows acquire safe defaults for newly exposed settings. Shared `GameProfileValidator` validates settings before save; window patterns must compile with a bounded match timeout and process hints cannot be paths. Actual credentials, remote-consent grants, images, history and cache content are not profile settings. Profile UI/portable import and session integration are still in progress, not covered by this persistence milestone.

`SqliteGlossaryRepository` persists the existing immutable global/per-profile entries, with the same 2,000-entry bound as `GlossaryCatalog`. Profile foreign keys are enforced. Deleting a profile cascades only its regions and scoped glossary; global glossary/cache survive, and history profile references become null. User confirmation belongs at the application command boundary; these repositories do not auto-delete or auto-start capture.

`SqliteHistoryRepository` defaults off each process lifetime; explicit configuration is required before writes. Retention defaults to 30 days, and each prune removes at most 128 rows (configurable 1–512). Append rejects already-expired records. Listing is explicit-user-action-only, at most 1,000 records, and filters expired rows even before cleanup. History text is limited to 4,000 characters per source/translation. Changing retention and its first prune commit transactionally before the in-memory policy changes; disabling stops future writes without silently deleting unexpired history.

Migration 4 adds persisted cache policy, row/byte accounting and a monotonic access sequence to `cache_state`, plus `translation_cache.access_order` and `ix_cache_access`. Three SQLite triggers maintain accounting on insert/delete/size update, including rollback and writes from other connections. Existing rows are accounted and assigned deterministic access order during upgrade. The original timestamp indexes are retained.

`SqliteTranslationCacheRepository` implements the existing cache contract with 128-entry maximum atomic writes. Each write transaction rereads the persisted policy and generation, so multiple instances obey the same limits and late pre-clear responses cannot restore deleted text. Creation age controls expiry; lookup touches last-use/hit count without extending expiry. A persistent sequence, not timestamp ties, determines LRU order. Policy reductions finish cleanup in yielding transactions, each bounded by the current policy's batch size. Per-entry admission evicts at most one bounded batch; an entry that still cannot fit is skipped. Counters for hits/misses/writes/evictions are per repository lifetime; rows/bytes/generation persist. Explicit text export is capped at 1,000 unexpired entries. Busy/corrupt/disk-full/unavailable errors map to the existing safe cache failure contract, allowing the translation gateway's fallback to remain unchanged.

Application DI now selects the SQLite cache/profile/glossary/history repositories. Main-window load initializes the database and applies cache/history policy asynchronously; safe database failures remain visible under **Profiles/Settings → Local database and privacy** rather than crashing the shell. Retry and explicit local recovery-copy commands are available there. The glossary editor offers explicit load/save, dirty-draft discard confirmation and atomic replacement with optimistic baseline checking.

`HistoryRecordingPresenter` decorates the session's presentation boundary. Only the explicit `Privacy.HistoryEnabled` session snapshot activates recording, and only successfully presented results are offered to persistence. The queue holds at most 32 entries; a slow database drops new history entries rather than blocking subtitles. Duplicate/foreign-session results are not recorded. Storage failure stops the recording worker safely; the visible session metrics count history errors/drops without logging text. Stop/shutdown cancels and awaits the worker; queued/in-flight optional records may be lost to meet stop responsiveness. This is best-effort history, not an audit log. The current ad-hoc session records a null profile FK; TASK-019's saved-profile integration will supply the persisted identity. History stores captured UTC and provider-reported translation duration, not a claimed end-to-end latency measurement.

Private-data clearing requires both an explicit checkbox confirmation and a truly stopped session. `SessionViewModel` serializes session startup and maintenance across asynchronous work, so starting cannot race the stopped-state check. One database transaction deletes history/cache and increments cache generation; profiles/glossary remain. `secure_delete`, vacuum and WAL truncation follow. The result distinguishes committed deletion from failed physical compaction/checkpoint. Separate backups/recovery copies are not removed, and storage hardware/OS backups are not claimed securely erased. The cache export box is cleared; an earlier pending export cannot restore its stale displayed text.

On application close, session work/maintenance completes first. Database initialization/admin commands are cancelled, new database admission closes, and all admitted operations drain before service disposal. Native window/capture tests pass `--synthetic-test`; this explicit test mode initializes a unique temporary database and removes only that owned temporary directory on normal exit. Normal app startup uses `%LOCALAPPDATA%/GameLingoAI/gamelingo.db`.

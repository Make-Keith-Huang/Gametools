# Translation memory and cache

## Exact identity

`TranslationCacheKeyBuilder` hashes a canonical UTF-8 JSON tuple with SHA-256. Version 1 contains provider ID, configured model, a fingerprint of output-relevant provider configuration, normalized language codes, the full system-prompt hash and version, glossary revision, selected-term hash, normalized source text, and ordered context. The system hash includes line-break/tag preservation choices. The selected-term hash also guards callers that accidentally supply a stale external glossary revision.

Normalization folds Unicode to NFC and CRLF/CR to LF. It preserves source case, spaces, tags, line count, and context order. Language codes are trimmed and lowercased invariantly. Request/session/sequence/correlation IDs and timing do not affect reuse. Provider/model/configuration/language/prompt/glossary/source/context changes that can change translation cause a miss; no fuzzy match is performed. Configuration fingerprinting uses settings and opaque credential references, never resolves credential values, and persists only a digest. `GetHashCode()` is never used as a persisted key.

The golden key fixture for provider `fake`, model `model-a`, configuration revision `cfg-v1`, languages `ja`→`en`, default v2 prompt, source `Hero`, and empty context/glossary is `8a561be531c6d05a91579a98d5a5c0dd9f3f971faaad5b07549ef9ac15e490b2`. It was independently calculated from the documented canonical tuple.

## Repository and retention

`ITranslationCacheRepository` provides cancellable exact lookup/touch, bounded batch upsert, incremental pruning, reconfiguration, clear, statistics, and explicit text export. Cache storage is independent of `Privacy.HistoryEnabled`. Entries contain text, provider/model/language/revision hashes, creation/last-use timestamps, and hit count; no pixels, credentials, or reusable session metadata are stored.

TASK-016 used `InMemoryTranslationCacheRepository`, as explicitly permitted before TASK-021. That implementation remains a deterministic test reference. Production DI now selects `SqliteTranslationCacheRepository`: short-lived connections, atomic lookup/touch, persisted policy/generation, trigger-maintained row/byte accounting and monotonic LRU access order. Creation age, rather than last access, controls expiry. Expired rows are never served, even before maintenance removes them. See `DATABASE.md` for migrations and lifecycle.

Defaults are 100,000 rows, 500 MiB of accounted payload, 30 days of age, and a 64-row prune batch. `SizeBytes` is the exact UTF-8 size of stored string fields plus a fixed 512-byte metadata allowance; it is logical accounting, not a claim about exact managed-heap or SQLite file size. The repository enforces both row and accounted-byte admission limits. A batch has at most 128 entries and is fully validated before mutation. Upserts replace existing keys. Oversized rows are not admitted. Each admission evicts at most one configured LRU batch; if that cannot make enough room, the new cache row is skipped while the translation remains usable. Policy reductions prune in bounded yielding batches.

Clear and policy changes increment a generation. A translation retains the generation read before its provider call, and a later upsert is accepted only if that generation is still current. Thus clearing the cache while translation is pending cannot restore the cleared data when an old response arrives. Clear preserves aggregate lifetime counters and removes stored rows; it is safe during translation because generation checks replace a stop-the-pipeline requirement for cache-only clearing.

## Concurrent misses and failure handling

`CachedTranslationProvider` implements the `ITranslationMemory` translation gateway; callers invoke one operation rather than splitting lookup/provider/store across a race. It supervises one worker per canonical key, with defaults of 32 distinct active keys and 64 waiters per key. Admission overflow returns a transient rate-limit failure and does not create an unbounded queue. Completed keys are removed before result publication.

One cancelled waiter detaches without cancelling other waiters. When the final waiter cancels, the underlying operation is cancelled and its slot stays occupied until the worker drains. Async disposal cancels and awaits every remaining worker. The owner must dispose the gateway before disposing its underlying provider. Cache hits and shared results are rebound to each caller's metadata; old session IDs and usage for a prior cached call are not replayed.

Only successful, cacheable, current provider/model results are stored. Provider failures, cancellations, oversized/invalid records, and non-cacheable outputs are not stored. Typed busy/corrupt/unavailable storage failures, I/O failures, and access failures increment a content-free counter and fall back to the provider or return its successful result without storage. A cache row with a mismatched key is treated as corrupt rather than displayed.

`TranslationMemoryFactory` is registered for session composition. It applies retention, binds provider identity/configuration, and creates the disposable gateway used by TASK-017's session pipeline. Existing concrete providers remain responsible for remote-text consent and HTTP error/timeout rules; a local cache hit performs no remote transmission.

## Administration

Profiles/Settings offers load/apply retention, clear, statistics export, and a separate explicit cached-text export. Setting maximum rows to zero disables cache storage. Statistics contain counts/bytes only. The text-export command includes up to 100 recent rows in the UI; the repository hard limit is 1,000 rows and it rejects calls without the explicit user-action flag. Export never runs automatically. Clear also removes any text currently displayed in the export box. Repository administration is dispatched off the UI thread. Cached text now survives application close in local SQLite until cleared or expired.

The added settings are optional fields within schema v3 and default safely when older v3 or migrated v1/v2 files omit them: `maximumAgeDays`, `pruneBatchSize`, `maximumInFlightRequests`, and `maximumWaitersPerKey`. No existing field changes meaning. Administration commands are mutually exclusive; the export display also uses a revision guard against a pending export completing after private-data clearing. Policy changes check cancellation before committing, then complete bounded cleanup before returning, even if cancellation arrives during that cleanup.

## Verification and database follow-through

The cache suite covers the golden key, canonical equivalence and semantic invalidation, fake-clock age/LRU/byte retention, batch validation and upserts, 32-way coalescing, metadata rebinding, bounded admission, cancellation isolation/draining, clear-during-miss, disabled cache, busy/corrupt fallback, explicit export, administration, and parallel repository access. It prints an exact-lookup timing smoke measurement.

Real temporary-SQLite tests now cover lookup/touch, shared policies, generation clearing, LRU/age/bytes, batch rollback including accounting triggers, bounded export, concurrent access and actual busy/corrupt failures. `DATABASE.md` describes the numbered migrations and database lifecycle. The in-memory benchmark is not a SQLite latency measurement; SQLite-specific performance and final session/admin integration remain part of TASK-021 acceptance.

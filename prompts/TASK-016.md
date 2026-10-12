# TASK-016 — Translation Memory and Cache

## Goal

Implement a fast, privacy-aware translation cache with canonical keys, LRU retention, and concurrency-safe writes.

## Preconditions

TASK-015 complete; TASK-021 database contracts/schema completed or use an in-memory repository until merged.

## Allowed modification scope

Core cache contracts/key logic, Infrastructure cache repository, pipeline integration, cache settings/UI, tests, and database/translation docs.

## Implementation requirements

- Build SHA-256 keys from documented canonical fields including provider/model/languages/prompt/glossary revisions and normalized text.
- Support exact lookup, hit metadata, batched/upsert writes, max age/rows/bytes, and incremental LRU pruning.
- Serialize concurrent same-key misses through bounded request coalescing without caching failures/cancellations.
- Allow clear/export statistics; content export requires explicit user action.
- Keep cache usable when history is disabled and degrade gracefully if persistence fails.

## Prohibited

No `GetHashCode` persistence, secret/pixel storage, fuzzy reuse that changes meaning, unbounded in-memory locks, UI-thread database work, audio, injection/hooks, or game modification.

## Acceptance criteria

Canonical-equivalent requests hit; any semantic field change misses; concurrent misses make one provider call; retention stays within configured limits; corrupt/database-busy conditions do not crash active sessions.

## Build/test

Add golden key vectors, concurrency/race tests, temporary-SQLite integration tests, retention/time tests with fake clock, and failure fallback tests. Run full build/tests.

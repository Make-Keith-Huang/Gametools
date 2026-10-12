# TASK-021 — SQLite Database and Migrations

## Goal

Implement the documented SQLite schema, transactional migrations, repositories, retention, and recovery behavior.

## Preconditions

TASK-002 complete; read `DATABASE.md`; choose and centrally pin a maintained .NET SQLite package.

## Allowed modification scope

Persistence contracts/models, `Infrastructure/Persistence/**`, package version files, composition, repository/migration tests, and database docs.

## Implementation requirements

- Create database under user-local application data; enable foreign keys, WAL, busy timeout, and parameterized statements.
- Implement ordered idempotent startup migrations with `schema_migrations`, transaction rollback, and backup before destructive changes.
- Implement profile/region/glossary/cache/history repositories with async/cancellable boundaries and clear concurrency policy.
- Default history off; implement retention/pruning and clear-private-data flow.
- Classify busy/corrupt/disk-full/schema-newer errors; offer read-only recovery/export when possible.

## Prohibited

No SQL concatenation, API keys, pixels/screenshots, plaintext secrets, database in install directory, silent destructive reset, UI-thread blocking, audio data, injection/hooks, or game-file changes.

## Acceptance criteria

Fresh database matches documented schema/indexes; every supported upgrade succeeds or rolls back unchanged; constraints/cascades work; repositories handle concurrent reads/writes; failures degrade without crashing capture; secrets are absent.

## Build/test

Use temporary real SQLite databases. Test fresh/upgrade/rollback/newer schema, constraints, injection strings, busy/corrupt/disk-full abstraction, retention, cancellation, and parallel access. Run full build/tests.

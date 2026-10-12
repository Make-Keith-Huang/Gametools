# TASK-015 — Glossary and Prompt Construction

## Goal

Implement deterministic terminology management and versioned translation prompts that preserve names, layout, and source meaning.

## Preconditions

TASK-011 complete and provider adapters available or faked; read database/profile designs.

## Allowed modification scope

Core glossary/prompt models/services, App glossary UI/ViewModels, persistence contracts, provider integration points, tests, and translation docs.

## Implementation requirements

- Support global and per-profile source/target terms, enabled/case-sensitive flags, validation, import/export, and deterministic conflict precedence.
- Select only relevant terms under count/token/length budgets.
- Build a versioned provider-neutral prompt/message plan; escape source/glossary as data and state translation-only rules.
- Preserve configurable line breaks/tags and define a stable glossary revision hash for cache invalidation.
- Make preview show exact non-secret prompt content.

## Prohibited

No arbitrary prompt execution/templates, hidden provider-specific behavior in Core, secrets, network in glossary logic, audio, injection/hooks, process memory, or game-file changes.

## Acceptance criteria

Precedence and matching are deterministic; hostile terms/source cannot alter message roles; same glossary yields same revision across runs; import rejects oversize/invalid data transactionally; provider adapters consume the shared plan.

## Build/test

Add Unicode/case/conflict/budget/hash/import/export/injection-shaped fixture tests and provider request snapshots. Run full build/tests.

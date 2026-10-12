# TASK-001 — Solution Baseline and Quality Gates

## Goal

Turn the bootstrap into a production-oriented .NET 8 solution baseline with enforceable compiler/analyzer/formatting conventions and CI-ready commands.

## Preconditions

Initialization prompt completed; read `AGENTS.md`, `CODING_RULES.md`, and current project files.

## Allowed modification scope

Root build properties, solution/project files, `.editorconfig`, build scripts, CI configuration, `README.md`, and bootstrap tests.

## Implementation requirements

- Add `.editorconfig`, analyzers supported without destabilizing local builds, consistent namespaces, and Release warning enforcement.
- Separate unit/integration test conventions and define artifact/test-result directories.
- Add a CI workflow or documented equivalent for restore, format check, Release build, and test.
- Pin package versions centrally if packages are introduced; document SDK/workload prerequisites.
- Keep projects free of feature implementation.

## Prohibited

Do not add product features, secrets, machine-specific paths, generated build output, audio/STT/TTS, injection, hooks, process-memory access, or game-file modification.

## Acceptance criteria

Fresh restore/build is deterministic; intentionally introduced warnings fail Release/CI; formatting is repeatable; repository ignores all generated/private artifacts; README lists exact setup commands.

## Build/test

Run restore, `dotnet format --verify-no-changes`, Release build, and all existing tests. Include exact results and any analyzer suppressions with justification.

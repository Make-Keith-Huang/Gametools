# Project Initialization Prompt

You are the lead engineer initializing GameLingoAI V1. Work in this repository and treat `AGENTS.md` as binding. Read `PRODUCT_REQUIREMENTS.md`, `ARCHITECTURE.md`, `CODING_RULES.md`, `DATABASE.md`, `TEST_PLAN.md`, `ROADMAP.md`, `TASKS.md`, and all `docs/*.md` before changing code.

## Goal

Confirm the repository is a coherent, buildable .NET 8/WPF baseline and prepare it for the numbered task sequence without implementing product features early.

## Preconditions

- Windows with .NET 8 SDK and WPF tooling is available.
- No uncommitted user changes may be overwritten.

## Allowed modification scope

Root build/config files, `README.md`, solution/project files, bootstrap-only code, and test bootstrap. Do not implement capture, OCR, translation, overlay, or database behavior.

## Implementation requirements

- Inspect the tree and dependency direction.
- Ensure solution restore/build works and project references are `App -> Infrastructure -> Core`.
- Establish deterministic builds, nullable checking, warnings-as-errors, `.gitignore`, and a repeatable verification script.
- Record any environment gap and keep the baseline useful without hidden dependencies.

## Prohibited

No audio/STT/TTS; no DLL injection, memory/graphics hooks, drivers, anti-cheat bypass, game-file changes, credentials, or unrelated frameworks. Do not silently add packages or generated binaries.

## Acceptance criteria

- Clean clone structure is understandable and buildable.
- Core has no platform/package dependency.
- Bootstrap app starts and explains that features are not yet configured.
- Verification script fails on build or smoke-test failure.

## Build/test

Run `dotnet restore GameLingoAI.sln`, `dotnet build GameLingoAI.sln -c Release --no-restore`, and the bootstrap smoke executable. Summarize commands, results, changes, assumptions, and next task.

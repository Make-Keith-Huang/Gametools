# AGENTS.md

## Mission

Build GameLingoAI V1: a safe, user-controlled Windows application that translates visible game subtitles in near real time. Optimize for reliability, low distraction, inspectability, and graceful degradation.

## Non-negotiable V1 boundaries

- Do not add audio capture, speech recognition, speech synthesis, voice cloning, microphone access, or speaker-loopback access.
- Do not inject DLLs, patch processes, read/write game memory, install drivers, hook DirectX/Vulkan/OpenGL, bypass anti-cheat, or modify game files.
- Capture only a window or region explicitly selected by the user through supported Windows APIs.
- Never log API keys, authorization headers, raw screenshots, or unredacted sensitive text.
- Never send pixels to a translation provider. Only OCR text may leave the machine after the user enables a remote provider.
- The overlay must not consume mouse input while click-through mode is enabled.

## Required workflow for every Codex task

1. Read this file, the task prompt, relevant design documents, and all files in the allowed modification scope.
2. Inspect the current working tree; preserve unrelated user changes.
3. State assumptions in the task summary. Do not silently expand scope.
4. Implement the smallest coherent change satisfying the acceptance criteria.
5. Add or update tests for normal, error, cancellation, and boundary behavior.
6. Run formatting, build, and relevant tests. Report exact commands and results.
7. Update documentation when behavior, configuration, schema, or interfaces change.
8. After successful verification, remove confirmed obsolete generated intermediates before proceeding. Cleanup is limited to unnecessary files created by the agent for this project; never clean unrelated local files, software or data. Keep current outputs, concise failure evidence, recovery packages and user data; summarize superseded conclusions instead of endlessly appending duplicates. Inspect disk use first, constrain cleanup to known generated paths, and ask before deleting projects or ambiguous files. Never hide unresolved failures by deleting their only evidence.

## Architecture rules

- Dependency direction is `App -> Infrastructure -> Core`; Core references no UI, storage, network, OCR, or Windows implementation.
- Use asynchronous APIs and `CancellationToken` for capture, OCR, translation, persistence, and orchestration.
- Use immutable records for pipeline messages where practical.
- Use interfaces at external boundaries. Keep provider-specific DTOs inside their adapters.
- UI code-behind is limited to view-only interop that cannot be expressed cleanly in XAML/MVVM.
- The pipeline is bounded and backpressured; never create an unbounded frame or request queue.
- Treat time, IDs, filesystem access, HTTP, and native APIs as replaceable dependencies when tests benefit.
- Configuration has explicit defaults and validation. Secrets use Windows Credential Manager or process/user secret sources, never tracked JSON.

## Quality bar

- Nullable reference types and implicit usings enabled; warnings are treated as errors in CI.
- Public contracts have XML documentation when intent is not obvious.
- No `async void` except UI event handlers; no `.Result`/`.Wait()` on async work.
- Dispose native, image, stream, database, and cancellation resources deterministically.
- User-facing failures are actionable; diagnostics include correlation IDs but no secrets.
- Maintain target budgets from `ARCHITECTURE.md` and tests from `TEST_PLAN.md`.

## Standard verification

```powershell
dotnet restore GameLingoAI.sln
dotnet build GameLingoAI.sln -c Release --no-restore
dotnet test GameLingoAI.sln -c Release --no-build
dotnet format GameLingoAI.sln --verify-no-changes
```

If a command cannot run because an external runtime/model/GPU is absent, keep unit tests deterministic with fakes and document the exact manual verification still needed.

## Definition of done

A task is done only when its acceptance criteria pass, tests/build pass, prohibited behavior is absent, cancellation and errors are handled, relevant docs are current, and the final summary lists changed files plus residual risks.

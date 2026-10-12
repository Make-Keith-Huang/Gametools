# TASK-002 — Core Domain Models and Contracts

## Goal

Define stable platform-independent contracts for capture, OCR, stabilization, translation, overlay, profiles, persistence, clock, and session lifecycle.

## Preconditions

TASK-001 complete; read `ARCHITECTURE.md`, `docs/OCR_PIPELINE.md`, `docs/TRANSLATION_PIPELINE.md`, and existing Core models.

## Allowed modification scope

`src/GameLingoAI.Core/**`, Core-focused tests, and contract sections of architecture docs.

## Implementation requirements

- Model session/correlation/sequence metadata, frame ownership, OCR spans, normalized regions, stable text, translation requests/results, provider capabilities, profile IDs, and typed failures.
- All long-running contracts accept cancellation; make ownership/disposal explicit.
- Validate invariants at construction or through result-returning factories.
- Add a deterministic clock abstraction and state-transition rules.
- Keep provider/native/database DTOs out of Core.

## Prohibited

No references to WPF, Windows capture types, PaddleOCR binding types, SQLite, Serilog, provider SDKs, audio, injection, hooks, game memory, or game files.

## Acceptance criteria

Core builds alone; invalid regions/transitions are rejected; contracts support the documented pipeline without platform leakage; XML docs explain non-obvious ownership and privacy semantics.

## Build/test

Add tests for model equality, boundary validation, monotonic sequence behavior, and allowed/forbidden session transitions. Run Core build and relevant tests, then full solution build.

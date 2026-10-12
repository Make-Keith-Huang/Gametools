# Agent Memory

This file is durable engineering context for Codex tasks. Update it only for decisions that later tasks must know.

## Fixed decisions

- Product: Windows game subtitle OCR translation overlay, V1 only.
- Stack: C#/.NET 8, WPF, MVVM, built-in DI, Windows Graphics Capture, PaddleOCR, SQLite, Serilog.
- Provider boundary: OpenAI-compatible, Ollama, and constrained custom HTTP.
- Privacy: captured pixels remain local; only normalized text may reach a remote provider after consent.
- Safety: no audio/STT/TTS, injection, memory/graphics hooks, drivers, anti-cheat bypass, input automation, or game-file changes.
- Architecture: Core contracts are platform-independent; bounded cancellable pipeline; latest sequence wins.

## Working conventions

- IDs are `Guid`; timestamps are UTC through an injectable clock.
- Capture regions are normalized `[0,1]` rectangles relative to captured content.
- Persisted cache hashes use SHA-256 canonical UTF-8 input.
- User secrets are referenced by opaque secret IDs and stored through an OS-backed secret store.
- History is opt-in and disabled by default.

## Decision log template

Append entries as: `YYYY-MM-DD | Task | Decision | Rationale | Consequences`. Do not record temporary progress or secrets.

## Open decisions for implementation tasks

- Exact PaddleOCR .NET/native binding after a license, maintenance, x64, and cancellation spike.
- OpenAI-compatible endpoint dialect negotiation.
- Installer choice (MSIX vs signed bootstrapper) after model-size and update experiments.

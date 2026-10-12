# TASK-013 — Ollama Translation Provider

## Goal

Implement a local Ollama provider with model discovery, health status, and predictable translation behavior.

## Preconditions

TASK-011 complete; Ollama protocol/version assumptions documented; local instance optional for manual tests.

## Allowed modification scope

`Infrastructure/Translation/Ollama/**`, provider configuration/UI, adapter tests, composition, and docs.

## Implementation requirements

- Default to loopback endpoint and validate that non-loopback HTTP requires explicit rejection or HTTPS policy.
- Implement health/model discovery and translation with non-streaming default; isolate protocol DTOs.
- Use shared prompt construction, limits, cancellation, timeout, concurrency, error mapping, and sanitizer.
- Provide actionable states for service unavailable, model missing, model loading, and invalid response.
- Never assume local means trusted; enforce response bounds.

## Prohibited

No automatic model pull without explicit confirmation/size disclosure, shell execution, arbitrary endpoint discovery, pixel/image upload, audio, injection/hooks, process memory, or game-file changes.

## Acceptance criteria

Fake Ollama server contract passes; loopback default works without secret; non-loopback policy is enforced; missing model is actionable; slow/load behavior does not freeze UI; output is plain sanitized text.

## Build/test

Add fake-HTTP tests for tags/health/generate or chat schemas, unavailable/model-missing/malformed/oversize/cancel cases. Add opt-in local manual test instructions; run full build/tests offline.

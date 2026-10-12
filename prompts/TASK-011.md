# TASK-011 — Translation Provider Foundation

## Goal

Implement provider-neutral translation orchestration contracts, typed errors, health/capability checks, rate limiting, timeout, retries, and output safety.

## Preconditions

TASK-002 and TASK-003 complete; read translation pipeline and security requirements.

## Allowed modification scope

Core provider models/contracts, shared Infrastructure HTTP/resilience components, DI/configuration, tests, and translation docs.

## Implementation requirements

- Define provider identity/capabilities, request/result, validation, health check, usage, and typed failure categories.
- Use typed `HttpClient` plumbing, response-size limits, cancellation, per-request timeout, rate/concurrency limits, bounded transient retries with jitter, and `Retry-After`.
- Sanitize output as plain text with length/line/control-character limits.
- Add consent gate proving only text—not pixels—can enter remote provider code.
- Make policies deterministic/testable via fake handler, clock, and jitter source.

## Prohibited

No provider-specific DTOs in Core, global mutable clients, TLS bypass, secret logging, pixel/image transmission, infinite retries, audio, injection/hooks, or game-file changes.

## Acceptance criteria

Shared harness distinguishes auth/validation/transient/timeout/cancel/parse failures; retry matrix is correct; concurrency is bounded; output sanitizer is enforced; consent blocks remote calls before approval.

## Build/test

Add exhaustive fake-HTTP tests including 200/400/401/408/429/5xx, slow, disconnect, oversized, malformed, and cancellation cases. Run build/tests without live network.

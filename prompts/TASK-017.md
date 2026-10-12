# TASK-017 — Bounded End-to-End Pipeline Orchestration

## Goal

Connect capture, preprocessing, OCR, stabilization, cache/translation, and overlay through a supervised cancellable session pipeline.

## Preconditions

TASK-006, TASK-010, and TASK-011 through TASK-016 complete; fake adapters available.

## Allowed modification scope

Core/application orchestration, Infrastructure registration, App session ViewModel, pipeline tests/telemetry, and architecture docs.

## Implementation requirements

- Implement documented session state machine with atomic start, pause/resume, stop, recovery, and fault reporting.
- Use capacity-bounded channels and latest-frame/result policies; sequence/session checks reject stale results.
- Supervise every worker task, propagate cancellation, dispose/drain resources, and prevent fire-and-forget exceptions.
- Separate transient stage degradation from fatal session failure; database/remote failure must not leak resources.
- Measure per-stage latency/drop/cache/error metrics with correlation IDs and no sensitive content.

## Prohibited

No unbounded queue/task creation, blocking waits, stale overlay updates, raw screenshot/text logging, automatic capture without selection, audio, injection/hooks, game memory, or game-file modification.

## Acceptance criteria

Fake end-to-end flow displays current translation; overload drops stale work while UI remains responsive; stop completes within two seconds; rapid restart isolates sessions; every injected stage failure reaches the defined state and recovers/disposes correctly.

## Build/test

Add deterministic virtual-time/fake-stage tests for ordering, pressure, cancellation, start rollback, faults, retry recovery, rapid restart, and metrics. Run full tests plus a synthetic 30-minute soak.

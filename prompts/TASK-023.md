# TASK-023 — Automated Tests, Benchmarks, and Soak Harness

## Goal

Complete the P0 quality suite and reproducible performance/soak harness required by `TEST_PLAN.md`.

## Preconditions

TASK-017 through TASK-022 complete; all current tests green; licensed/synthetic fixture policy established.

## Allowed modification scope

`tests/**`, benchmark/soak tooling under `tools` or `scripts`, CI configuration, minimal production seams for testability, TEST_PLAN and benchmark docs.

## Implementation requirements

- Replace bootstrap runner with maintained unit/integration/UI test projects as appropriate and central package versions.
- Cover domain, state machine, backpressure, cancellation, OCR stabilization, provider contracts, cache, SQLite migrations, overlay logic, privacy/redaction, and composition.
- Build deterministic timestamped frame replay and fake provider fault/latency simulator.
- Add benchmark reports for stage/end-to-end p50/p95, drop rate, allocations, CPU/memory sampling; add 60-minute active and 8-hour idle soak modes.
- Tag platform/slow/opt-in tests and keep default CI reliable/offline.

## Prohibited

No copyrighted game captures without rights, live paid API in CI, secret credentials, flaky sleeps, disabled assertions, audio tests, injection/hooks, memory access, or game changes.

## Acceptance criteria

All P0 automated requirements map to named tests; default suite runs offline; failure diagnostics are safe/actionable; replay is repeatable; performance gates compare against documented baselines; soak detects growth and exits nonzero on breach.

## Build/test

Run format, Release build, default tests with coverage, platform integration suite, replay benchmark, shortened soak in CI mode, and document commands/results plus unexecuted long/manual gates.

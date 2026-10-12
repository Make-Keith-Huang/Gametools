# TASK-022 — Serilog Diagnostics and Safe Export

## Goal

Add structured, privacy-preserving diagnostics, pipeline metrics, bounded rolling logs, and a user-controlled support bundle.

## Preconditions

TASK-003 and TASK-017 complete; threat/privacy requirements reviewed; centrally pin Serilog packages.

## Allowed modification scope

Diagnostics abstractions, Infrastructure logging/redaction/export, App diagnostics UI, configuration, tests, package metadata, and support docs.

## Implementation requirements

- Configure Serilog console/debug in development and bounded rolling files in user-local data for production.
- Enrich session/correlation/stage/error category while excluding secrets and content by default.
- Central redaction handles keys, auth headers, URLs/query values, provider configs, exception data, and known secret values.
- Expose aggregate latency/drop/cache/error/resource metrics and recent sanitized events.
- Support bundle requires preview/consent and includes versions/config schema/logs/metrics only; never pixels, raw OCR/translation/history, keys, or dumps by default.

## Prohibited

No remote telemetry by default, secret/raw content logging, unbounded files, crash dumps with frame buffers, audio, injection/hooks, process memory, or game-file changes.

## Acceptance criteria

Synthetic secrets never appear in any sink/export; rotation/retention are enforced; logging failure cannot crash pipeline; correlation supports one event flow; export preview exactly matches archive contents and cancellation cleans partial files.

## Build/test

Add adversarial redaction/property tests, sink failure tests, rotation/retention tests, metric concurrency tests, and archive manifest/absence tests. Run full build/tests and inspect a generated safe bundle manually.

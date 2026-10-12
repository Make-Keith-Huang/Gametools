# Test Plan

## Strategy

Use a test pyramid: pure unit tests for normalization/stabilization/cache keys/state transitions; integration tests for SQLite, provider HTTP contracts, and composition; Windows adapter tests on a dedicated runner; and a small manual release matrix with real games or synthetic windows.

## P0 automated suites

The maintained xUnit entry points and P0 requirement mapping are in [P0 test map](docs/P0_TEST_MAP.md); [test conventions and commands](tests/README.md) describe offline defaults, platform/opt-in gates and coverage. `scripts/verify.ps1` runs default tests with coverage, a synthetic regression benchmark and a 12-second wall-clock soak; explicitly add `-IncludePlatform` on an interactive Windows desktop. [Harness commands and limitations](tools/GameLingoAI.QualityHarness/README.md) describe 60-minute active/8-hour idle modes and fault profiles. Neither the short soak nor virtual counter tests are evidence of the long/full-application gates below.

- Domain invariants: normalized regions, language/provider configuration, sequence ordering.
- Pipeline: bounded queue drops stale frames, cancellation drains safely, latest result wins, faults transition correctly.
- OCR processing: Unicode normalization, line order, confidence threshold, repeated-frame stabilization, empty/noisy frames.
- Translation: cache hit/miss, glossary revision invalidation, timeout, 429/5xx transient retry, 401 no retry, malformed/oversize responses.
- Security: redaction of API keys/headers, no pixels in outbound HTTP, HTTPS validation with loopback exception, output control-character removal.
- SQLite: clean migration, upgrade from every supported schema, rollback on failure, foreign keys, retention pruning.
- Overlay state: DPI coordinate conversion, window move/resize/minimize, click-through style, stale update rejection.

## P0 manual matrix

| Area | Cases |
|---|---|
| OS | Windows 10 22H2 and current Windows 11, x64 |
| Display | 100%, 150%, 200% DPI; single and mixed-DPI dual monitor |
| Game mode | windowed and borderless; document unsupported exclusive full-screen behavior |
| OCR | Latin, Simplified Chinese, Traditional Chinese, Japanese; small/outlined subtitles |
| Providers | local Ollama, OpenAI-compatible service, custom mock server, offline mode |
| Lifecycle | start/stop 50 times; minimize, restore, close/reopen target; sleep/resume |

## Performance and soak

- Replay a fixed timestamped frame corpus so changes are comparable.
- Report p50/p95 per-stage and end-to-end latency, drop rate, CPU, GPU where observable, allocations, and private bytes.
- Run a 60-minute active soak and an 8-hour idle soak. After warm-up, active private-memory growth must remain below 50 MB.
- Simulate 100-1,500 ms provider latency, packet loss, 429, 500, disconnect, and recovery.

## Accessibility and usability

- Keyboard-only setup; screen-reader labels on all controls; focus remains visible.
- Overlay contrast and typography at supported DPI; no mouse interception in click-through mode.
- First-run privacy language clearly distinguishes local pixels from remotely sent text.

## Release gate

All P0 automated tests, build, formatting, packaging smoke test, and manual matrix must pass. No unresolved critical/high security issue, prohibited V1 capability, crash, data loss, secret leak, or unbounded queue is acceptable.

# Architecture

## System shape

```text
WPF Views/ViewModels
        | commands + observable state
Application Orchestrator / Session State Machine
        | bounded channels and immutable messages
Capture -> Crop/Preprocess -> OCR -> Stabilize/Deduplicate -> Translate -> Overlay
        |                    |                      |
 Windows Graphics Capture  PaddleOCR        Provider abstraction
                                                   | OpenAI-compatible
Persistence (SQLite)                              | Ollama
Profiles / Glossary / Cache / History             | Custom API
```

`GameLingoAI.Core` owns models and contracts. `GameLingoAI.Infrastructure` implements external concerns. `GameLingoAI.App` is the composition root and WPF surface. No Core type depends on WPF, Windows capture, PaddleOCR, SQLite, Serilog, or an HTTP provider.

## Core contract semantics

- `PipelineMetadata` carries strongly typed session/correlation IDs, a non-negative monotonic sequence, and a UTC capture timestamp through every stage.
- A `CaptureFrame` is owned by its consumer and must be disposed after downstream processing. Its optional lease controls pixel lifetime; captured pixels are local-only and are never valid translation-provider input.
- Domain constructors reject invalid normalized bounds, dimensions, confidence, identifiers, timestamps, and session transitions. `MonotonicSequenceGuard` rejects duplicate or late results.
- External failures cross boundaries as `PipelineFailure` values with safe messages and correlation IDs rather than provider/native/database DTOs.
- Long-running capture, preprocessing, OCR, stabilization, translation, overlay, persistence, and session contracts accept `CancellationToken`.
- Time and ID creation are abstracted by `IClock` and `IIdGenerator` for deterministic tests.

## Runtime pipeline

1. `ICaptureSource` emits timestamped frames for a user-selected window.
2. A capacity-two channel drops the oldest frame when full.
3. `IFramePreprocessor` crops normalized regions and prepares OCR images.
4. `IOcrEngine` returns positioned text candidates and confidence.
5. `ITextStabilizer` normalizes, orders, merges, and emits bounded `New`/`Changed`/`Disappeared` events only after temporal stability rules pass.
6. `ITranslationMemory` checks exact/normalized cache and glossary rules.
7. `ITranslationProvider` translates only cache misses under rate/timeout policy.
8. `IOverlayPresenter` receives the latest display model; stale sequence IDs are ignored.

Every message carries `SessionId`, monotonic `Sequence`, capture timestamp, and correlation ID. Cancellation flows from a session-scoped token. A new profile/window selection cancels and drains the old session before starting another.

TASK-016 makes `ITranslationMemory` a translation gateway extending `ITranslationProvider`, rather than exposing a racy split lookup/store API. `TranslationMemoryFactory` binds the effective provider configuration and repository; its gateway performs canonical lookup, bounded same-key coalescing, provider invocation and generation-checked storage as one operation. Session owners asynchronously dispose the gateway before its underlying provider. TASK-017's `SessionResourcesFactory` now connects this gateway to real adapters, while Core's `SessionController` supervises four workers with fixed 2/1/1 channels. See [session pipeline](docs/SESSION_PIPELINE.md) for ownership, lifecycle, UI entry, transient recovery and verification limits.

## Session state machine

`Stopped -> Starting -> Running -> Paused -> Running -> Stopping -> Stopped`. Any state may enter `Faulted`; recovery either retries the failed adapter or returns to `Stopped`. Starting is transactional: the overlay is hidden and acquired resources are disposed if any step fails.

## Performance budgets

| Stage | Typical budget | Policy |
|---|---:|---|
| Capture/crop | 16-50 ms | sample 5-15 FPS; drop stale frames |
| OCR | 80-400 ms | one in flight per engine by default |
| Stabilization | <10 ms | pure, allocation-conscious logic |
| Cache lookup | <5 ms | indexed normalized hash |
| Remote translation | 200-1,200 ms | cancel stale request when safe |
| Overlay update | <16 ms | dispatcher update, latest result only |

## Technology decisions

- .NET 8 and WPF: mature Windows desktop stack with strong native interop.
- MVVM: testable UI state and command handling.
- `Microsoft.Extensions.DependencyInjection`: composition and lifetimes.
- Windows Graphics Capture: supported OS capture surface without process injection.
- PaddleOCR: local multilingual OCR behind `IOcrEngine`.
- SQLite: embedded transactional persistence with migrations.
- Serilog: structured rolling logs and redaction enrichers.
- `HttpClientFactory`: pooled clients, per-provider timeout, testable handlers.

## Resilience

- Use bounded channels, per-stage timeouts, exponential backoff with jitter only for transient failures, and circuit breaking for repeated provider failures.
- Never retry invalid credentials, invalid requests, cancellation, or unsupported models.
- Maintain last good overlay briefly during transient gaps, then fade/hide according to profile.
- Persist cache writes in batches; database failure disables persistence without taking down capture/overlay.

## Security boundaries

The game process is outside the trust boundary and is never opened for memory access. Captured pixels enter only local OCR. Translation adapters receive text after the consent gate. Native handles are validated and short-lived. Provider responses are untrusted text and are length-limited before rendering.

## Deployment

Primary packaging is self-contained win-x64 MSIX or signed installer. OCR models are versioned external assets with SHA-256 validation and explicit license notices. First launch validates WebView-free WPF runtime, model availability, writable data directory, and database migrations.

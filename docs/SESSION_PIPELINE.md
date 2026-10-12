# Session pipeline

TASK-017 connects the real WGC, preprocessing, local OCR, stabilization, glossary, cache gateway and selected translation adapter. The latest display boundary supplies the Home page and TASK-018's transparent Windows overlay. Overlay lifecycle is independently bounded and attached to the confirmed session target. No window is captured automatically.

## Starting a session

1. Explicitly install/verify a supported local OCR model on the OCR page. No automatic model download occurs at startup.
2. Configure a provider and, for remote text processing, explicitly grant consent in settings.
3. Select and confirm a window; configure valid subtitle regions.
4. On Home, load configured providers, explicitly select one, then Start. The profile ID shown there can be copied into profile-scoped glossary entries before starting.

Start snapshots the region list, languages, preprocessing, selected provider mapping, glossary and preservation options. Editing these while running does not redirect the existing capture or change its prompt. Stop and start to apply changes. The current profile is session-local until TASK-021 persistence. History is not enabled by this orchestration task.

The local preview is stopped and drained before starting. A 15-second cancellable startup budget covers resource acquisition and the first valid capture frame. Running is committed only after that frame arrives. Validation/model failures roll back acquired adapters and leave an actionable, correlated fault. Native model creation still happens on first inference; failure then is supervised as an OCR fault.

## Ownership and pressure

- Four supervised tasks: capture, preprocessing/OCR/stabilization, cache/translation, presentation.
- Three bounded channels: two captured frames, one stable-text event, one display update. Full queues drop the oldest item; dropped frames immediately release their pixel lease.
- Capture samples at the profile FPS. Preprocessing and OCR have only one in-flight item. Source and processed image leases are released on success, failure and cancellation.
- Session and monotonic capture sequences reject stale input. Each adapter must preserve metadata; a contract violation faults rather than displaying mismatched output.
- New stable text cancels the previous translation request. An adapter that returns despite cancellation still cannot publish a stale result. Disappearance invalidates prior translation and independently queues a hide update.
- A short asynchronous presentation gate orders stable-event publication against display commit. Presenters must honor cancellation and finish promptly; no lock is held while OCR or HTTP runs.
- Cache gateways are disposed before underlying providers; OCR and capture are also session-owned. Disposal attempts every adapter even if one fails.

Pause cancels and awaits all cycle workers, drains queues, hides the display and resets stabilization. It retains reusable adapters but releases the active capture enumeration/native capture resources. Resume starts fresh queues using the same session and continuing capture sequence. Stop cancels startup if necessary, drains workers and disposes adapters. A restart gets a new session ID. Closing the main window awaits session shutdown before closing the application.

## Failures and observability

### Session buttons and shortcuts (TASK-020)

Home exposes Start, Pause, Resume, Stop, Hide/Show subtitles and Emergency stop. Applied global shortcuts route to the same guarded commands; they never confirm a target or select a provider. Pause/Resume chooses the command appropriate to the current state. Startup, pause/resume and layout-edit transitions reject overlapping requests, and repeated stop requests share the current drain. Shutdown detaches shortcut dispatch before releasing session resources.

Stop and Emergency stop immediately unbind the overlay, cancel pending Start/Resume/layout work, and request pipeline cancellation before awaiting cleanup. Stop is available even while startup waits for maintenance/configuration and the controller is still Stopped. Startup/layout continuations recheck cancellation before displaying anything. Settings are not written or discarded. Native resource cleanup is awaited, not forcibly terminated; the native inference limitation below still applies.

Hide suppresses presentation without discarding the current session. New translations and timer refreshes cannot override it. Show displays the latest eligible subtitle but cannot override target visibility/foreground protections. A new session or explicit layout edit clears suppression. Home also displays the confirmed/active window, selected/active profile and provider, and the latest translation-result duration (not an end-to-end latency measurement).

Regression coverage includes cancellation before controller startup, stopped command admission, repeated hotkeys and unsubscription, and real overlay HWND suppression/new-result/restore/foreground behavior. The isolated Windows overlay harness also runs `SessionControlsTests`: successful fake-pipeline Running/Pause/Resume, repeated action dispatch, a noncooperative translation request proving immediate cancellation notification/native hiding before drain, blocked resource startup and first-frame Resume cancellation, layout-pause/Emergency interleaving, late-result rejection, disposal-failure reporting and persisted profile/configuration preservation. Real WPF controls verify emergency/visibility command bindings and visible identity/latency bindings. No input is simulated in these tests; foreground-independent physical shortcut acceptance is still required before TASK-020 is accepted.

For the opt-in physical-key harness, set `GAMELINGOAI_SESSION_MANUAL=1` and run the `overlay Windows harness` smoke filter. Its test window uses a fake pipeline, temporary SQLite data, no remote service and the native target-visibility probe. Only clicking its explicit Enable button registers Ctrl+Alt+Shift with F7 Start, F8 Pause/Resume, F9 Hide/Show, F10 Stop and F11 Emergency stop. Follow the displayed sequence, trigger F11 with a different application's window foreground, and select Finish. Finish requires all five actions (including two Start/PauseResume/Toggle notifications), a foreground-other notification, no synchronous focus change and a stopped session. Closing early or the 14-minute timeout is NOT a pass; registrations are disposed. Success also verifies native unregister/re-registration. User observation is still needed to confirm visible behavior and absence of delayed focus stealing; the harness does not record arbitrary keys or other-app text.

On 2026-09-29 the physical harness exited successfully after Finish: all five actions received, 16 foreground-other notifications, no synchronous focus change, and release/re-registration passed. The final full 39-suite gate also passed after this harness addition. The user subsequently confirmed normal pause/resume, hide/show and focus behavior (`都正常`), completing TASK-020 acceptance. See `TASK_EXECUTION_PROGRESS.md` for the evidence and retained native-model release limitations.

Transient OCR failures skip the frame and retry on the next observation. Transient translation outcomes receive at most three orchestration attempts with 250/500 ms cancellable delays, in addition to the adapter's bounded transport policy. After exhaustion, stabilization is reset so unchanged subtitles can be observed again. Newer text supersedes this retry work. Permanent configuration/authentication/consent/model failures, contract violations and unexpected stage exceptions fault the session. Retry is an explicit new Start, not an uncontrolled background loop.

Database/cache failures are contained by the TASK-016 gateway and do not own or retain frame pixels. Metrics contain only session/correlation IDs, enum stage names, counters, and the latest 256 latency samples per stage (p50/p95). They include frame/text/result drops, stale outcomes, cache hits and errors. No source/translated text, pixels, endpoint, credential or raw exception is stored in these metrics. The UI polls a latest-value slot every 200 ms, rather than enqueueing one dispatcher action per frame.

## Verification and limits

`dotnet run --project tests/GameLingoAI.Tests/GameLingoAI.Tests.csproj -c Release --no-build -- "session pipeline"` runs fake-stage lifecycle, pressure, latest-result, cancellation, start rollback, fault recovery, metadata, ownership and virtual-time retry tests. It also replays 1,801 acknowledged frames over exactly 30 virtual minutes, checking bounded managed-memory growth and disposal counts. This is a synthetic accelerated soak, not 30 wall-clock minutes of native capture/inference or a CPU/GPU benchmark.

The complete `scripts/verify.ps1` additionally covers real WPF startup, WGC cycles, preprocessing, OCR-adapter resource tests, provider contracts and cache behavior. Native PaddleOCR inference still needs explicitly provisioned model/fixture assets. The two-second stop assertion covers cooperative fake adapters and interrupted requests. An in-process native inference call cannot be forcibly interrupted safely; real-model shutdown latency and long active/idle hardware soaks remain part of the release matrix in `TEST_PLAN.md`.

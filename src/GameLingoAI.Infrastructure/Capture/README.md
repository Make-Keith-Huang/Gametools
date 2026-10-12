# Windows capture adapter

The adapter uses Windows Graphics Capture (WGC) on Windows 10 version 1903 or later. It has no Desktop Duplication fallback and does not inject code, hook graphics APIs, inspect process memory, persist screenshots, or capture audio.

## Consent and capability

`WindowsGraphicsCaptureSourceFactory` accepts only a `ConfirmedCaptureWindow`, which can be produced by `WindowSelectionTracker` only after explicit user confirmation. Native process identity is checked again at every start so a closed window or reused handle cannot silently become a new target.

Capability detection checks the OS version and `GraphicsCaptureSession.IsSupported`. Messaging tells users that exclusive-fullscreen targets may require borderless or windowed mode. A minimized target is rejected with a recoverable instruction to restore it.

## Frame and resource ownership

- WGC and D3D11 produce `B8G8R8A8UIntNormalized` frames.
- The capture source copies each accepted surface into tightly packed managed BGRA memory. The yielded `CaptureFrame` belongs to its consumer and must be disposed after local preprocessing/OCR.
- Pixels are local-only. They are not logged, written to disk, or valid translation-provider input.
- Sequence numbers and UTC timestamps increase strictly across repeated starts of one source.
- The native frame pool and managed latest-frame buffer both have capacity two. When the consumer falls behind, the oldest native frame is disposed and only current content is retained.
- Capture sessions, frame pools, frames, D3D devices, timers, cancellation sources, and queued frames are released on cancellation, stop, target close, startup failure, and source disposal. CsWinRT owns the projected capture-item reference; manually disposing its cached native object can invalidate a later session.

## Runtime behavior

- A size change disposes the transition frame and recreates the frame pool at the new dimensions.
- Zero-size or minimized targets produce no frames until usable again.
- DXGI device-removed, device-hung, and device-reset errors recreate all session/device resources up to three times. Other failures are not retried blindly.
- A rapid restart can briefly return Windows resource-state error `0x8007139F`; startup retries that exact error at most five times with target revalidation and 100 ms delays. Other startup errors remain actionable.
- A lightweight window monitor makes target close terminate promptly even when WGC emits no final event.
- Only one enumeration may run per source. Ending or cancelling it releases the run gate, so repeated start/stop is idempotent and does not overlap native sessions.

## Verification

Automated tests cover bounded newest-frame behavior, dropped-frame disposal, cancellation, error propagation, consent, minimized validation, device-loss classification, ordered BGRA output, live resize, target close, and 50 real start/stop cycles against a synthetic app window.

Latest observed 50-cycle target-process private-memory delta: 1,159,168 bytes. This is below the 128 MiB regression ceiling and the run completed without a hang or surviving capture target.

Manual verification:

1. Confirm a normal visible test window in **Window/Region**.
2. Start capture and verify locally that frames match its client content and remain ordered.
3. Resize, minimize, restore, and close the target; verify resize resumes at the new size, minimization pauses, restore resumes, and close reports a recoverable error.
4. If an exclusive-fullscreen application cannot be captured, switch it to borderless/windowed mode; do not enable an unsupported fallback.

# TASK-006 — Windows Graphics Capture Adapter

## Goal

Implement safe, bounded capture of the explicitly selected window using Windows Graphics Capture.

## Preconditions

TASK-005 complete; Windows 10 1903+; read capture architecture and privacy rules.

## Allowed modification scope

Core capture contracts if strictly necessary, `Infrastructure/Capture/**`, capture-specific tests/harness, composition, and capture docs.

## Implementation requirements

- Create capture items only for the confirmed target and use supported Windows Graphics Capture APIs.
- Emit BGRA frames with monotonic sequence/timestamp and documented buffer ownership.
- Use a capacity-two latest-frame policy; adapt to resize; pause on minimize/zero size; recover device loss.
- Make start/stop idempotent, cancellation responsive, and all COM/D3D/frame-pool resources deterministic.
- Add capability detection and clear unsupported/exclusive-fullscreen messaging.

## Prohibited

No Desktop Duplication fallback without design approval; no DLL/graphics API injection, hooks, process memory, drivers, anti-cheat bypass, disk screenshot persistence by default, or audio.

## Acceptance criteria

Test window capture produces correctly sized ordered frames; resizing recreates resources safely; repeated start/stop and target close do not leak or hang; queue stays bounded; pixels never leave local capture/OCR boundary.

## Build/test

Add fake-source unit tests and Windows integration/soak harness. Run full tests plus 50 start/stop cycles and report memory/resource observations.

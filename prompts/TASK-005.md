# TASK-005 — Window Discovery and Explicit Selection

## Goal

Enumerate eligible top-level Windows windows and let the user explicitly select a capture target with safe metadata and refresh behavior.

## Preconditions

TASK-002 and TASK-004 complete; supported Windows target available.

## Allowed modification scope

Core window-selection contracts, Infrastructure Windows discovery interop, App selection ViewModel/view, tests, and relevant docs.

## Implementation requirements

- Enumerate visible, non-cloaked, non-tool windows with non-empty client area; exclude this application and overlay.
- Return opaque ID/handle wrapper, title, process display hint, bounds, DPI, minimized state, and optional safe thumbnail.
- Refresh without UI freezes; handle windows disappearing or handles being reused.
- Require explicit user selection/confirmation and expose recoverable validation states.
- Release all native resources deterministically.

## Prohibited

No process-memory access, module inspection for evasion, injection, hooks, anti-cheat bypass, game-file scanning, automatic capture without consent, or audio.

## Acceptance criteria

Synthetic test windows appear and update; closed/minimized windows are handled; own windows are excluded; selection survives refresh only while identity remains valid; native error paths are actionable.

## Build/test

Unit-test filtering/identity with fakes and add Windows integration tests for open/rename/minimize/close. Run full build/tests and document manual verification.

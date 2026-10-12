# TASK-020 — Session Controls and Global Hotkeys

## Goal

Provide safe start/pause/resume/stop controls and configurable global hotkeys with clear session feedback.

## Preconditions

TASK-017 through TASK-019 complete; state machine and profile validation stable.

## Allowed modification scope

Core command/state additions if necessary, App session controls/ViewModels, Infrastructure hotkey adapter, settings, tests, and user docs.

## Implementation requirements

- Expose start, pause/resume, stop, hide/show overlay, and emergency stop; commands reflect valid state and prevent reentrancy.
- Register user-selected hotkeys through supported Windows APIs, detect conflicts, unregister deterministically, and avoid text input interception.
- Emergency stop cancels pipeline/hides overlay quickly without terminating the app or losing settings.
- Display selected window/profile/provider, state, current latency, and actionable errors.
- Persist hotkey definitions without capturing arbitrary keystroke history.

## Prohibited

No keyboard hooks unless separately justified and approved; no input simulation, macro/automation behavior, gameplay control, audio, injection/hooks into games, memory access, or game-file changes.

## Acceptance criteria

Commands follow state transitions; rapid repeated input is safe; conflict/registration failure is actionable; hotkeys work while another app is focused without stealing keys; emergency stop hides and cancels within two seconds; all registrations release on exit.

## Build/test

Unit-test command/state logic and fake hotkey adapter; add Windows integration tests for register/conflict/invoke/unregister and manual game-safe smoke. Run full build/tests.

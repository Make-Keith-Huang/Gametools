# TASK-007 — Capture Region Editor and Coordinate Mapping

## Goal

Let users preview a selected window and create, move, resize, name, order, and persist normalized subtitle regions accurately across DPI and resize changes.

## Preconditions

TASK-005 and TASK-006 complete; read `docs/GAME_PROFILE.md` and overlay coordinate rules.

## Allowed modification scope

Core region models, App preview/editor views and ViewModels, coordinate services, profile draft persistence, tests, and docs.

## Implementation requirements

- Display throttled local preview frames and editable region handles with keyboard nudging.
- Convert among preview DIPs, capture pixels, client bounds, and normalized coordinates through a tested service.
- Clamp regions to content, enforce minimum size/count, support undo/reset and multi-region ordering.
- Persist only normalized geometry, not screenshots; account for letterboxing and per-monitor DPI.
- Pause or isolate high-rate pipeline work during layout editing.

## Prohibited

No remote image upload, screenshot persistence by default, input simulation, game-process access, injection/hooks, audio, or out-of-bounds coordinates.

## Acceptance criteria

Regions round-trip within one pixel at supported DPI; resize/move/monitor changes preserve relative position; invalid geometry cannot save; editor is keyboard accessible; preview resources release on navigation.

## Build/test

Property-test coordinate transforms and boundaries; add ViewModel tests and manual mixed-DPI checks at 100/150/200%. Run full build/tests.

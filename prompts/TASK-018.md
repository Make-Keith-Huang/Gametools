# TASK-018 — Transparent Click-Through Overlay

## Goal

Implement a DPI-correct, non-activating, transparent subtitle overlay that follows the selected window and never steals gameplay input in running mode.

## Preconditions

TASK-017 complete; read `docs/OVERLAY_DESIGN.md`; use a synthetic target window for tests.

## Allowed modification scope

Overlay contracts/models, `App/Overlay/**` and Windows interop, overlay settings/ViewModels, tests/harness, and overlay docs.

## Implementation requirements

- Create a borderless transparent WPF overlay; running mode applies click-through/no-activate/tool-window styles and avoids taskbar/Alt-Tab presence.
- Track target client bounds across move/resize/monitor/DPI/minimize/cloak/close and hide promptly when invalid.
- Render sanitized plain text with configurable font, outline/background, alignment, line/length limit, anchor, and offset.
- Marshal minimal work to Dispatcher; reject stale session/sequence updates; provide an explicitly interactive layout-edit mode.
- Restore/clean native styles and event hooks on disposal.

## Prohibited

No input interception/simulation, game overlay injection, DirectX/Vulkan/OpenGL hooks, game-process memory, markup execution, audio, or game-file modification.

## Acceptance criteria

Mouse clicks reach a synthetic window beneath running overlay; focus never changes on show/update; alignment is correct at 100/150/200% and mixed-DPI monitors; minimize hides within 250 ms; old results never replace new; repeated open/close leaks no hooks/windows.

## Build/test

Add renderer/ViewModel/coordinate tests and a Windows UI harness for click, focus, bounds, DPI, lifecycle, and stale updates. Run full build/tests and document manual evidence.

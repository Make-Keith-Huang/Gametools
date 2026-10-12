# Overlay Design

## Goals

The overlay displays translated subtitles near the source region, remains legible across varied scenes, tracks the selected window across monitors/DPI changes, and does not interfere with gameplay.

## Window behavior

- Separate borderless WPF window, transparent, topmost only relative to the configured experience.
- Apply `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW` in click-through mode.
- Never activate on show; exclude from taskbar and Alt-Tab as appropriate.
- Follow target client bounds using supported window events/polling; hide while minimized, cloaked, closed, or invalid.
- Convert physical capture pixels and per-monitor DPI carefully. Recompute on move, resize, monitor, and DPI change.

## Rendering

Default: centered white semibold text, dark outline plus translucent rounded background, maximum two to three lines, safe margins, and subtle 120 ms transitions. Expose font family, size, weight, foreground, outline, background opacity, max width, alignment, anchor, and vertical offset per profile.

Render provider output strictly as text. Strip control characters, limit length/lines, and do not interpret markup, links, or templates.

## Modes

- Running: click-through and non-activating.
- Layout edit: intentionally interactive with visible handles; pipeline may be paused.
- Paused/faulted: overlay hidden by default, optional small status badge outside gameplay area.

## Acceptance probes

Verify clicks reach a test window beneath the overlay, focus never moves during an update, bounds stay correct at 100/150/200% DPI, stale sequences cannot overwrite new text, and target minimization hides within 250 ms.

## TASK-018 implementation

`App/Overlay` owns the WPF window, outlined plain-text renderer, native metadata probe, latest-slot controller, interactive drag handles and settings editor. Core owns validated options, physical-pixel placement and session/sequence rejection. The application manifest declares PerMonitorV2 awareness; native placement uses physical screen pixels (including negative coordinates) and rechecks the overlay's own DPI after moving across monitors.

Running windows use `WS_EX_LAYERED` (WPF transparency), `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, `ShowActivated=false` and no taskbar entry. Only the application's own HWND receives a WPF message callback; no game/window event hooks, input injection or input simulation are installed. The callback rejects activation and returns transparent hit testing in running mode. Styles are restored and the callback detached before window disposal. This follows Microsoft's [layered-window hit testing rules](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features) and [per-monitor DPI guidance](https://learn.microsoft.com/en-us/windows/win32/hidpi/declaring-managed-apps-dpi-aware).

One 33 ms UI timer reads target bounds/visibility/DPI/foreground and a latest result slot. It never queues one UI action per OCR frame. Unchanged positions/styles/text do not trigger redundant native placement or redraw. Target PID/start marker are revalidated; invalid identity closes the overlay. Minimized, cloaked, invisible or non-foreground targets hide on the next poll. Explicit layout-edit mode may remain visible while its handles are active. The existing sequence guard prevents foreign-session/late results from replacing the current subtitle.

Rendering uses `FormattedText.BuildGeometry` with fill/outline, a translucent background, validated local font family names, alignment, anchor and bounded offsets. Provider text never enters XAML/HTML or URI loading. Logical newlines and wrapped text are bounded by line/character/height limits; control and bidi-format characters are removed. Showing uses a 120 ms fade; hiding for invalid/minimized targets is immediate.

Home → **Overlay appearance and layout** exposes settings load/save. **Edit overlay layout** explicitly pauses translation and enables blue drag/width handles. **Done** restores click-through and hides the edit preview. **Resume** is explicit. Dragged options update the settings editor; save them to retain them for subsequent starts. Active-session editing stays attached to the original confirmed session target even if the window-selection editor changes.

## Automated and manual harness

```powershell
dotnet run --project tests/GameLingoAI.Tests/GameLingoAI.Tests.csproj -c Release --no-build -- overlay
```

Automated tests cover validation, Unicode/plain-text limits, state isolation, placement at 96/120/144/192 DPI, settings success/failure/cancellation, native focus/styles/hit-test/bounds, minimized hiding, invalid/probe-fault cleanup, rendering and 50 HWND create/dispose cycles. The native hit-test is not claimed as an actual physical click.

The test executable shares the application's PerMonitorV2 manifest and explicit WPF DPI-scaling runtime configuration. A thread-only awareness override is insufficient to represent the application: manual testing exposed black client borders and clipped overlay content on a 125% → 100% transition with that older harness configuration. The regression test moves its owned synthetic window onto every connected monitor, checks native versus WPF DPI, verifies rendered content fills the physical client area, and checks overlay placement. On the development host it passes at native DPI 120/WPF 1.25 and native DPI 96/WPF 1.00; visual confirmation remains a separate manual check.

For real click delivery and physical mixed-DPI evidence, use the explicit user-operated harness (no input is simulated):

```powershell
$env:GAMELINGOAI_OVERLAY_MANUAL = '1'
dotnet run --project tests/GameLingoAI.Tests/GameLingoAI.Tests.csproj -c Release --no-build -- 'overlay Windows harness'
Remove-Item Env:GAMELINGOAI_OVERLAY_MANUAL
```

Wait for the MANUAL subtitle, click its covered button area and check the actual-click counter. Move/resize/minimize/restore the target, drag it between differently scaled physical displays, then close it. The harness reports click count; zero clicks is explicitly not proof of delivery. Record monitor/scaling details and observed behavior in `TASK_EXECUTION_PROGRESS.md`. Full physical/manual acceptance is pending until that evidence exists; automated coordinate tests do not substitute for hardware evidence.

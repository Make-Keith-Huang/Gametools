# Capture region editor

The Window/Region page provides an isolated, throttled local preview after the user explicitly confirms a capture target. Preview pixels stay in memory, are never written to the region draft, and never enter a translation-provider request. Leaving the page, losing confirmation, pressing **Stop preview**, or disposing the shell cancels the preview capture and releases its WGC resources.

## Coordinate model

Normalized `[0,1]` geometry is the only persisted geometry. `RegionCoordinateMapper` converts among:

- capture pixels;
- client-area screen pixel bounds;
- WPF preview device-independent pixels (DIPs);
- normalized region coordinates.

The preview uses uniform scaling. Its content rectangle is computed from capture size and per-axis DPI, then centered in the available preview viewport; unused space is treated as letterboxing. Pointer coordinates are translated relative to that content rectangle and clamped before creating a validated normalized region.

Normalized edges are rounded independently when mapped to client pixels. Property tests cover 500 randomized regions at each of 96, 144, and 192 DPI and require normalized/preview/client round trips to remain within one capture pixel.

## Editing rules

- One to eight regions are allowed.
- Each region has a non-empty name, stable ID, unique contiguous order, and validated normalized bounds.
- The minimum editable size is 8 × 8 capture pixels.
- Dragging and resizing clamp to captured content; coordinates cannot extend into letterboxing or outside `[0,1]`.
- Add, delete, reorder, undo, reset, load, and save actions are exposed as commands.
- A focused move handle accepts arrow keys for one-DIP movement. Shift+arrow resizes by one DIP.
- Resize and DPI changes recompute the preview projection from normalized geometry, preserving relative placement.

## Draft persistence

The working draft is atomically written to `%LOCALAPPDATA%\GameLingoAI\region-draft.json`. It contains only profile identity, region IDs/names/order, normalized `x/y/width/height`, and a UTC update timestamp. It contains no image, screenshot, capture pixel buffer, private path, credential, history, or OCR text. Invalid counts, duplicate IDs/orders, invalid names, and invalid normalized geometry cannot be saved by the editor.

## Manual mixed-DPI verification

Run this matrix on Windows with a synthetic resizable target:

1. At 100%, 150%, and 200% display scale, confirm the target and start the local preview.
2. Draw a region against known pixel landmarks; move and resize it with both mouse and keyboard.
3. Resize the target and the GameLingoAI window. Confirm the region remains at the same relative content position and never enters letterboxing.
4. On a mixed-DPI dual-monitor setup, move both target and editor between monitors and repeat at each scale.
5. Navigate to Diagnostics and confirm the preview stops and its image is cleared; return and restart it explicitly.
6. Save and reload the draft, then inspect the JSON to confirm only normalized geometry and metadata are present.

Automated tests cover the coordinate/DPI matrix and preview cancellation. Changing the host's physical display scale is intentionally left to the manual Windows matrix because the automated runner must not reconfigure the user's monitors.

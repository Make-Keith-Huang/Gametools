# Window discovery and explicit selection

`WindowsWindowDiscovery` enumerates top-level desktop windows on a worker task. It includes only visible, non-cloaked, non-tool windows with a non-empty client area and title. The current process and caller-supplied overlay window IDs are excluded.

Returned metadata is deliberately limited to an opaque window ID, title, process display hint, client bounds, DPI, minimized state, process ID, and process start marker. Discovery does not inspect process memory, modules, game files, or window contents, and it does not capture a thumbnail.

## Identity and consent

`WindowIdentity` combines the native window ID with the owning process ID and process start marker. A title or bounds change therefore preserves a selection, while a closed window or reused handle invalidates it. A user must select and explicitly confirm a target before the selection reaches `Ready`; refresh never starts capture automatically.

The Window/Region page exposes refresh, selection, confirmation, recoverable validation state, minimized state, and a safe error message. Native enumeration failures are returned as typed, actionable failures rather than escaping through the UI.

## Verification

The automated Windows integration test starts the built application as a synthetic target and verifies open, rename, minimize, restore, and close behavior. Some Windows desktop configurations cloak WPF windows while minimized. In that valid case the target is filtered until restored; otherwise it remains listed with `IsMinimized=true`. The test also checks that this process is excluded and that cancelled refreshes do not surface as errors.

Manual check:

1. Open **Window/Region** and choose **Refresh windows**.
2. Confirm that this application's own shell and overlay are absent.
3. Select another visible window and choose **Confirm selected window**.
4. Rename, minimize/restore, and close that target, refreshing after each change.
5. Confirm that identity survives metadata changes, minimized/cloaked state is handled, and closing the target produces the recoverable `TargetUnavailable` state without starting capture.

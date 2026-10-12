# WPF application shell

The shell uses constructor-injected ViewModels and commands. Views contain only presentation and view wiring; no capture, OCR, translation, persistence, or session business logic runs in code-behind.

## Navigation

The left navigation exposes placeholders for Home/Session, Window/Region, Provider, Profiles/Settings, Diagnostics, and About. `ShellViewModel` owns the current `PageId`, so navigation can be tested without creating a WPF window.

All visible strings and reusable styles live in `App.xaml`. Controls use system brushes as high-contrast foundations, access-key underscores, a predictable tab order, wrapping text, and scalable WPF device-independent units.

## Lifetimes

- Singleton: error reporter, observable session status, and shell ViewModel.
- Scoped: `SessionContext`, which owns a session ID and cancellation source.
- Transient: windows and placeholder page ViewModels.

The service provider validates the graph and scope rules during startup. Startup failures show a fixed, actionable message without exception details or secrets and then shut down cleanly.

`AsyncRelayCommand` prevents duplicate execution, exposes cancellation, catches expected cancellation, and routes all other exceptions through the safe error reporter so an unhandled command exception cannot terminate the UI.

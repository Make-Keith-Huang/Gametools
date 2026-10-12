# TASK-004 — WPF MVVM Shell and Dependency Injection

## Goal

Create the accessible WPF application shell, navigation, ViewModels, commands, and DI lifetimes needed by later features.

## Preconditions

TASK-002 and TASK-003 complete; read UI, overlay, and architecture requirements.

## Allowed modification scope

`src/GameLingoAI.App/**`, composition extensions in Infrastructure, UI/ViewModel tests, and UI documentation.

## Implementation requirements

- Implement MVVM without business logic in views; add home/session, window/region setup, provider, profile/settings, diagnostics, and about navigation placeholders.
- Show observable session state and validation errors; commands support cancellation and prevent duplicate execution.
- Define correct singleton/session/transient lifetimes and validate the service graph at startup.
- Use resources for strings/styles/colors and support keyboard navigation/high contrast foundations.
- Handle startup failure with a safe, actionable dialog and clean shutdown.

## Prohibited

No capture/OCR/provider implementation, blocking UI calls, service locator, hidden global state, audio features, injection/hooks, or game modification.

## Acceptance criteria

App starts, navigates, and closes cleanly; ViewModels are testable without WPF window creation; composition resolves all declared services; no unhandled command exception crashes the UI.

## Build/test

Add ViewModel/command/lifetime tests. Run format check, Release build, tests, and a manual startup/navigation/keyboard smoke test.

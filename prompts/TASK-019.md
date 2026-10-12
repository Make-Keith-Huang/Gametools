# TASK-019 — Game Profiles and Settings UI

## Goal

Deliver create/edit/duplicate/import/export/delete workflows for validated game profiles and all V1 settings except secrets.

## Preconditions

TASK-007, TASK-018, and TASK-021 complete; read profile/database/privacy designs.

## Allowed modification scope

Profile application services/repositories, App profile/settings views and ViewModels, import/export DTOs, tests, and docs.

## Implementation requirements

- Support profile identity/name, window hints, languages, regions, OCR preset/threshold, provider/model/secret reference, overlay style, capture FPS, cache/history privacy.
- Use transactional validation/save and unsaved-change prompts; preserve last-known-good profile on failure.
- Version JSON import/export, preserve safe unknown fields when possible, and exclude credentials/history/cache/private paths.
- Duplicate generates new IDs; delete explains cascades and requires confirmation.
- Never auto-start capture after import or window-hint match; require user confirmation.

## Prohibited

No plaintext secrets, captured images in profiles, automatic game modification, process memory/module scanning, injection/hooks, audio settings, or silent destructive migration.

## Acceptance criteria

Every setting round-trips; invalid profiles cannot start/save; imports are size/schema bounded and transactional; exports contain no secrets; duplicate/delete semantics match database rules; UI is keyboard accessible and responsive.

## Build/test

Add ViewModel, repository, validation, JSON golden/forward-version, secret-absence, transactional failure, duplicate/delete, and large-hostile import tests. Run full build/tests and manual workflow smoke.

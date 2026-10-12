# Game Profile

## Purpose

A profile captures all non-secret choices required to restart translation for one game/window style. Profiles are portable JSON plus database records; exports omit history, cache, absolute private paths, and credentials.

## Logical shape

```json
{
  "schemaVersion": 1,
  "id": "guid",
  "name": "Example Game",
  "windowMatch": { "processHint": "game", "titlePattern": "Example.*" },
  "languages": { "source": "ja", "target": "zh-Hans" },
  "capture": { "fps": 8, "regions": [{ "name": "Dialogue", "x": 0.1, "y": 0.72, "width": 0.8, "height": 0.22 }] },
  "ocr": { "languageModel": "japan", "confidence": 0.65, "preprocessPreset": "outlined-subtitle" },
  "translation": { "providerId": "ollama", "model": "model-name", "secretRef": null },
  "overlay": { "fontSize": 30, "maxLines": 3, "anchor": "BottomCenter", "offsetY": -90 },
  "privacy": { "historyEnabled": false }
}
```

## Validation

Regions must be finite, normalized, positive, and contained within the frame. FPS is 1-30. Language tags follow BCP 47 where possible. Provider/model IDs are length-limited. Regex window matching has a timeout and falls back to exact/manual selection. Unknown fields are preserved when safe; unsupported future schema versions open read-only.

The region editor persists its working draft separately as normalized geometry only. Preview DIPs, capture pixels, client bounds, DPI-specific projections, and preview images are transient and are recalculated when the target or monitor changes; they are never profile data.

## Window matching

Hints assist discovery but never auto-authorize capture. The user confirms the window at least once. Do not identify or interact with games through memory inspection, module enumeration for evasion, injection, hooks, or game-file scanning.

## TASK-019 implementation checkpoint

`GameProfile.Settings` now includes window hints, provider/model/credential lookup reference, OCR model/confidence, capture queue capacity, complete overlay/cache/stabilization options and log retention. SQLite migration 5 persists these choices in the same transaction as regions. The repository and portable document share `GameProfileValidator`; failed settings/region writes preserve all prior fields. The editor and explicit saved-profile session selection are integrated as described below; remaining acceptance work is listed at the end.

`ProfileJson` implements portable schema 1 as a bounded, side-effect-free codec. The logical example above is illustrative; actual exported fields include region `id`/`sortOrder`, `capture.queueCapacity`, `ocr.preprocessing` (the full validated preset/options record), complete `overlay`, `cache`, `stabilization`, and `retainedLogFileCount`. Enums are camel-case names, not integers. Valid identities and at least one region are required. Document limits are 128 KiB UTF-8 and 16 levels; duplicate property names (including case variants) are rejected. Unsupported future versions return a read-only result with no editable profile. Parsing never writes SQLite or starts capture.

Export omits the local credential reference as well as credential values. Import ignores credential-reference fields, remote consent and arbitrary unknown private data; users must explicitly reselect a credential locally. History *configuration* can round-trip, but no history/cache content, endpoints, custom headers, screenshots or model paths are part of this document. Explicit export includes the user-entered profile name/window hint, so those user-visible labels should not contain private information.

Only root `x-*` extensions with finite numeric or boolean values are retained (at most 32 fields / 4 KiB, ASCII alphanumeric/hyphen names at most 64 characters). Unknown strings, objects, arrays and nested fields are omitted because their privacy meaning cannot be established. This limited forward preservation is intentional and must be explained by the importing UI. Local duplication regenerates both profile and region IDs while retaining settings; import-to-new persistence is still the editor's responsibility.

The Profiles/Settings page now exposes `ProfilesViewModel` commands for refresh, explicit load/new/save, duplicate, import-as-new, sanitized export and confirmed deletion. The advanced JSON draft is separate from the import/export buffer and the local credential-reference field. Dirty drafts require an explicit discard confirmation before replacement, and closing the application prompts before discarding them. Navigation alone retains the draft. Imports remain unsaved until Save; imported future schemas are read-only. A conflicting region write rolls back the entire saved profile and leaves the draft available. Commands serialize operations, disallow edits while persistence is running, and cancel/drain before database shutdown.

The page also exposes grouped form controls for the allowlisted portable fields: text/numeric entries and boolean checkboxes, including normalized region coordinates and order. Identity/version/extensions are not editable form fields. Region add/remove commands keep at least one and at most 32 regions. Optional window hints and OCR threshold can be cleared even after loading a non-null value. Incomplete numeric input stays visible with an error and blocks save/export/duplication rather than silently saving its previous value. Starting an advanced JSON edit removes the old form, and explicit Reload form validates before rebuilding it; unfinished invalid form input cannot be silently discarded by this switch. Full OCR preset buttons replace the complete preprocessing record; further parameter edits mark it Custom. Persistence-time validation still checks ranges and cross-field constraints.

The Home/Session page now loads saved profiles alongside local providers. Choosing a saved profile clears the previous window confirmation; the user must explicitly confirm again. Start rereads that profile from SQLite (not the editor draft or stale list snapshot), resolves its model/reference against exactly one local provider, applies regions/OCR/capture/cache/stabilization/overlay/history settings and passes its persisted ID to history recording. Deleted or invalid profiles fail before capture setup. The ad-hoc button restores the prior global-settings/region-editor workflow. `ProfileSessionSettings` normalizes supported OCR aliases (including japan → ja), copies mutable region/header lists, and preserves local remote consent rather than granting it. OpenAI-compatible saved profiles require an explicit local credential reference; a missing profile reference does not silently inherit the provider's credential reference.

`ProfileWindowHints` suggests only from already discovered safe window metadata: exact case-insensitive process-display hint and a culture-invariant title regex. Work is capped at 256 candidates, 20 results, a 100 ms loop budget plus the current 10 ms regex match, and 4,096 title characters; cancellation is checked per candidate. Timeout falls back to exact title equality/manual selection. Home runs matching off the UI thread and displays suggestions without selecting or confirming a window. Refresh/select/confirm remain explicit actions on Window/Region.

Real SessionViewModel/DI tests with a temporary SQLite database now prove switching profiles clears confirmation, an unconfirmed profile never reaches resource creation, Start rereads edits made after list selection, expected regions/model/settings reach the resource factory, deleted profiles cannot reuse a stale snapshot, and switching back to ad-hoc clears authorization. The inspecting factory intentionally stops before capture creation; these are not real-game or end-to-end OCR tests.

STA tests now instantiate the actual profile view, check visible inputs' accessible names/tab-stop/focus support, and verify text/checkbox bindings update the draft. A real SQLite/presenter integration test checks that a displayed synthetic result retains its saved-profile foreign key and that deleting that profile preserves history with a null profile link. This complements the saved-profile Start request tests; it is not a real OCR session test.

Interactive smoke verified name editing, transactional save and duplicate-draft creation in an isolated `--synthetic-test` database. After the user authorized resuming Computer Use following an Escape stop, the remaining smoke passed: Tab moved from Name to the next group; Alt+S/Alt+E/Alt+I saved/exported/imported; the imported profile and regions received distinct IDs; import did not start capture; closing an unsaved draft prompted and No retained it. Destructive deletion and privacy controls were verified only in isolated automated tests, not through desktop input. TASK-019 is accepted; native OCR with an external model remains a separately documented opt-in check.

The editor concurrency tests cover blocked save, competing commands and shutdown cancellation/drain. Session provider/profile reads check cancellation again after each await, so a read completed near shutdown cannot apply a late UI update; a delayed-read STA regression reproduces and guards this race.

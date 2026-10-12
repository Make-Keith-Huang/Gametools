# Glossary and shared translation prompts

## Terms and scope

`GlossaryEntry` is an immutable record with an ID, optional profile ID, source/target language codes, source/target terms, `CaseSensitive`, and `Enabled`. A null profile ID means global. Language codes are trimmed and lowercased invariantly; terms are trimmed and normalized to Unicode NFC. Language codes are limited to 32 UTF-16 code units and each term to 100. Blank terms, control characters, invalid Unicode, empty IDs, and an empty non-null profile ID are rejected. A catalog has at most 2,000 entries and unique IDs.

Selection considers only enabled entries for the requested language pair and either the current profile or global scope. Matching uses NFC-normalized subtitle text and ordinal literal substring matching, with `OrdinalIgnoreCase` only when the term allows it. This works without word separators in CJK text; it does not use regex, culture-sensitive casing, fuzzy matching, or executable templates.

Conflicts are grouped by case-insensitive source spelling. For a matched group, a profile entry takes precedence over a global entry, followed by a case-sensitive entry over a case-insensitive entry, then ordinal source spelling and ordinal target spelling. Only one translation is supplied for a case-folded source group. Disabled entries do not suppress an enabled global fallback. Different, overlapping phrases are retained and ordered longest-source-first, then ordinally. These rules are independent of import order, IDs, host culture, and process.

After precedence is resolved, selection enforces all three budgets: 32 terms, 1,024 combined source/target UTF-16 code units, and 1,024 estimated glossary tokens by default. The provider-neutral token estimate conservatively counts the UTF-8 bytes of the actual escaped glossary JSON, including brackets, commas, keys, and flags. It is an admission estimate, not provider-reported token usage; no model tokenizer or network dependency is required. A term exceeding the remaining budget is skipped so a later smaller term can fit. Budgets can be supplied explicitly within hard bounds of 64 terms, 4,096 code units, and 4,096 estimated tokens.

## Revision and interchange

`GlossaryCatalog.Revision` returns a lowercase SHA-256 hex digest of a versioned canonical UTF-8 JSON value for the active language pair and profile scope. Enabled relevant entries are sorted ordinally and identical semantic duplicates are removed. Source/target spelling, case sensitivity, global/profile scope, and language pair affect the digest. IDs, input order, disabled entries, and entries for other profiles/language pairs do not. Enabling, disabling, deleting, editing, or moving a relevant term invalidates the digest when its effective semantics change. The revision intentionally covers all relevant enabled entries, not only terms matching one subtitle, so one revision can accompany a profile's cache requests.

Export is JSON with top-level `Version: 1` and `Entries`. Entry properties are `Id`, `ProfileId` (null or an object with `Value` containing a GUID), `SourceLanguage`, `TargetLanguage`, `SourceTerm`, `TargetTerm`, `CaseSensitive`, and `Enabled`. No credentials, timestamps, screenshots, or translated subtitle history are included.

Import constructs a new immutable catalog before the caller swaps any state. Unsupported versions, malformed JSON, unknown/duplicate properties, duplicate IDs, invalid entries, depth greater than eight, more than 2,000 entries, and input over 4 MiB are rejected. The size limit accommodates export of a full catalog of maximum-length Unicode entries. Failed imports leave the current catalog untouched. Import replaces the snapshot rather than silently merging conflicting files.

`IGlossaryRepository` now persists to SQLite. In Profiles/Settings, choose **Load saved glossary** first; add/update/remove and JSON import edit a local draft, while **Save glossary** commits it atomically. Loading refuses to discard an unsaved draft unless the explicit discard checkbox is selected. Export remains available to preserve a conflicting draft. Saving compares the entire loaded baseline within the write transaction; if another instance changed stored entries, the save is rejected rather than overwriting it. A constraint/cancellation failure rolls back the replacement, including any rows already inserted. Scoped entries require a profile that exists in SQLite. A failed save retains the editable draft and last saved data.

Selecting a row loads its editable text, scope, language pair and flags. Import/export and preview perform no network calls. TASK-017 uses an immutable snapshot for each session; later glossary edits affect the next session, not an active one.

## Version 2 message plan

A 2026-10-07 language-code explanation experiment (v3) was withdrawn after strict real qwen2.5:3b checks still found incomplete translation. The current shared prompt and cache golden vector remain v2. The separately verified Ollama temperature omission was fixed; an Ollama-specific configuration revision invalidates older cached output. Three final two-fixture meaning checks passed, but broader small-model quality acceptance remains open. See RELEASE_AUDIT.md for passing and failing experimental evidence.

`TranslationPromptBuilder` creates one fixed system instruction and one serialized user-data message. The system instruction specifies translation only, source meaning and terminology, configurable line-break/tag preservation, and no commentary or code fences. Source text, context, languages, and glossary are always JSON data, never inserted into system instructions or message roles. A request carries immutable `TranslationPromptOptions`; adapters preserve those options when choosing their configured model.

The data object contains `sourceLanguage`, `targetLanguage`, `text`, `context`, and the selected `glossary`. It preserves the original source exactly, including line endings and tags; source text is limited to 4,000 characters, context to eight strings of at most 1,000 characters. Invalid input becomes a typed provider validation failure before HTTP. Prompt construction does not resolve secrets, evaluate strings, or perform network or filesystem access. These boundaries prevent data from changing request roles; they do not claim a language model can never be influenced by hostile source content.

The preview displays the exact system string and user JSON, including JSON Unicode escapes, using the same builder as transport. It contains only the user's manual sample and glossary data, with no credential resolution. Editing prompt inputs or glossary contents clears a stale preview.

- OpenAI-compatible maps the plan to two messages with literal `system` and `user` roles.
- Ollama maps the same strings to `system` and `prompt`, with streaming disabled, and sends both the configured `num_predict` limit and `temperature` (previously temperature was omitted).
- Custom HTTP maps the same bounded data into its existing exact tokens. Optional `{{systemPrompt}}` and `{{promptData}}` string tokens carry the full shared messages for chat-style endpoints. Tokens in a `role` field are rejected. A plain translation endpoint can continue using `{{text}}`, context, and glossary without a system-message field; its server controls translation behavior.

Automated acceptance includes Unicode/case/culture/conflict fixtures, independent budgets, a golden revision vector, full-catalog Unicode import/export, transactional rejection, hostile message-shaped strings, editor operations, and full fake-HTTP request snapshots for all three adapters. No live provider or credential is required.

# TASK-012 — OpenAI-Compatible Translation Provider

## Goal

Implement a configurable OpenAI-compatible text translation adapter using the shared provider foundation.

## Preconditions

TASK-011 complete; a fake compatible server/recorded schema is available; secret storage works.

## Allowed modification scope

`Infrastructure/Translation/OpenAICompatible/**`, provider options/UI, adapter tests, composition, and provider docs.

## Implementation requirements

- Support validated HTTPS base URL, model, secret reference, temperature/max-output limits, and an explicitly selected supported endpoint dialect.
- Build versioned system/user messages that treat OCR text as data and preserve requested line breaks/glossary.
- Parse content and optional usage defensively; map errors to shared categories; honor shared timeout/rate/retry policies.
- Send authorization only to the configured origin and redact it everywhere.
- Add a user-triggered connection test that sends no captured/game text.

## Prohibited

No hard-coded vendor URL/model/key, endpoint guessing that leaks credentials, image input, logging request bodies by default, SDK types in Core, audio/STT/TTS endpoints, injection/hooks, or game changes.

## Acceptance criteria

Contract fixtures produce correct requests/results; Unicode and multiline content round-trip; credentials never appear in config/logs/errors; origin validation prevents redirect credential leakage; cancellation is prompt.

## Build/test

Use fake HTTP only for automated tests; cover success, usage absent, empty choices, malformed JSON, redirect, all error classes, redaction, prompt injection-shaped OCR, and cancellation. Run full build/tests.

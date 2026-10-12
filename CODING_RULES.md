# Coding Rules

## Language and structure

- Target `net8.0-windows`; enable nullable, implicit usings, analyzers, and deterministic builds.
- Prefer small cohesive types, constructor injection, file-scoped namespaces, records for messages, and sealed implementations unless extension is intentional.
- Keep Core dependency-free. External packages and P/Invoke live in Infrastructure or App interop folders.
- Name asynchronous methods with `Async` and accept `CancellationToken` as the final parameter.

## Concurrency and resources

- Never block the UI thread with OCR, I/O, database, or HTTP work.
- Prefer `Channel<T>` with an explicit capacity and full-mode policy.
- Do not fire-and-forget without a supervised task owner and logged exception path.
- Dispose frames immediately after downstream ownership ends; document ownership for native buffers.
- Use `ConfigureAwait(false)` in library code where no UI context is required.

## UI

- ViewModels expose commands and immutable/read-only state; views contain presentation only.
- Marshal only the final property/overlay update to the dispatcher.
- All colors, fonts, sizes, and strings are resources. Support 100-250% DPI and keyboard navigation.
- Click-through state must be explicit and testable; configuration mode may temporarily accept input.

## Networking and secrets

- Use typed clients from `IHttpClientFactory`; set request timeout and response-size limits.
- Validate base URLs; HTTPS is required except loopback Ollama endpoints.
- Never serialize credentials into profiles or logs. Redact headers and query secrets.
- Treat provider output as untrusted: cap length, remove control characters, and render as text only.

## Persistence

- All schema changes use ordered migrations in a transaction.
- Use parameters, never string-concatenated SQL.
- Store UTC timestamps as ISO-8601 text or documented Unix milliseconds consistently.
- Foreign keys are enabled; destructive migrations require backup and explicit release notes.

## Testing and review

- Unit tests are deterministic and do not require network, a game, GPU, or installed OCR model.
- Integration tests use temporary directories/databases and fake HTTP handlers.
- Add contract tests for each translation provider and OCR/capture adapter boundary.
- Code review rejects any V1 prohibited feature even if disabled by default.

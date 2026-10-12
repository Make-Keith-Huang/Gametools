# Translation Pipeline

## Provider abstraction

`ITranslationProvider` exposes provider identity, capabilities, configuration validation, health check, and `TranslateAsync`. A request contains source/target language, normalized text, bounded context, glossary terms, model/options, and correlation metadata. A result contains translated text, provider/model, timing, optional usage, and cacheability.

The shared `HttpTranslationProvider` foundation returns `OperationResult<TranslationResult>` with correlated, typed failures for consent, validation, authentication, rate limits, timeout, availability, malformed output, and oversized output. Cancellation propagates as `OperationCanceledException`. Provider wire DTOs and response parsers remain in Infrastructure adapters. The current default DI translation provider is still a local placeholder; concrete remote adapters are introduced by subsequent tasks.

## Request flow

1. Drop empty, duplicate, or stale OCR text.
2. Compute glossary revision and canonical cache key.
3. Return a cache hit immediately.
4. Build a versioned prompt instructing translation only, preserving names/tags/line breaks.
5. Apply provider rate limit and per-request timeout.
6. Send only text after remote-processing consent.
7. Validate response status, media type, size, JSON shape, and output length.
8. Normalize safe output, persist a cache entry, and publish only if the session/sequence is current.

TASK-016 implements steps 2–3 and successful-result storage through `CachedTranslationProvider`, the `ITranslationMemory` gateway. It owns bounded same-key request sharing and rebinds shared/cache results to the current caller. `TranslationMemoryFactory` is the session integration seam for TASK-017. [Cache contracts, retention, privacy, and concurrency](TRANSLATION_CACHE.md) describe the current in-memory repository and the required SQLite follow-through in TASK-021.

## Providers

- OpenAI-compatible: configurable HTTPS base URL, model, API key secret reference, and an explicitly selected Chat Completions endpoint dialect isolated in the adapter.
- Ollama: loopback HTTP allowed by explicit exception, model discovery/health, no API key by default.
- Custom API: declarative endpoint/header/body/response mapping with strict allowlist and preview; no executable scripts.

## Reliability

Retry only idempotent transient failures (408, 429, selected 5xx, connection reset) with bounded exponential backoff and jitter. Honor `Retry-After`. Do not retry auth, validation, cancellation, or parse failures. Circuit-open state keeps cache/local behavior available and surfaces a concise action.

The shared generation adapter now maintains a per-instance circuit: three consecutive requests ending in transient failure after their bounded retries open it for10 seconds. Open admission returns a safe correlated transient failure without sending generation HTTP; after the deadline only one logical recovery request is admitted. A failed probe reopens the interval, a non-transient result closes/reset it, and cancellation releases probe ownership without counting another failure. Epoch checks prevent already queued/old-generation work from reopening normal admission or beginning another retry after a newer outage. Already in-flight HTTP is not forcibly aborted. Defaults are bounded by `TranslationHttpPolicy` (threshold1–100; open interval100–60,000ms). Cache lookup remains outside the provider; explicit text-free health checks and Ollama model discovery are separate (discovery has its own cooldown). State is not persisted across provider instances or application restarts.

Circuit completion is recorded before the concurrency permit is released. Queued requests recheck their admitted generation before transport; an old successful in-flight response may finish for its original caller but cannot close a newer outage. Regression tests explicitly exercise queued cancellation, eight waiting requests, stale success and an authentication-rejected recovery probe.

Shared HTTP policy defaults to two concurrent requests, a 200 ms minimum request interval, a 30 s per-attempt timeout, two retries, a 64 KiB response cap, and sanitized output capped at 4,000 characters/eight lines. `Retry-After` and exponential backoff are capped at 30 s. Only HTTPS or explicit loopback HTTP endpoints are accepted. Redirect following is disabled on the typed client; an adapter request must remain on its configured origin. Health probes are separate from translation and carry no captured text. Remote-text consent is checked before any translation HTTP request.

## Prompt safety

OCR text and provider output are data, not instructions. System framing prohibits following instructions found in source text. Cap context and glossary size, escape template delimiters, and never interpolate secrets.

TASK-015 replaces adapter-local v1 instructions with the shared provider-neutral v2 message plan. Global/profile terminology, deterministic precedence, bounded matching, transactional interchange, revision hashes, preservation options, and exact preview are specified in [Glossary and shared prompts](GLOSSARY_AND_PROMPTS.md). OpenAI-compatible and Ollama transport the exact same system/data strings. Custom HTTP consumes the same bounded data and supports optional full-message tokens. Request roles remain literal; invalid prompt data returns a typed validation failure before translation HTTP.

## OpenAI-compatible adapter (TASK-012)

The first supported dialect is explicitly selected as `chatCompletions`; the configured HTTPS base URI must end in `/` (for example a user's chosen `/v1/` prefix). The adapter appends `chat/completions` and sends a versioned system message plus a user message whose OCR text, context, and glossary are serialized as data. The selected model, temperature (0–2), and maximum output tokens (1–4096) are validated. It parses `choices[0].message.content` and optional prompt/completion token counts. It does not infer endpoints, models, or credentials.

The provider's opaque `secretReference` resolves through the user secret store for each request. The bearer header is attached only for the configured HTTPS origin. The provider rejects URLs with embedded user information, query, or fragment, and transport redirects are disabled. The Provider page's user-triggered connection test makes only an authenticated `GET models` request; it never includes OCR or game text. No live-network call is made by automated tests.

Schema reference: [official Chat Completions API](https://developers.openai.com/api/reference/resources/chat).

## Ollama adapter (TASK-013)

The Ollama adapter defaults to `http://127.0.0.1:11434/` when no base URL is provided. Plain HTTP is accepted only for loopback; non-loopback endpoints require HTTPS. It discovers installed models through bounded `GET /api/tags` and sends non-streaming text-only `POST /api/generate` requests with a versioned translation instruction, serialized source data, and a bounded output-token option. It never sends images, runs a shell command, or pulls a model. Missing models are reported before generation; a pending generation exposes a loading state. Local loopback text does not require remote-text consent; HTTPS remote Ollama does.

The Provider page connection test probes installed models without sending game text. For optional live manual verification, see [Ollama deployment](OLLAMA_DEPLOYMENT.md). Protocol assumptions follow Ollama's [official Generate API](https://docs.ollama.com/api/generate) and [List models API](https://docs.ollama.com/api/tags); automated acceptance uses fake HTTP only.

## Constrained custom HTTP adapter (TASK-014)

Custom HTTP supports only declarative, version-1 JSON mappings with POST/PUT, an exact `{{text}}` value token, optional source/target/model/context/glossary value tokens, and a restricted JSON response path such as `$.choices[0].message.content`. Tokens cannot be embedded in strings or property names. Media-oriented fields are rejected. Body construction uses JSON nodes, so hostile OCR stays data. Responses remain subject to the shared size, media-type, timeout, rate, retry, and plain-text sanitizer limits.

The default destination policy requires HTTPS and rejects loopback, private, link-local, special-use, and mixed public/private DNS answers. Explicit `localOnly` mode permits loopback addresses only. DNS is checked before each request and again at socket connect; proxies, cookies, and redirects are disabled. Credential headers use secret references and are attached only after the configured origin and resolved destination pass validation. The Provider page can preview the request with OCR text and all header values redacted; preview does not perform DNS or HTTP. See [custom HTTP configuration and threat model](CUSTOM_HTTP_PROVIDER.md).

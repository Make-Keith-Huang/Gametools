# Constrained custom HTTP provider

This advanced provider is intentionally declarative. It cannot run JavaScript, C#, shell code, environment expansion, file reads, or user-supplied expressions.

## Configuration shape

Use a provider with `kind: "customHttp"`, a selected `id`, `model`, absolute endpoint `baseUri`, `timeoutSeconds`, and a `customHttp` object. The nested object's `version` is `1`; supported `method` values are `post` and `put`. `bodyJsonTemplate` must be a JSON object of at most 8192 characters. At least one string value must be exactly `{{text}}` or `{{promptData}}`. Other allowed exact value tokens are `{{sourceLanguage}}`, `{{targetLanguage}}`, `{{model}}`, `{{context}}`, `{{glossary}}`, and `{{systemPrompt}}`. Context and selected glossary become JSON arrays. `{{systemPrompt}}` and `{{promptData}}` become the exact strings from the shared versioned translation plan, suitable for chat message content. A token embedded inside another string, used as a property name, or placed in a `role` field is rejected.

`responsePath` is a bounded dot-and-index path such as `$.choices[0].message.content`, with at most eight steps and small numeric indexes. The extracted value must be a string. There are no wildcards, filters, recursive selectors, or scripts.

At most eight fixed headers may be configured. Supported names are `Authorization`, `X-Api-Key`, and `X-Client-Id`. Credential headers require opaque `secretReference` values resolved from the user secret store; `Authorization` uses the Bearer scheme. `X-Client-Id` may have a fixed non-control value. The sanitized settings export retains mapping version/structure but clears all header values and secret references; it is not an importable secret backup.

## Destination threat model

Remote endpoints require HTTPS. Plain HTTP is allowed only in explicit `localOnly` mode targeting loopback. The default mode rejects literal and DNS-resolved loopback, private, link-local, carrier-grade NAT, documentation, multicast, and other special-use addresses. All DNS answers must be allowed; a mixed set fails closed. The destination is checked before translation, at every HTTP attempt, and at socket connect to resist DNS rebinding. Automatic redirects, proxies, and cookies are disabled. A redirect response is treated as a failure; credentials are never forwarded to its location.

The user must grant remote-text consent before any custom translation request. Only OCR text and bounded metadata enter the request builder; pixels, audio, and files have no template tokens or provider input. A preview is available in the Provider page and uses replacement text plus redacted header values without sending a request.

## Verification

`scripts/verify.ps1` runs fake-HTTP contract, hostile-text fuzz, DNS-category/rebinding, redaction, malformed/oversized response, timeout, and cancellation tests without a live server. For optional manual verification, configure a user-owned mock HTTPS endpoint or explicit loopback-only mock, inspect the redacted preview, then run a short text-only translation with consent. No service or model is installed automatically.

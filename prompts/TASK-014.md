# TASK-014 — Constrained Custom HTTP Provider

## Goal

Allow advanced users to configure a safe declarative JSON HTTP translation endpoint without executable scripts.

## Preconditions

TASK-011 complete; threat model reviewed; UI can show validation/preview.

## Allowed modification scope

`Infrastructure/Translation/CustomHttp/**`, safe template/config models, provider UI, tests, composition, and docs.

## Implementation requirements

- Support HTTPS URL, method from an allowlist, fixed headers with secret references, JSON body template tokens, and a restricted JSON response path.
- Validate templates without executing code; encode text as JSON data; preview a fully redacted request.
- Prevent loopback/private-network targets by default except an explicit local-only mode; revalidate redirects and resolved destinations.
- Enforce shared size/time/rate/retry/output policies and restrict authorization to the configured origin.
- Version exported configuration and omit secret values.

## Prohibited

No JavaScript/C#/shell expressions, arbitrary file reads, environment expansion, dynamic code, unrestricted headers/methods, TLS bypass, credential-bearing redirects, image/audio upload, injection/hooks, or game changes.

## Acceptance criteria

Valid templates generate exact JSON; hostile OCR cannot escape structure; invalid tokens/paths/URLs fail before send; SSRF/redirect protections and redaction work; response extraction is bounded and deterministic.

## Build/test

Add fuzz/property tests for templating plus fake-HTTP tests for redirects, DNS/IP categories, hostile text, headers, malformed/oversize responses, timeouts, and cancellation. Run full build/tests.

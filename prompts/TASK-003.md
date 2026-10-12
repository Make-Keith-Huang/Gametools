# TASK-003 — Configuration, Validation, and Secret Storage

## Goal

Implement typed configuration with safe defaults, validation, migration/versioning, and an OS-backed secret-store boundary.

## Preconditions

TASK-002 complete; read privacy requirements, profile design, and coding rules.

## Allowed modification scope

Core configuration contracts/models, Infrastructure configuration/secret-store adapters, App composition/config files, tests, and configuration documentation.

## Implementation requirements

- Define options for capture, OCR, stabilization, providers, cache, overlay, logging, and privacy.
- Validate ranges/URLs/language tags and aggregate actionable errors before session start.
- Store API keys using Windows Credential Manager or DPAPI-backed user storage behind `ISecretStore`; persisted config contains opaque references only.
- Support schema version and safe migration; provide sanitized sample settings.
- HTTPS required except explicit loopback Ollama endpoints.

## Prohibited

Never commit/log secrets, authorization headers, raw screenshots, or local private paths. No permissive TLS bypass. No V1 prohibited capabilities.

## Acceptance criteria

Valid config round-trips without secret material; invalid settings produce field-specific messages; secret set/get/delete is isolated and testable; logs/config export are redacted.

## Build/test

Use temporary directories and fake secret store for tests. Cover defaults, every range edge, corrupt/unknown config, migration, redaction, and cancellation. Run Release build and full tests.

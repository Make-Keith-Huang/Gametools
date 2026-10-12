# TASK-009 — PaddleOCR Adapter and Model Management

## Goal

Integrate local PaddleOCR behind `IOcrEngine` with explicit model provisioning, checksum/license metadata, and safe native resource management.

## Preconditions

TASK-008 complete; complete and document a binding/runtime spike covering license, x64, CPU support, maintenance, and deployment.

## Allowed modification scope

OCR contracts only when justified, `Infrastructure/Ocr/**`, model manifest/provisioning UI, package metadata, tests, and OCR/deployment docs.

## Implementation requirements

- Select and pin a compatible PaddleOCR binding/runtime; isolate its types in the adapter.
- Map preprocessed images to positioned/confident Unicode spans and provider-neutral error codes.
- Default to CPU; expose capabilities rather than assuming GPU; serialize inference if the runtime is not thread-safe.
- Provision language models by explicit user action, verify SHA-256, show size/license/source, and recover partial downloads.
- Dispose native tensors/images/engine state deterministically and support cancellation before/after inference.

## Prohibited

No cloud OCR, arbitrary unsigned binary execution, silent model download, raw screenshot logging, audio, injection/hooks, process memory, anti-cheat bypass, or game-file modification.

## Acceptance criteria

Known fixtures for supported V1 languages yield non-empty ordered spans at documented thresholds; missing/corrupt models produce distinct actionable states; repeated initialization/inference/disposal does not leak; Core stays binding-free.

## Build/test

Add adapter mapping tests with a fake runtime, opt-in native integration tests, checksum/download tests, cancellation tests, and a 1,000-frame resource smoke test. Run full build/tests.

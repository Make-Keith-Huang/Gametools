# TASK-008 — Frame Preprocessing

## Goal

Implement an efficient, configurable local preprocessing pipeline that converts cropped subtitle regions into OCR-ready images.

## Preconditions

TASK-007 complete; read `docs/OCR_PIPELINE.md`; obtain representative licensed/synthetic fixture images.

## Allowed modification scope

Core preprocessing contracts/options, Infrastructure image processing, fixtures, tests/benchmarks, profile presets, and OCR docs.

## Implementation requirements

- Crop normalized regions safely and support grayscale, contrast, threshold, inversion, sharpening, scaling, and padding as composable operations.
- Provide conservative presets for plain and outlined subtitles; preserve a no-op path.
- Pool buffers and define ownership; reject invalid dimensions/overflow; cap output resolution.
- Keep implementation deterministic and cancellation-aware between expensive stages.
- Produce debug images only under explicit diagnostic opt-in with retention/redaction warning.

## Prohibited

No remote processing, unbounded allocations, permanent raw-frame storage, arbitrary native binary download, audio, injection/hooks, process memory, or game-file modification.

## Acceptance criteria

Golden fixtures produce stable outputs; all region edges are safe; memory is returned on success/failure/cancel; presets serialize in profiles; processing meets the documented stage budget on reference fixtures.

## Build/test

Add golden/hash, fuzzed-dimension, cancellation, allocation, and benchmark tests. Run full build/tests and report benchmark environment/results.

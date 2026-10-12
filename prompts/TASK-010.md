# TASK-010 — OCR Normalization and Temporal Stabilization

## Goal

Transform noisy per-frame OCR spans into ordered, normalized, stable subtitle events while suppressing duplicate translations.

## Preconditions

TASK-009 complete; read OCR pipeline and collect anonymized/synthetic span sequences.

## Allowed modification scope

Core normalization/stabilization services and models, pure implementation, tests/fixtures, configuration, and OCR docs.

## Implementation requirements

- Sort spans by region and reading order; merge fragments using spatial/line heuristics.
- Normalize whitespace, line breaks, Unicode/punctuation, and configurable language-specific artifacts without destroying meaning.
- Track similarity plus spatial overlap over a bounded time window; require configurable confidence/repetition.
- Emit new/changed/disappeared events with session/sequence; stale input cannot change current state.
- Bound memory and reset state on session/profile/region/language change.

## Prohibited

No translation/provider calls, UI dependencies, unbounded history, wall-clock sleeps in tests, audio, injection/hooks, or game modification.

## Acceptance criteria

Stable repeated subtitles emit once; incremental subtitle growth behaves predictably; brief noise is suppressed; changed lines emit promptly; disappearance resets redisplay; CJK and Latin fixtures preserve intended punctuation/order.

## Build/test

Use an injectable fake clock and table/property tests for jitter, confidence, similarity, ordering, reset, stale sequences, and memory bounds. Run Core and full solution tests.

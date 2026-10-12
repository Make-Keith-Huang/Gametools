# TASK-025 — Release Hardening and Documentation

## Goal

Audit GameLingoAI V1 against product, architecture, privacy, security, performance, and usability requirements and prepare a release candidate.

## Preconditions

TASK-001 through TASK-024 complete; release package available; clean Windows test environments and manual matrix scheduled.

## Allowed modification scope

Any repository file needed to fix verified V1 defects, plus release/user/troubleshooting/privacy/known-limit documentation. Avoid feature expansion.

## Implementation requirements

- Trace every requirement and P0 test to implementation/evidence; resolve or explicitly block release for gaps.
- Audit dependencies/licenses/vulnerabilities, secret handling, outbound requests, logs/support bundle, database migrations, native resource disposal, accessibility, and error copy.
- Execute Windows 10/11, DPI/multi-monitor, window lifecycle, provider/offline, start-stop, sleep/resume, performance, and soak matrices.
- Search code/package contents for prohibited capabilities and unexpected network/game-directory/process access.
- Produce versioned release notes, install/upgrade/uninstall, quick start, privacy model, troubleshooting, limitations, checksum, and rollback guidance.

## Prohibited

No new major feature, waived critical/high issue, hidden telemetry, raw private data, audio/STT/TTS, DLL injection, memory/graphics hooks, drivers, anti-cheat bypass, input automation, or game-file modification.

## Acceptance criteria

All release gates in `PRODUCT_REQUIREMENTS.md` and `TEST_PLAN.md` pass; zero prohibited implementation/package; no unresolved critical/high security/data-loss/crash issue; package works on clean supported OS; documentation enables a new user to install, configure, run, stop, and remove safely.

## Build/test

Run the complete documented verification and manual matrix from a clean checkout/package. Preserve sanitized evidence and hashes. Final report must list version, artifact, pass/fail table, known limitations, residual risks, and an explicit ship/no-ship recommendation.

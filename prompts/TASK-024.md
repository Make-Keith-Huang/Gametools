# TASK-024 — Packaging and OCR Model Provisioning

## Goal

Produce a clean Windows x64 release package with reliable install/uninstall/upgrade behavior and verified optional OCR model provisioning.

## Preconditions

TASK-023 green; package/installer decision recorded; signing approach and third-party licenses known.

## Allowed modification scope

Packaging projects/manifests, publish scripts, model manifest/downloader, version/license assets, CI release configuration, tests, and deployment docs.

## Implementation requirements

- Publish .NET 8 win-x64 using documented framework-dependent or self-contained choice; package only required runtime files.
- Implement signed-package-ready MSIX/installer metadata, per-user data separation, clean uninstall that preserves/deletes data only by explicit policy, and upgrade compatibility.
- Model manifest includes language/version/size/SHA-256/license/source; download is explicit, resumable where safe, atomic, and checksum verified.
- Generate SBOM/license notices and verify artifact hashes; secrets/models/build caches are excluded unless intentionally packaged.
- Startup reports missing runtime/model requirements without crashing.

## Prohibited

No silent network download, unsigned arbitrary executables, admin/driver requirement without approval, bundled credentials, game-directory writes, audio components, injection/hook libraries, anti-cheat bypass, or game modification.

## Acceptance criteria

Install/start/upgrade/uninstall pass on clean Windows 10/11 VMs; no file is written to game directories; model cancel/corruption recovers; package inventory contains no prohibited/private artifacts; licenses/SBOM/version/hash are present.

## Build/test

Run clean Release publish/package, package-content scan, install/launch/uninstall and upgrade smoke, checksum/cancel/offline model tests, and full automated suite. Record exact artifacts and signing status.

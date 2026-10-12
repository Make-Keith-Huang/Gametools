# Windows development packaging

## Scope and acceptance

TASK-024 is accepted for the user-revised local-development scope on 2026-10-06. On 2026-10-05 the user approved current-computer install/start/upgrade/uninstall acceptance, deferring the original clean Windows 10/11 matrix. That local lifecycle passed with a temporary test signature, followed by package/certificate cleanup. On 2026-10-06 the user approved continuing development without complete upstream build attestation. Neither another OS nor a clean machine is considered tested. Distribution signing and final bundled-license review remain open; these artifacts are not public releases. See [release audit](RELEASE_AUDIT.md) for current evidence and limitations.

The package is self-contained .NET 8, win-x64, unpacked assemblies (no trimming/single-file extraction). The release profile pins the three shared runtime packs to 8.0.31 independently of the installed SDK. The SDK's WPF parent runtime-pack resolution requires updating known references as well as the requested framework version. Packaging checks runtimeconfig against the resolved dependency graph, so mismatched runtime versions fail instead of silently shipping. Source for the serviced version: [Microsoft .NET 8 downloads](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), checked 2026-10-04.

## Build and inspect

Requirements: PowerShell 7, .NET SDK capable of building this solution, Windows SDK MakeAppx (default 10.0.26100.0 x64). Restore may access NuGet. No app, certificate or model installation occurs in these commands.

```powershell
pwsh -NoProfile -File scripts/package.ps1
# For a later development upgrade, preserve identity/publisher and increase version:
pwsh -NoProfile -File scripts/package.ps1 -Version 0.1.0.1
pwsh -NoProfile -File scripts/scan-package.ps1 -Payload <run-directory>/payload
pwsh -NoProfile -File scripts/verify-package.ps1 -RunDirectory <run-directory>
```

Every run owns a new `<DataRoot>/artifacts/release/<guid>` directory; builds/restore intermediates are isolated from ordinary tests. Local `-DataRoot` defaults to `D:\ai工作数据和目录存放点\GameLingoAI`. Process-scoped TEMP/TMP, .NET CLI and NuGet caches use that root and are restored on exit, including failures. Existing user NuGet packages are a read-only restore fallback; missing dependencies may still require NuGet access. A missing data drive fails rather than falling back to C. The GitHub packaging workflow explicitly supplies its disposable checkout as DataRoot so its artifact-upload paths remain valid; that remote workflow is not a local storage override. Failed runs are retained, not recursively deleted. Output includes `payload`, unsigned MSIX, `payload-inventory.json` and `artifact-hash.json`. The inventory records every payload file's length and SHA-256. Verification recomputes the MSIX hash and every archived payload hash, rejects missing/duplicate/unexpected files and requires MSIX block metadata. It is an integrity check against local build records, not proof of publisher trust or successful installation.

The scanner rejects credentials, databases, model archives, caches, test executables, optional FFmpeg transport and crash-dump executable. It is a conservative path/type inventory check, not a malware detector. Full .NET shared framework files remain because the existing application depends on their supported runtime composition; arbitrary trimming is disabled.

## Identity, trust and user data

Development identity/publisher default to `GameLingoAI.Development` / `CN=GameLingoAI.Development`. `-Identity` and `-Publisher` configure future signing metadata; no signing key/password is accepted, copied or bundled. The certificate subject must match the MSIX publisher. Do not publish under this placeholder identity or install an unsigned package by disabling Windows security. Any local test-certificate trust change needs explicit permission and exact-certificate cleanup after testing.

The app runs as the user, with no elevation/driver/service requirement. MSIX declares `runFullTrust` for the desktop app and `unvirtualizedResources` with filesystem virtualization disabled, preserving `%LOCALAPPDATA%/GameLingoAI` across upgrades/uninstall. It does not disable registry virtualization or write game directories. Uninstall removes application registration/binaries; settings/database/models/logs and Credential Manager entries are retained. Data removal is a separate explicit user action, never an installer side effect. Back up user data before an upgrade; do not downgrade a migrated database without a supported backup/restore path.

Reference: [Microsoft manual desktop MSIX packaging](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion) and [AppData virtualization/preservation](https://learn.microsoft.com/en-us/windows/msix/desktop/flexible-virtualization).

## Models and attribution

`model-manifest.json` contains model version, languages, sources, license, byte sizes and SHA-256 values; tests compare it to the application's catalog. Actual models are excluded. Provisioning requires explicit user action, checks resume Content-Range, reuses complete checksum-verified archives offline, verifies extracted required files, and checks cancellation before committing installation. Missing models are a settings/setup condition, not an automatic download at startup.

`sbom.cdx.json` inventories resolved NuGet packages, self-contained runtime packs and native DLL file components with SHA-256/source-package properties. `native-provenance.json` traces the 11 shipped NuGet native DLLs to exact package assets, package content hashes and observed PE version/copyright strings; the package build rejects missing/changed DLLs and duplicate ownership. Optional FFmpeg transport is explicitly excluded. This report does not cover static subcomponents or .NET runtime-pack native files; the latter remain represented by runtime-pack components and the full payload inventory. It proves equality with restored files, not reproducible upstream builds or legal compliance.

`THIRD-PARTY-NOTICES.txt` records NuGet license metadata; `licenses/` copies available package notice texts. This is not yet a complete legal attribution audit of native bundled subcomponents (Paddle/MKL/oneDNN/OpenCV/ONNX). `release-status.json` deliberately marks license review and public-release readiness false. No project redistribution license or publisher identity is invented for the user. See [native attribution evidence](NATIVE_ATTRIBUTION.md) for the open questions.

ONNX Runtime 1.11.1 and MKLML/OpenMP 2019.0.5.20190502 are matched to their upstream binary archives: packaging includes the complete licenses/third-party statements. Paddle2ONNX 1.0.0rc2 is also matched; main/ONNX/optimizer source licenses are included, but static protobuf attribution remains unresolved. `copy-native-notices.ps1` checks DLL and notice hashes; a dependency or notice change requires review before packaging succeeds. Other native attribution remains incomplete. No notice downloads occur during normal packaging; `fetch-native-notices.ps1 -Component OnnxRuntime` or `-Component Mklml` explicitly reproduces archive checks for maintainers.

oneDNN LICENSE/THIRD-PARTY-PROGRAMS from its reported source commit, SQLite 3.53.3 public-domain attribution/source-ID match, and SQLitePCLRaw 2.1.12 LICENSE/NOTICE are also included and hash-guarded. Their evidence and limits are in the attribution document; global release/license-review flags remain false until the outstanding components are resolved.

## Remaining local checks

The curated OpenCV codec notices now include the fixed-source JPEG/IJG, PNG, WebP patent/license, TIFF, zlib and XZ/liblzma declarations. Top-level bundled notices include the IJG acknowledgment. These materials are hash-guarded against the current slim DLL; no codec executable, model or downloaded build script is included. Native attribution remains incomplete for other components listed in `NATIVE_ATTRIBUTION.md`.

- Local signed installation, 0.1.0.0 to 0.1.0.1 upgrade and uninstall passed on 2026-10-05; user confirmed both versions' main windows were normal and closed. Two existing data files retained identical hashes after each package operation. This machine already has .NET and the native prerequisites; absence-of-system-runtime and explicit missing-model UI were not separately exercised. Subsequent source changes add presence-only OCR prerequisite status and deterministic missing-model/no-network tests; the earlier installed packages do not contain those changes.
- Temporary certificate (CurrentUser/My key and LocalMachine/TrustedPeople public trust) and test package were removed and absence verified. Signed test artifacts remain locally for evidence; their certificate is no longer trusted. They are not distribution packages.
- Scanner fixture tests cover six forbidden paths, required-file absence, runtime graph mismatch and archive payload tampering. Normal CI runs them; manual `package.yml` builds unsigned artifacts without installing/trusting anything. Workflow configuration is local evidence only; no remote CI run has occurred.
- Complete native dependency license/SBOM audit and signing approach for distribution.
- Cross-OS/clean-machine checks and long/native soak remain deferred release evidence, not passed gates.

Computer-use could enumerate the MSIX window but rejected its own package identity during capture, twice. Per the skill's recovery rule, automation stopped and startup UI confirmation came from the user instead. No terminal UI automation or security-dialog clicking was used.

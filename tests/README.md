# Test conventions

`GameLingoAI.Tests` is now an xUnit/VSTest project. `dotnet test` discovers named `DomainTests` and `P0Tests` cases and produces TRX results; coverlet collects coverage. The bootstrap suite loop has been removed. Its assertions remain in area-specific helpers, invoked by named facts rather than duplicated. Central pinned test-only packages are xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1, xunit.runner.visualstudio 3.1.4 and coverlet.collector 6.0.4. They support the existing .NET 8/VSTest workflow without changing product dependencies ([xUnit guide](https://xunit.net/docs/getting-started/v2/getting-started)).

Test conventions:

- The current small solution keeps unit/integration/UI cases in one maintained test project, with explicit `Suite` traits. This avoids copying native dependencies and helpers into multiple assemblies; distinct test cases remain independently discoverable/filterable.
- Unit tests must not require the network, GPU, OCR models, a game, or persistent machine state.
- Integration tests use fakes or temporary directories/databases and label environment-dependent cases explicitly.
- `scripts/verify.ps1` defaults generated data to `D:\ai工作数据和目录存放点\GameLingoAI` (override explicitly with `-DataRoot` on another workstation). Build outputs use `artifacts`; results use `reports/default-tests` and `reports/platform-tests`, relative to that data root, not the source checkout. Process temp, CLI state and writable NuGet caches are routed there and prior environment values restored on exit. Existing user NuGet packages are read-only fallback inputs.
- Cancellation, error, and boundary behavior are tested alongside the normal path.

The default `tests/default.runsettings` excludes `Suite=Platform` and `Suite=OptIn`; it requires no network, provisioned model, game or interactive desktop. Restore may need NuGet on a fresh machine. `Speed=Slow` labels existing bounded resource/pipeline exercises, which still run by default. Test parallelism is disabled because a few suites deliberately replace process-wide Console output and native WPF lifetime is process-global.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1
# Explicit interactive-desktop acceptance, in addition to the default gate:
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1 -IncludePlatform
dotnet test GameLingoAI.sln -c Release --no-build --filter FullyQualifiedName~P0Tests.DiagnosticRedaction
```

Platform settings select only native/UI cases and enable their explicit environment gate. These include real HWND hotkey dispatch, shell composition/accessibility, discovery, WGC and the isolated overlay harness. The executable entry point is retained ONLY as `--overlay-harness` for WPF process isolation/manual hardware validation; it is no longer the automated suite runner. Old documentation commands that invoke test-name strings directly on the executable are historical and should use `dotnet test --filter` instead.

Native OCR remains separately opt-in: set `GAMELINGOAI_RUN_NATIVE_OCR_TESTS=1`, `GAMELINGOAI_OCR_MODELS_ROOT` and `GAMELINGOAI_OCR_NATIVE_FIXTURE`, then use `dotnet test --filter Suite=OptIn`. Missing prerequisites must not be reported as a passed native-model check. Fixtures must be synthetic or explicitly licensed; the supplied hand-authored preprocessing corpus is CC0-1.0.

Real local Ollama acceptance is separately opt-in. Install `qwen2.5:3b` explicitly and start Ollama, then set `$env:GAMELINGOAI_RUN_LOCAL_OLLAMA_TESTS='1'` and run `dotnet test GameLingoAI.sln -c Release --no-build --filter FullyQualifiedName~LocalOllamaTests --logger "console;verbosity=normal"`. This fixes the endpoint to `127.0.0.1:11434`, grants no remote consent, sends only two authored synthetic English subtitles through the production adapter, and prints their translated output/timing. It never pulls models or accesses credentials/settings. Missing service/model fails after opting in; without the opt-in the test is skipped, not passed. Required Chinese meaning tokens, CJK/output/metadata and usage assertions cover these two fixtures, not general translation quality or latency. Stop/unload this model after acceptance if it is not otherwise in use.

The verify script also runs the [synthetic benchmark and 12-second wall-clock soak](../tools/GameLingoAI.QualityHarness/README.md) in separate processes outside coverage. Test methods under `ReplayTests`, `QualityMetricTests`, `PipelineReplayTests`, `ReplayBenchmarkTests` and `SoakTests` cover the tooling. Process probes test real nonzero growth-gate exits but explicitly label injected counters as virtual. Long/native soaks and the manual release matrix are not covered by this short gate; see the progress record for actual execution evidence.

The script runs quality tools against a new `pending-<id>.json` (the harness requires an unused path), then publishes successful results over `reports/quality/benchmark-current.json` or `soak-ci-current.json`. Failed pending reports remain evidence; successful temporary names disappear, preventing accumulation of successful duplicates. A retained current report is not proof that a newer failed run passed. Package guard fixtures inherit the routed artifacts root and remove only their own successful run's synthetic GUID directory after exact-path/link checks; failed fixture runs remain available for diagnosis. Coverage collectors still create their own result subdirectories; these are not automatically deleted by the script.

## Authorized local pipeline window

### Real application idle observation

To smoke-test an exact self-contained package payload, supply `-ApplicationPath <run-directory>/payload/GameLingoAI.App.exe`. The same isolation, hashing and shutdown checks apply; this checks payload startup, not MSIX installation or publisher trust.

`pwsh -NoProfile -File scripts/test-app-idle.ps1 -Mode Smoke` launches the routed Release application with `--synthetic-test`, observes its main-window responsiveness/private bytes/CPU/handles for30s (5s warm-up), then requests normal window close and requires exit0. `-Mode EightHours` observes28800s with300s warm-up and10s sampling. Missing counters, early exit, lost main window, excessive sampling gaps (including suspend), unresponsiveness or forced shutdown fail. Reports include executable/managed assembly hashes and distinguish smoke from release-duration evidence. This is the actual application startup, idle UI/timers and shutdown, not headless replay; it does not validate active capture/OCR, navigation, screen-reader use or provider quality. Idle peak growth is reported, not claimed as the active50MB gate.

The default data root is `D:\ai工作数据和目录存放点\GameLingoAI`; an existing routed Release build is required. Only the child process TEMP/TMP are changed and restored afterwards. The synthetic database/settings/logs live beneath a new report's `temp` directory and the app removes its own synthetic directory on normal exit. The script does not navigate to user region/model pages, download models, register shortcuts, capture windows or contact providers. Do not interact with the test window, lock/suspend the computer or close it early during a release-duration run. Failed temporary evidence remains for diagnosis; successful empty temporary directories can be removed after report inspection. A smoke pass is not an eight-hour pass.

`--local-pipeline-lifecycle <models> <evidence>` uses the same explicit capture permission and isolated data as the short harness, but runs50 real controller sessions with fresh session identities. Each verifies both visible meanings, pause/resume/cache, zero pipeline/overlay errors and stop under2s; native/session resources are recreated, while the isolated SQLite cache persists across sessions. Report version3 retains each completed cycle and exact requested/completed counts; first-cycle translations miss cache, subsequent cycles must hit it. The15-minute internal deadline and16-minute outer guard bound failure. This is real production-pipeline lifecycle evidence, not full-shell/manual50-session, sleep/resume or long-soak certification. Keep the owned target foreground; no game/other-window capture, download or remote translation is allowed.

Report version 2 separates session-start duration, start-to-first-visible result, local uncached fixture waits, resume-to-cached-visible result and stop duration. Unreached timings stay null on failure. Fresh fixture cases must miss the isolated cache; resume must hit cache, preserve the expected meaning and become visible. Timings include managed polling (50 ms), not physical presentation. A single cached resume is not a representative median or a remote-provider latency result. No preload is performed and model-load duration is not inferred from aggregate stage timing.

For this workspace, route controllable outputs to `D:\ai工作数据和目录存放点\GameLingoAI`: set process `TEMP`/`TMP` to its `temp`, build with `GAMELINGOAI_ARTIFACTS_PATH` pointing at its `artifacts`, and supply its `reports/local-pipeline` as the evidence root. Existing isolated models stay in the repository's `artifacts/native-compatibility/models`; no automatic migration/download. Resolve the executable under the routed artifacts directory. The relative command below illustrates argument order, not permission to use old output locations when the workspace storage rule applies.

Latest new-default functional replay59349 (2026-10-10) passed both visible translations, pause/resume/cache and118ms stop. OCR p50/p95=181.11/221.24ms, but first cold fixture9445ms and second1006ms; cold translation dominated. Do not interpret harness exit0 as full latency-budget acceptance. Result hash and exact D-drive path are in RELEASE_AUDIT.md. The Ollama model was unloaded after verification; its files remain installed.

After explicit user permission to capture the harness-owned synthetic window, build Release and run:

```powershell
$env:GAMELINGOAI_RUN_LOCAL_PIPELINE='1'
& ./artifacts/bin/GameLingoAI.Tests/release/GameLingoAI.Tests.exe --local-pipeline-harness ./artifacts/native-compatibility/models ./artifacts/local-pipeline
```

Requires an interactive Windows desktop, installed/verified OCR models at the supplied root, and running local Ollama with `qwen2.5:3b`. No models are downloaded. Keep the test window foreground so the production overlay can appear; changing focus intentionally hides it. The target handle is created internally, not selected from other applications. The test uses production capture, preprocessing, OCR, stabilization, SQLite cache, Ollama, SessionController and OverlayController; it does not automate shell controls or certify the whole application UI. It checks two authored translations, visible subtitle rendering, pause/hide, resume/cache hit and stop/overlay disposal under two seconds. A new GUID directory retains synthetic-only database and sanitized result.json (no pixels/text); normal user settings/data are not used. A timeout/failure is not a pass. The default test suite never runs this executable mode. Unload the Ollama model afterwards if not otherwise in use.

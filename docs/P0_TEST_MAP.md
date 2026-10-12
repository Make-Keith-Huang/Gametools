# P0 automated requirement map

Cases below are independently discoverable with `dotnet test --list-tests`. Each case invokes the existing area helper and all of its assertions; none is a placeholder assertion. The project namespace is `GameLingoAI.Tests`. Default cases run offline after package restore. Windows-native cases require `tests/platform.runsettings` and an interactive Windows desktop; model inference is a separate opt-in. This map is not a claim that long-duration or manual release gates have passed.

| TEST_PLAN / task requirement | Named test evidence |
|---|---|
| Domain normalized regions, metadata, sequence and state invariants | `DomainTests.TestRegionBoundaries`, `TestMetadataValidation`, `TestMonotonicSequence`, `TestSessionTransitions`, `TestFrameValidationAndDisposal`, `TestModelEquality` |
| Configuration defaults, ranges, secret references, migration/preservation | `P0Tests.Configuration`, `HotkeyConfiguration`, `ProfileJson` |
| Bounded queues, stale results, cancellation/drain, session faults/recovery | `P0Tests.SessionPipeline`, `BufferedDiagnostic`, `HistoryRecording`, `TranslationCache` |
| Capture selection/confirmation, HWND reuse, bounded frame buffer, resource release | `P0Tests.WindowDiscovery`, `Capture` (Platform); `SessionPipeline` (Default fakes) |
| Preprocessing bounds, normalization, OCR language mapping/order/confidence, empty/noisy/repeated text | `P0Tests.RegionCoordinates`, `RegionEditor`, `Preprocessing`, `Ocr`, `Stabilization` |
| Native OCR inference against explicitly provisioned model | `P0Tests.NativeOcrFixture` (OptIn; absent model is not a pass) |
| Missing OCR runtime/model status without loading native code or downloading | `OcrPrerequisiteTests.CpuAndNativeRequirementsAreExplicitAndPresenceOnly`, `MissingModelsAndRuntimeAreVisibleWithoutDownloadOrCrash` (presence checks, not native inference) |
| Provider timeouts, retryable 429/5xx, nonretryable auth, malformed/oversized response, cancellation | `P0Tests.TranslationFoundation`, `OpenAiCompatible`, `Ollama`, `CustomHttp` (fake HTTP handlers, no paid/live API) |
| Real installed local model, denied remote consent, two synthetic translations/meaning checks | `LocalOllamaTests.InstalledLocalModelTranslatesSyntheticSubtitles` (OptIn; fixed loopback/qwen2.5:3b; no automatic installation; not broad model quality certification) |
| Authorized owned-window WGC → native OCR → real local translation → visible overlay, pause/cache/stop | `--local-pipeline-harness` (separate explicit environment gate, not a default xUnit case; isolated synthetic database; not full-shell/long-soak acceptance) |
| Glossary/prompt safety, cache keys, coalescing, generation/retention | `P0Tests.GlossaryPrompt`, `TranslationCache`, `SqliteCache`, `GlossaryPersistenceUi` |
| Privacy: pixel exclusion, HTTPS/loopback/DNS rules, output sanitization | `P0Tests.OpenAiCompatible`, `Ollama`, `CustomHttp`, `TranslationFoundation`, `ProfileJson` |
| Privacy: all logging destinations, exception/property/secret removal, safe immutable export | `P0Tests.DiagnosticRedaction`, `DiagnosticFile`, `BufferedDiagnostic`, `ApplicationDiagnostics`, `SupportBundle`, `DiagnosticsViewModel` |
| SQLite fresh/upgrade/rollback/future/corrupt/busy/cancel, foreign keys and retention | `P0Tests.DatabaseMigration`, `ProfilePersistence`, `HistoryPersistence`, `SqliteCache`, `DatabaseMaintenance`, `DatabaseAdmin`, `SessionMaintenance` |
| Overlay coordinate/stale-result logic | `P0Tests.Overlay`, `RegionCoordinates` |
| Native overlay move/resize/minimize/DPI/style/hit-test/lifecycle | `P0Tests.OverlayWindow` (Platform; process-isolated native harness) |
| Hotkey ownership/rollback/stale dispatch and HWND registration/release | `P0Tests.HotkeyOwnershipAndFailures` (Default), `NativeHotkeyDispatch` (Platform), `HotkeysViewModel` |
| Composition, WPF controls/accessibility labels, command cancellation/error/duplicates | `P0Tests.UiShell` (Platform), `ProfilesViewModel`, `ProfileSessionSettings`, `DiagnosticsViewModel`, `SessionMaintenance` |
| Resource sampling/concurrency, safe metrics | `P0Tests.DiagnosticMetrics`, `SessionPipeline` |
| Repeatable synthetic timestamps/pixels, clearing owned frames and cancellation | `ReplayTests.CorpusRepeatsMetadataAndPixelsWithoutRetainingBuffers`, `CorpusRejectsInvalidRangesAndCancellation` |
| Bounded copied provider script, 100–1,500 ms simulated delays, fault/recovery and cancellation | `ReplayTests.ProviderScriptRepeatsLatencyFaultsAndRecovery`, `ProviderCancellationHasNoSleepOrLateSuccess`, `ProviderScriptRejectsUnboundedOrInvalidInputs` |
| Bounded/concurrent nearest-rank percentile statistics | `QualityMetricTests.PercentilesAreNearestRankAndUseOnlyBoundedRecentSamples`, `ConcurrentSamplingHasExactCountsAndFiniteOrderedPercentiles` |
| Warm-up/peak private-byte growth, exact threshold, missing data and duration rejection | `QualityMetricTests.WarmupDoesNotCountAndExactLimitIsABreachEvenAfterRecovery`, `MissingCountersShortRunsAndOneSampleCannotPass`, `GrowthGateRejectsInvalidAndOutOfOrderMeasurements` |
| Real controller/preprocessing/stabilization/cache replay, deterministic corpus hash, drain/error recovery | `PipelineReplayTests.RealPipelineReplayRepeatsInputAndDrainsFrames`, `ReplayHonorsCancellationAndNonTransientFailure` (synthetic OCR/provider, headless presentation) |
| Versioned synthetic performance baseline comparison, incompatible input and regression rejection | `ReplayBenchmarkTests.ReferenceWorkloadPassesButDifferentCorpusCannotPass`, `LatencyAllocationCpuDropsAndMissingDataFailClosed` |
| Real-time sampling, continuous active session/idle controller, drain/cancellation, sampler failure | `SoakTests.WallClockIdleSamplesAndActiveSessionDrains`, `SampledBreachMissingCounterAndEarlyWorkloadCannotPass`, `CancellationAndSamplerFailureDrainOwnedWorkload` |
| Real CLI pass/breach/unavailable/short exit codes, honest virtual labels, no overwrite, fatal provider/no partial report | `SoakTests.CliGrowthFixturesSaveHonestReportsAndReturnRealExitCodes` (four cases), `CliBadInputAndFatalProviderDoNotCommitReports` |

## Execution evidence and remaining manual gates

- Final default/platform/benchmark/short-soak evidence and coverage are recorded in [TASK execution progress](TASK_EXECUTION_PROGRESS.md), TASK-023 acceptance dated 2026-10-04.
- Actual 60-minute/8-hour runs and terminal Ctrl+C delivery remain unexecuted and must not be reported as passed. Managed cancellation/drain and process-level failure/report/exit behavior are covered separately.

## Release-only evidence (not substituted by unit tests)

Physical mouse/key delivery, the complete Windows 10/11 and 100%/150%/200%/mixed-DPI matrix, actual local/remote providers, sleep/resume, clean package install/upgrade/uninstall and real long soaks remain subject to the release matrix. Synthetic or virtual elapsed time must never be reported as a real 60-minute/8-hour run. Fixture sources are synthetic/CC0 unless an explicit license accompanies an added asset; never add game captures or credentials.

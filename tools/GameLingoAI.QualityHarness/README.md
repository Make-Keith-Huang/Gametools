# Quality harness

This test/tool-only Windows project is not referenced by the application. It supplies deterministic fixtures, a real-controller replay report command, a versioned synthetic benchmark gate and wall-clock soak modes. It never opens a game, captures a native window or contacts a provider.

- `ReplayCorpus` version `synthetic-bgra-v1` lazily creates 640×360 BGRA frames. Metadata starts at the Unix epoch with fixed generated session/correlation IDs, a configurable 1 ms–1 min cadence, and 1–100,000 frames. Groups repeat eight frames; each fourth group is blank. Geometric glyph-like patterns are hand-authored for this repository and dedicated to CC0-1.0. There are no screenshots, game assets, fonts, credentials or real user text. This fixture does not measure native OCR accuracy.
- Every call owns an independent pixel array; disposing its frame clears the array. The corpus itself retains no frame buffers. Cancellation is checked before allocation and during generation. No source depends on machine time or random seeds.
- `ScriptedTranslationProvider` copies a bounded (1–1,024 step) immutable script and cycles through success, 429-style rate limiting, 500-style server error, disconnect, packet-loss timeout and rejected authentication. Latencies are bounded to 0–30 seconds; tests exercise 100–1,500 ms without sleeping using an injected delay. The default delay uses actual cancellable wall-clock time. Failure messages never echo request text. This models provider outcomes, not actual HTTP transport; real adapter status/retry/HTTP contracts remain tested separately.
- `ReplayTests` verifies identical metadata/pixels across independent runs, changing/repeated/blank scenes, independent buffer ownership/clearing, range/cancellation handling, exact fault/latency scripts, copied inputs, recovery and cancellation without late success.
- `LatencyWindow` stores only the latest 4,096 measurements by default (configurable 1–65,536). Its immutable snapshot reports total observations separately from retained window size and uses nearest-rank p50/p95 over that window; maximum is also window-local. Missing samples are null, not zero. Concurrent writers/snapshots are serialized, and nonfinite/negative input is rejected. It does not claim whole-run exact percentiles after wrapping.
- `MemoryGrowthGate` retains only a baseline and peak growth, ignoring pre-warm-up samples. The first valid sample at/after warm-up is the baseline. The TEST_PLAN limit is interpreted as **50,000,000 bytes (decimal MB)** and is exclusive: reaching it is a breach. Peak breach remains a failure even if memory later shrinks. Missing post-warm-up counters, fewer than two valid samples or a duration shorter than requested cannot pass. `SoakRunner` uses actual monotonic elapsed time, never replay timestamps. CLI exits are 0 pass, 3 breach, 2 incomplete/unavailable.
- `QualityMetricTests` covers nearest-rank/wrapped windows, 10,000 concurrent recordings, invalid durations/values, warm-up, exact threshold, recovery after breach, unavailable counters and incomplete runs. These are deterministic gate tests, not evidence of a real soak.

## Real pipeline replay

```powershell
dotnet run --project tools/GameLingoAI.QualityHarness -c Release --no-build -- replay 40 artifacts/quality/replay-40.json
```

Frame count must be a multiple of eight between 8 and 100,000. The destination must be a new local JSON file; writing uses an owned temporary file and non-overwriting commit. Exit 0 currently means replay/report generation completed, **not** that a baseline or release gate passed. Invalid arguments/output return 2, cancellation/timeout returns 130, and pipeline failure returns 1. Error output omits raw exceptions and private paths. Reports explicitly mark native OCR, native overlay, remote provider and performance-gate evaluation as false. No window is opened and no network request is sent.

`PipelineReplay` uses the production `SessionController`, `FramePreprocessor`, `TemporalTextStabilizer` and bounded in-memory `CachedTranslationProvider`. Capture and OCR are synthetic and the presenter is headless. The controller supplies a fresh session ID; sequence, correlation and corpus-relative timestamps remain deterministic. Stabilization/cache clocks follow corpus time, while measured processing/end-to-end latency uses real monotonic wall time. The input SHA-256 includes sequence/timestamps/pixels but excludes the random session identity. The default provider incurs real 100 ms delay for cache misses.

Completion requires the final frame to reach stabilization and the latest emitted stable event to reach the controller's presentation-completed diagnostic. The source then waits for explicit stop instead of returning EOF, which correctly means target loss to the production controller. Stop drains workers and releases all generated pixel leases. Completion/error waits are cancellable and have a bounded timeout. `PipelineReplayTests` compares independent 40-frame hashes, counts, cache hits, release and successful completion, and exercises pre-cancellation, invalid frame counts, authentication failure and a subsequent successful replay.

Reports contain controller stage p50/p95 (latest 256 samples per stage), capture-to-headless-presentation p50/p95 (latest 4,096 samples, including disappearance/hide events), frame-drop rate, cache/error counters, allocated bytes/per-frame allocation, cumulative process CPU delta, private bytes before/after and total wall duration. They do not contain pixels or subtitle text. End-to-end latency starts at the captured frame that produces the stable event, not at the first observation of a new scene. The fixed 4,096-slot timing ring fails explicitly rather than reporting zero if a result outlives its retained capture timestamp. Two endpoint memory samples are **not** the post-warm-up peak-growth soak gate.

For baseline comparison, use `benchmark 40 <new-local-report.json>` instead of `replay`; see [reference measurements, formulas, scope and exit codes](../../docs/REPLAY_BASELINE.md). A saved assessment identifies its baseline and fixed safe failure codes. No baseline change is inferred from a slower host.

## Fault profiles

`fault-replay recovery <new-local.json>` runs 128 frames with a cyclic script of rate limiting (100 ms), server error (500 ms), disconnect (750 ms), packet-loss timeout (1,500 ms), each followed by success (100 ms). The production controller may cancel a slow request when newer text arrives; the report records actual errors/stale results, not a promise that every scripted outcome completed. A final blank scene allows the disappearance event to drain. This is unbaselined adapter simulation, not real HTTP or network-loss injection. `fault-replay auth <new-local.json>` exercises a fatal credential failure, returning 1 without a completed report. Script unit tests separately verify every exact outcome without wall-clock sleeps.

## Wall-clock soak

```powershell
dotnet run --project tools/GameLingoAI.QualityHarness -c Release --no-build -- soak ci artifacts/quality/soak-short.json
dotnet run --project tools/GameLingoAI.QualityHarness -c Release --no-build -- soak active artifacts/quality/soak-hour.json
dotnet run --project tools/GameLingoAI.QualityHarness -c Release --no-build -- soak idle artifacts/quality/soak-idle.json
```

- `ci`: 12 seconds active, 4-second warm-up, 1-second sampling. This is tooling smoke only, never a long-soak pass.
- `active`: one continuous synthetic session for 60 minutes, 5-minute warm-up, 10-second sampling. It uses the same production controller/preprocessing/stabilization/cache as replay, without repeatedly restarting the session. It stops and releases generated frames at the elapsed-time boundary; the last in-flight translation may be cancelled by normal shutdown.
- `idle`: a stopped production controller for 8 hours, 5-minute warm-up, 10-second sampling. It does not instantiate WPF, native capture/OCR, SQLite or the full application composition. Idle uses the same conservative growth gate as active as an additional harness policy; TEST_PLAN explicitly requires that limit for active.

Own-process private/working-set/managed/allocated bytes, cumulative CPU milliseconds and handle counts are sampled. Null means unavailable; GPU is explicitly unavailable. The report retains only the latest 1,024 timestamped samples and total sample count, while the peak-growth gate covers **all** post-warm-up samples. It is sampled peak, not a claim to detect between-sample spikes. Stage and E2E percentiles remain bounded recent windows as above. Idle per-frame metrics are null, not divide-by-zero values.

Only a complete measured duration with sufficient valid memory samples can pass. A breach report is saved before exit 3; missing/incomplete data returns 2. Ctrl+C/timeout cancels the pipeline, awaits cooperative worker cleanup, removes owned partial output and returns 130 without a completed report. Other workload/sampler failures return 1. The duration+30-second cancellation watchdog is cooperative, not a guarantee against a permanently blocked native call. There are no native calls in this workload.

`growth-check <steady|breach|missing|short> <new-local.json>` is an explicit **virtual counter probe** of the same growth gate and report/exit wiring. Its report sets `WallClockMeasured=false` and cannot be cited as soak evidence. Process tests require actual exits 0/3/2/2, check the assessment, and verify no overwrite/partial leftovers. `SoakTests` separately drives real wall-clock sampling with injected counters, cancellation, sampler failure and early workload completion. The normal verify script and CI run the benchmark and real 12-second soak outside coverage instrumentation.

Long 60-minute/8-hour commands have **not** been executed yet. Full-application/native-resource soaks, Ctrl+C delivery from a terminal and the complete manual release matrix remain release evidence, not something the synthetic harness can certify. Virtual elapsed time and injected counters must always be labelled separately from measured wall time.

# Synthetic replay regression baseline

Baseline ID: `synthetic-40-v1-20261004`, implemented by `ReplayBenchmark` in the test/tool-only QualityHarness. This is an initial engineering regression guard, **not** a native OCR, GPU, WPF overlay, remote-provider or release-performance certification. The reference is one observed cold-start run, not a statistical hardware survey. Do not silently increase limits after a failure; inspect the report, load and code changes, and version an intentional baseline change.

## Reproduce

```powershell
dotnet run --project tools/GameLingoAI.QualityHarness -c Release --no-build -- benchmark 40 artifacts/quality/benchmark-40.json
```

Run outside coverage instrumentation. The destination must not already exist. Exit 0 means this synthetic regression gate passed; 3 means breached, 2 means incompatible corpus/arguments or output failure, 130 means cancellation/timeout and 1 means pipeline failure. A completed breached/incompatible assessment is saved before returning nonzero. The `replay` command remains an explicitly unbaselined measurement mode. Both leave native-evaluation flags false.

## Reference evidence and scope

Reference: `artifacts/quality/task023-replay-20261004-01.json`, SHA-256 `0DA09910BC7C6D7272A518EC824575B2EC8A4F7B6985FD4F495E04B1D9E54C8E`, .NET 8.0.19, 16 logical processors, captured 2026-10-04. Corpus: `synthetic-bgra-v1`, 40 frames, 640×360 BGRA, eight-frame scenes at a requested 10 FPS, real 100 ms fake-provider delay on cache misses, production preprocessing/stabilizer/in-memory cache/controller and headless presentation. Corpus sequence/timestamp/pixel hash: `E8E51AB08F1E3EEFD62B02021CD851A40A20E1E21717DD550DB9B791406A959C`. Counts and input hash are mandatory; another frame count is incompatible, not a free pass.

The stage gate is `max(reference p95 × 4, scheduling floor)`. The deliberately documented headroom prevents tiny fake-stage timings and ordinary timer/thread-pool scheduling variability from becoming meaningless sub-millisecond gates. It still catches large regressions; it is not a replacement for ARCHITECTURE budgets measured with real adapters. For this 40-frame fixture all stage samples fit the 256-entry stage window and all five presentations fit the 4,096-entry E2E window.

| Metric | Reference | Gate |
|---|---:|---:|
| Capture p95 | 2.2828 ms | ≤10 ms |
| Preprocessing p95 | 1.8423 ms | ≤10 ms |
| Synthetic OCR p95 | 0.0104 ms | ≤5 ms |
| Stabilization p95 | 0.2494 ms | ≤10 ms |
| Cached/simulated translation p95 | 119.6668 ms | ≤478.6672 ms |
| Headless presentation p95 | 0.0289 ms | ≤5 ms |
| Capture-to-presentation p95 | 125.5566 ms | ≤502.2264 ms (4×) |
| Total wall duration | 4430.8636 ms | 3900–8861.7272 ms (upper 2×; lower proves requested cadence was not bypassed) |
| Allocation per generated frame | 990207.4 bytes | ≤1485311.1 bytes (1.5×) |
| Process CPU delta | 78.125 ms | ≤500 ms (`max(4×, 500)`) |

All 40 frames must be released. Stage counts must be 40/40/40/40/4/5 in capture/preprocess/OCR/stabilize/translate/present order; exactly one cache hit and zero drops/stale results/errors are required. Missing, duplicate, unknown or nonfinite stage measurements cannot pass. Empty E2E samples and absent private-byte counters cannot pass. Private-byte before/after are reported but are not a valid warm-up/peak-growth soak measurement; use the separate [wall-clock soak commands](../tools/GameLingoAI.QualityHarness/README.md) for continuous resource samples.

`ReplayBenchmarkTests` checks the reference, incompatible input, injected slow/missing/nonfinite stage data, E2E regressions, CPU/allocations, drops, lost leases and implausible duration. `SoakTests` checks real process growth-gate breach exits using explicitly virtual counter probes, not an artificial OS leak. These tests do not substitute for unexecuted long/native soaks.

# OCR Pipeline

## Contract

Input is an owned `PreprocessedFrame`, never the original capture frame. Output is an `OperationResult<OcrResult>` containing positioned text spans or a provider-neutral failure. `IOcrEngine` exposes supported languages/devices and owns its native runtime until asynchronous disposal. The OCR engine does not translate, persist, log, or retain images.

## Stages

1. Validate frame dimensions and region bounds.
2. Map normalized regions to pixels using capture-content size, not overlay DPI.
3. Crop with pooled buffers; optionally upscale 1.5-2x.
4. Apply configurable grayscale, contrast, threshold, sharpen, and subtitle-background suppression presets.
5. Run PaddleOCR detection and recognition with the selected local language model.
6. Reject low-confidence/invalid spans, restore coordinates, sort by region then reading order, and join compatible fragments.
7. Normalize Unicode (NFKC where profile permits), whitespace, punctuation, and common OCR artifacts.
8. Stabilize across frames before emitting a translation request.

## Preprocessing implementation

`FramePreprocessor` crops each normalized region against capture pixels, never overlay or screen DPI. Operations are deterministic and composable in this order: crop, optional Gray8 conversion, contrast, threshold, inversion, sharpening, scaling, then padding. The no-op preset performs only an exact BGRA crop. Conservative `plainSubtitle` and `outlinedSubtitle` presets are serializable in both application configuration and game profiles.

Every intermediate and output buffer comes from an injectable pool. Returned `PreprocessedFrame` instances own their region images; consumers must dispose the frame after local OCR. Buffers are requested to be cleared when returned because they may contain sensitive visible text. Partial outputs are disposed on validation failure, cancellation, diagnostic failure, and resolution-cap failure.

Output is capped by configured width, height, and total pixels before allocation. Cancellation is checked between regions and at least every 32 image rows during expensive stages. The final TASK-008 verification measured the synthetic 640×360 reference fixture (576×90 subtitle crop, outlined preset, 2× scaling) at p50 0.97 ms and p95 1.14 ms on the 16-logical-CPU development host, within the 16–50 ms capture/crop budget. Results vary by hardware and should be re-baselined on release runners.

Diagnostic images remain disabled by default. Enabling the preprocessing flag also requires explicit diagnostic-screenshot consent and a separately configured sink that warns about sensitive text and enforces retention/redaction. The default sink refuses all writes; no preprocessing output is persisted automatically.

## Temporal stabilization

`OcrTextNormalizer` first orders regions according to the active profile, clusters spans into lines by vertical overlap, then orders fragments left-to-right. Latin fragments use NFKC, normalized quote/spacing rules, and spaces between word fragments. Chinese/Japanese fragments use NFC, preserve CJK punctuation, and avoid artificial inter-character spaces. Line breaks remain explicit, control characters are removed, and up to 32 literal language/profile artifact replacements may be configured. Normalized text is capped at 4,096 characters and source spans at the configured per-observation limit.

`TemporalTextStabilizer` tracks candidates by normalized Levenshtein similarity plus spatial IoU. Defaults require two observations inside 600 ms with 0.65 average confidence. Unchanged or near-identical jitter emits nothing; incremental growth requires confirmation; an unrelated line at 0.97 confidence can emit `Changed` immediately. Two consecutive empty/low-confidence observations emit `Disappeared`, after which the same text must stabilize again and emits `New`.

Events carry the triggering session/sequence metadata and are `New`, `Changed`, or `Disappeared`. Duplicate or lower sequences are ignored before they can mutate state. A session, profile, language, ordered-region set, or artifact-rule change resets all candidates. Explicit reset is cancellable. Candidate history (default 8), region count (32), artifact rules (32), spans (128), and normalized text are hard-bounded; no unbounded history is retained and no wall clock sleeps are required in tests.

## PaddleOCR adapter

`PaddleOcrEngine` hides all Sdcb/OpenCV types behind `IPaddleOcrRuntime`. It consumes Gray8 or BGRA preprocessed images, maps detected pixel rectangles back into source normalized coordinates, applies NFKC normalization and confidence filtering, and sorts spans by configured region then top-to-bottom/left-to-right reading order.

Inference uses CPU/MKL only in V1 and is serialized by a capacity-one semaphore because the native runtime is treated as non-thread-safe. Cancellation is checked before queueing, immediately before native work, and after native work. Paddle's synchronous native call cannot be interrupted safely mid-inference; a cancellation requested during that call is observed as soon as it returns. Runtime initialization/inference failures reset and dispose the native engine before a retry.

The default factory now uses `ReusablePaddleOcrRuntime`: one owned detector/recognizer pair per engine, serial inference with width-sorted recognition batches and original output ordering. It preserves pinned PaddleSharp preprocessing, CTC confidence and rectangle conventions without reflection. The caller token reaches the runtime through `IPaddleOcrRuntime.Recognize(image, token)` and is checked between regions, crops and batches; disposal waits for owned native work to finish. The interface retains a pre/post-cancel compatibility implementation for legacy adapters. Explicit `reusePredictors: false` selects the old upstream clone-per-call implementation for comparison/rollback. This optimization does not waive model integrity verification or establish full-application latency/soak acceptance.

The unified PP-OCRv5 mobile recognition model covers English, Simplified Chinese, Traditional Chinese, and Japanese. Empty text is a successful result. Missing/partial and corrupt models map to distinct `ModelMissing` and `ModelCorrupt` failures. See `OCR_DEPLOYMENT.md` for packages, checksums, provisioning, and native-test instructions.

## Failure behavior

No text is a valid result. Missing model, corrupt model, unsupported language, native load failure, and inference failure are distinct error codes. The pipeline backs off repeated failures and keeps the UI responsive.

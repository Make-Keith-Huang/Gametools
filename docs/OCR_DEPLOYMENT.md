# PaddleOCR deployment and model provisioning

## Binding/runtime decision

The Windows x64 build pins these packages in `GameLingoAI.Infrastructure.csproj`:

| Package | Version | Purpose |
| --- | ---: | --- |
| `Sdcb.PaddleOCR` | 3.3.1 | Maintained .NET Standard PaddleOCR binding |
| `Sdcb.PaddleInference.runtime.win64.mkl` | 3.3.1.70 | Windows x64 CPU/MKL native runtime |
| `OpenCvSharp4` | 4.13.0.20260627 | Explicit matching managed API |
| `OpenCvSharp4.runtime.win.slim` | 4.13.0.20260627 | Windows x64 slim OpenCV native runtime |

The binding and PaddleOCR project are Apache-2.0 licensed. The current binding release supports Windows x64 and Paddle Inference 3.x. CPU/MKL is deliberately the only advertised V1 device; no CUDA dependency or GPU assumption is present. Binding, inference, and OpenCV types are isolated in `SdcbPaddleOcrRuntime.cs`; Core remains binding-free.

The optional `Sdcb.PaddleOCR.Models.Online` package is intentionally not referenced because it downloads models on first use. Model installation instead goes through the application provisioner so consent, metadata, partial recovery, and verification cannot be bypassed.

As of2026-10-10, the default CPU adapter is `ReusablePaddleOcrRuntime`, with owned serial predictors and no per-call cloning/reflection. `SdcbPaddleOcrRuntimeFactory(threadCount, reusePredictors: false)` retains the pinned upstream baseline. The adapted algorithms retain the PaddleSharp Apache-2.0 attribution noted in the source; package/model versions are unchanged. Actual default-path probe52442 passed38 corpus checks and7 lifecycle groups; full solution5639 passed Release0 warnings/errors,69 defaults and format. Full capture/translation/overlay regression and updated packaging remain pending; existing package0.1.0.14 does not contain this source change.

## Built-in model manifest

The model is Paddle's unified PP-OCRv5 mobile detector/recognizer. It supports the V1 English, Simplified Chinese, Traditional Chinese, and Japanese choices.

| Official archive | Bytes | SHA-256 |
| --- | ---: | --- |
| `PP-OCRv5_mobile_det_infer.tar` | 4,935,680 | `50446E5D01AC2A73D5319C89513281F6578414C888C602F9AF13F93FEEFFFC58` |
| `PP-OCRv5_mobile_rec_infer.tar` | 16,834,560 | `566B9512B34E34A9F0DB54D87B51FA5A0B9ED2CF1AB7E49728CC0B8B5A64F414` |

Total download size is 21,770,240 bytes (20.8 MiB). The UI displays the size, Apache-2.0 license URL, PaddleOCR source URL, and languages before installation.

## Explicit installation and recovery

Open **Profiles / Settings** and select **Download, verify, and install**. Merely opening the application or checking status performs no network request.

The provisioner:

1. accepts HTTPS sources only and caps each archive at 512 MiB;
2. retains `.partial` files after cancellation and resumes with a validated HTTP Content-Range; fully downloaded, checksum-verified archives are reused without networking;
3. validates exact byte count and SHA-256 before extraction;
4. rejects absolute/traversal paths, links, and unsupported TAR entry types;
5. extracts into a staging directory and atomically replaces the installed package;
6. records SHA-256 and size for every extracted file and revalidates them before each engine call; empty/malformed/duplicate entries or omitted required model files cannot pass;
7. checks cancellation before creating provisioning directories and immediately before committing installation.

A bad downloaded checksum is discarded so the next attempt starts cleanly. A cancelled partial download remains resumable. A missing or partial install is actionable as `ModelMissing`; an invalid integrity manifest or modified installed file is `ModelCorrupt`.

Models are stored under `%LOCALAPPDATA%\GameLingoAI\models\ocr`. They contain local inference data only and are never executed as arbitrary binaries. Captured or preprocessed pixels are not written alongside models.

## Native integration verification

On 2026-10-06, with explicit user consent, the isolated [compatibility probe](../tests/GameLingoAI.NativeCompatibilityProbe/README.md) downloaded the catalog's two official archives into `artifacts/native-compatibility/models` through the production checksum/extraction implementation. Both verified, and the production runtime recognized the expected synthetic English/numeric text with valid geometry in Gray8 and BGRA32, over two create/dispose cycles. Candidate build had zero warnings/errors; after replacement the full 69 default + 5 platform gate passed. This proves a local native smoke case, not four-language accuracy, clean-OS compatibility or a long soak. User models were not modified.

The slim DLL's actual imports still include `mf.dll`, `mfplat.dll`, `mfreadwrite.dll`; preflight requirements below remain valid. Build metadata still says non-free enabled in the shared OpenCV build. Reduced wrapper exports do not settle static-component licensing; public release remains disabled. See [native attribution](NATIVE_ATTRIBUTION.md).

### Native prerequisites (checked 2026-10-05)

Self-contained .NET does not include all OCR native dependencies. `dumpbin /dependents` on the pinned published binaries found:

| Binary | Additional Windows prerequisites |
| --- | --- |
| `mkldnn.dll` | `vcomp140.dll` |
| `onnxruntime.dll`, `paddle2onnx.dll` | `msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` |
| `OpenCvSharpExtern.dll` | `mf.dll`, `mfplat.dll`, `mfreadwrite.dll` (alongside standard Windows libraries) |

These seven files were absent from the package and present in the tested computer's system directory. Install/repair the [official Microsoft x64 Visual C++ Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist) if required. Windows N may require the operating system's Media Feature Pack. No system component is installed automatically. OpenCV's linked Media Foundation libraries do not mean this application captures audio; no audio capture path is implemented.

`OcrRuntimePrerequisites` checks x64 process/AVX support and file presence without loading native code. The model settings status reports missing prerequisites on construction and after status/provisioning operations. A positive presence check is not proof of correct binary version, architecture, loadability or working inference. `OcrPrerequisiteTests` exercises missing/present prerequisites and real model-status commands against an empty isolated directory, asserting zero HTTP requests, filesystem writes or reported command errors. Actual clean-machine UI and native inference are separate pending evidence.

Unit and smoke tests use a deterministic fake runtime, including four-language mapping, model errors, cancellation, serialized inference, and 1,000 frames across 20 create/dispose cycles. The native test is opt-in because CI and a clean developer checkout do not silently install external models.

After installing the built-in model in the UI, prepare a rectangular text fixture whose lines contain `#` for white pixels and any other character for black pixels, then run:

```powershell
$env:GAMELINGOAI_RUN_NATIVE_OCR_TESTS = '1'
$env:GAMELINGOAI_OCR_MODELS_ROOT = "$env:LOCALAPPDATA\GameLingoAI\models\ocr"
$env:GAMELINGOAI_OCR_NATIVE_FIXTURE = 'C:\path\to\large-english-text-fixture.txt'
dotnet test GameLingoAI.sln -c Release --no-build --filter Suite=OptIn
```

The fixture must be large/high-contrast enough for OCR and must contain non-sensitive synthetic text. The test asserts a non-empty native result and uses the same integrity manifest and CPU adapter as the application.

## Packaging checklist

- Publish for `win-x64`; do not combine the MKL and CUDA Paddle runtime packages.
- Preserve the native assets emitted by NuGet under the published runtime directory.
- Include the Apache-2.0 notices for PaddleOCR/PaddleSharp and OpenCV in release attribution.
- Test on a supported AVX-capable Windows 10 1903+ or Windows 11 x64 host.
- Do not bundle downloaded model archives into logs, diagnostics, or crash reports.

# GameLingoAI Codex DevKit

开发试用版，尚未完成正式发布验收。中文操作说明见[使用指南](docs/USER_GUIDE.md)，当前包和验收限制见[发布审查](docs/RELEASE_AUDIT.md)。源码采用 [MIT](LICENSE)；第三方组件及模型不因本项目许可证而改变其原有许可。不要将本地模型、数据库、密钥或构建缓存提交到 GitHub。

GameLingoAI is a Windows desktop companion that captures a user-selected game window, recognizes subtitle text, translates it with a configurable provider, and renders the result in a transparent click-through overlay.

## V1 scope

- Windows 10 1903+ / Windows 11, x64.
- C# and .NET 8, WPF, MVVM, dependency injection.
- Windows Graphics Capture for user-authorized window capture.
- PaddleOCR through a replaceable OCR adapter.
- SQLite-backed profiles, terminology, history, and translation cache.
- Serilog structured diagnostics with secret redaction.
- OpenAI-compatible, Ollama, and custom HTTP translation providers.
- Borderless, transparent, topmost, mouse-click-through subtitle overlay.

V1 never captures audio, performs STT/TTS, injects DLLs, hooks game memory, modifies game files, or bypasses anti-cheat/security controls.

## Start here

For using the current development build, see the [Chinese user guide](docs/USER_GUIDE.md). For version, checksum, verification evidence and outstanding release gates, see the [release audit](docs/RELEASE_AUDIT.md). Development packaging is available; public-release acceptance is not yet complete.

1. Read `AGENTS.md`, `PRODUCT_REQUIREMENTS.md`, and `ARCHITECTURE.md`.
2. Install a serviced .NET 8 SDK and Visual Studio 2022 with the **.NET desktop development** workload.
3. Open PowerShell at the repository root and run the exact setup commands below.
4. Give `prompts/00_PROJECT_INIT.md` to Codex, then execute `TASK-001.md` through `TASK-025.md` in order unless a task explicitly allows parallel work.
5. Keep each task in its own branch and require its acceptance checks before merging.

## Build and test

Exact setup and verification commands:

```powershell
dotnet --version
dotnet restore GameLingoAI.sln
dotnet format GameLingoAI.sln --verify-no-changes --no-restore
dotnet build GameLingoAI.sln -c Release --no-restore
dotnet test GameLingoAI.sln -c Release --no-build --results-directory artifacts/test-results
```

For the same checks with fail-fast behavior, run `pwsh -NoProfile -File scripts/verify.ps1 -DataRoot <absolute-local-work-directory>`. This workspace defaults to `D:\ai工作数据和目录存放点\GameLingoAI`; other machines should explicitly choose their own writable data directory. The script uses a single MSBuild node and routes CLI home, temporary files, new NuGet caches and artifacts beneath DataRoot. It restores process environment settings on exit.

Direct `dotnet` commands above use the ignored repository `artifacts` directory unless `GAMELINGOAI_ARTIFACTS_PATH` is set. The verification/packaging scripts use the selected DataRoot instead. Unit and integration test naming and isolation rules are documented in `tests/README.md`.

The repository targets .NET 8 and requests SDK `8.0.100` in `global.json`. Its explicit `latestMajor` roll-forward keeps the package-free bootstrap usable when a machine has only a supported newer SDK; release and CI environments should install a serviced .NET 8 SDK so the intended toolchain is exercised. C# is fixed at version 12 and analyzer rules at .NET 8 to prevent a newer fallback SDK from silently changing language or analysis behavior.

After a successful build, start `src/GameLingoAI.App`. Local OCR models are not downloaded automatically; review their size, source, and license and install them explicitly from **Profiles / Settings**. See `docs/OCR_DEPLOYMENT.md` for the pinned native runtime and verification process.

## Repository map

- `src/GameLingoAI.App`: WPF composition root and UI.
- `src/GameLingoAI.Core`: domain models, contracts, and use-case boundaries.
- `src/GameLingoAI.Infrastructure`: capture/OCR/translation/persistence adapters.
- `tests/GameLingoAI.Tests`: maintained xUnit/VSTest cases with default/platform/opt-in separation.
- `docs`: focused design specifications.
- `prompts`: directly executable Codex engineering prompts.

Platform and third-party adapters are completed incrementally by the numbered task sequence; later-task placeholders may remain until their task is accepted.

For self-contained Windows MSIX development packaging, local test-signing policy and deferred release gates, see [packaging](docs/PACKAGING.md). `pwsh -NoProfile -File scripts/package.ps1` produces unsigned internal artifacts; it does not install the application, trust certificates or download OCR models.

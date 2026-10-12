# Product Requirements

## Product statement

GameLingoAI V1 helps a Windows player understand on-screen game subtitles by recognizing text in a selected capture region, translating it, and drawing readable translated text over the game without interacting with the game process.

## Target user and jobs

The primary user plays a windowed, borderless-windowed, or compatible full-screen game whose subtitle language they do not understand. They need to select the game, mark a subtitle region, choose languages/provider, preview recognition, start translation, and stop instantly.

## Functional requirements

1. Enumerate capturable top-level windows and let the user explicitly choose one.
2. Preview the selected window and define one or more normalized OCR regions.
3. Capture at a configurable rate with bounded memory and no game-process access.
4. Preprocess frames and run PaddleOCR locally through a replaceable engine contract.
5. Normalize, merge, deduplicate, and stabilize OCR lines across frames.
6. Translate stable text via OpenAI-compatible, Ollama, or custom HTTP adapters.
7. Apply glossary, cache, retries, timeout, cancellation, and rate limiting.
8. Render translated subtitles in a topmost transparent click-through overlay aligned to the chosen window.
9. Persist game profiles, regions, preferences, glossary, cache, and optional text history in SQLite.
10. Expose session status, latency, provider errors, and safe diagnostic export.

## User experience requirements

- First-run setup explains local OCR and remote-text privacy before any remote request.
- Start/stop is obvious and also available as a configurable global hotkey.
- Default overlay uses high-contrast text with outline/background and respects DPI scaling.
- Lost/minimized/closed windows pause capture and show a recoverable state.
- Offline/cache-only mode remains useful after models are installed.

## Non-functional requirements

- Target median glass-to-glass latency: <= 800 ms for cached translations and <= 1,800 ms for remote translations under normal network conditions.
- Capture queue capacity <= 2; stale work is dropped in favor of current subtitles.
- Idle CPU <= 2% on reference hardware; active CPU target <= 20% excluding OCR backend variation.
- No sustained private-memory growth above 50 MB during a 60-minute soak after warm-up.
- Crash-free session target >= 99.5%; all pipeline stages support cancellation within 2 seconds.
- UI remains responsive while capture/OCR/network/database work runs.

## Privacy and security

- Screenshots stay in memory and local processing only; diagnostic screenshot capture is opt-in and disabled by default.
- Remote providers receive only normalized OCR text and the minimum prompt context.
- API credentials are encrypted by the OS; exported profiles exclude secrets.
- Logs redact secrets and truncate/redact recognized content by default.
- Database history collection is off by default and can be cleared by the user.

## Explicitly out of scope for V1

- Any audio, STT, TTS, dubbing, microphone, or voice feature.
- DLL injection, game memory access, API hooking, graphics interception, kernel drivers, anti-cheat bypass, or game-file modification.
- Mobile/macOS/Linux, cloud screenshot OCR, multiplayer collaboration, subtitle-file patching, automated gameplay, or input simulation.

## Release acceptance

Release is allowed only after all P0 tests in `TEST_PLAN.md` pass, the privacy flow is verified, no prohibited technique is present, the packaged app runs on a clean Windows 10 and Windows 11 VM, and the known limitations are documented.

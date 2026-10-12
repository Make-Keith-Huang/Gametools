# Task Index

Execute `prompts/00_PROJECT_INIT.md` once, then use the tasks below. Each prompt is self-contained and includes scope, constraints, acceptance criteria, and verification.

| Task | Outcome | Depends on |
|---|---|---|
| 001 | Solution baseline and quality gates | Init |
| 002 | Core domain models and contracts | 001 |
| 003 | Configuration, validation, secret storage contract | 002 |
| 004 | WPF MVVM shell and DI composition | 002-003 |
| 005 | Window discovery and selection | 002,004 |
| 006 | Windows Graphics Capture adapter | 005 |
| 007 | Capture-region editor and coordinate mapping | 005-006 |
| 008 | Frame preprocessing | 007 |
| 009 | PaddleOCR adapter and model management | 008 |
| 010 | OCR normalization and temporal stabilization | 009 |
| 011 | Translation provider foundation | 002-003 |
| 012 | OpenAI-compatible provider | 011 |
| 013 | Ollama provider | 011 |
| 014 | Custom HTTP provider | 011 |
| 015 | Glossary and prompt construction | 011-014 |
| 016 | Translation memory/cache | 015,021 recommended |
| 017 | End-to-end bounded pipeline orchestration | 006,010-016 |
| 018 | Transparent click-through overlay | 017 |
| 019 | Game profiles and settings UI | 007,018,021 |
| 020 | Session controls and global hotkeys | 017-019 |
| 021 | SQLite database and migrations | 002 |
| 022 | Serilog diagnostics and safe export | 003,017 |
| 023 | Automated tests, benchmarks, soak harness | 017-022 |
| 024 | Packaging and model provisioning | 023 |
| 025 | Release hardening and documentation | all |

Tasks 011-014 may be split across branches after Task 011. Task 021 can run alongside capture/OCR work after Task 002. Resolve contract changes centrally before merging parallel work.

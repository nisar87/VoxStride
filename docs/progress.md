# Progress report and independent audit

_Last updated: 26 November 2026, after green CI run 45760942621 (commit f53ddf6 and later docs)._

## Summary
VoxStride builds, tests, packages, installs and uninstalls cleanly on Windows in CI. The core safety and voice contract is implemented and verified by automated tests: explicit commit, authorization, journaled and verified execution, undo, and three provider adapters. The pieces that **cannot be verified without a physical Windows 11 PC, a real microphone or live API keys** are implemented but marked unverified below. Two spec items are not started: the browser extension for the user's existing browser (P5b) and the UIA/vision fallback worker (part of P4).

This is **not** a claim of "100% complete".

## Test results (actual, from CI)
| Suite | Linux | Windows |
|---|---|---|
| Core (broker, policy, router, voice, files, providers, run coordinator) | 160 / 160 passed | 160 / 160 passed |
| Persistence (SQLite) | 5 / 5 | 5 / 5 |
| Windows integration (known folders, Credential Manager, Recycle Bin, app catalog, **Notepad write + read-back**, speech env) | — | 14 / 14 |
| Installer: silent per-user install → smoke test from installed exe → uninstall → data removed | — | passed |
| Vulnerable-package audit | — | clean, after pinning SQLitePCLRaw 2.1.13 for GHSA-2m69-gcr7-jv3q |
| Secret scan | passed | — |

Evidence: [benchmarks/2026-11-26-ci-windows-latest.json](benchmarks/2026-11-26-ci-windows-latest.json).

Measured on the CI runner, not a target PC: host startup 288 ms (headless), working set 70.9 MB (headless, no UI/speech), direct drive read 15–42 ms commit → verified, app catalog 116 entries.

## Phase status

| Phase | Status | Notes |
|---|---|---|
| P0 research/spikes | **Partial** | Research, ADRs, threat model and docs done. Competitor hands-on trials and the UFO VM benchmark are pending (they need a disposable Windows VM). |
| P1 safe vertical slice | **Verified** | Contracts, broker, journal, router, three adapters (fixture-verified), disk and app tools, CI. |
| P2 voice engine | **Implemented; logic verified** | 240-trial scripted gate: zero premature commits. 0.3.0 rebuilt the audio path on one shared microphone (ADR-0009). The offline engine is verified end to end on Windows CI with synthesized speech fed as live audio. The owner's physical microphone, wake latency and false-accept rate are unmeasured. |
| P3 files/system | **Verified** | Organizer, exclusions, collisions, path escapes, TOCTOU, undo conflicts, crash reporting, temp cleanup with measured bytes. |
| P4 desktop apps | **Partial** | Notepad and Notepad++ adapter (exact-HWND write and read-back; Notepad verified on CI). Not started: UIA worker process, vision fallback, multi-monitor/DPI GUI tests. |
| P5 browser | **Partial** | P5a Playwright and Edge (YouTube visible-row selection, ChatGPT draft-only) implemented, with layout logic unit-verified. Live sites unverified. P5b existing-browser extension not started. |
| P6 UX/hardening | **Implemented** | WPF Fluent UI, overlay, approvals, history/undo, privacy, budgets, diagnostics, hotkeys, lock handling, installer. Physical UI/accessibility review, code signing and an independent security review are pending. |
| P7 expansion | Not started (by design) | — |

## Flagship scenarios (spec §18)
| # | Scenario | State |
|---|---|---|
| 1 | Long spoken instruction with pauses + explicit finish | Logic verified (240 trials). Real-mic verification pending. |
| 2 | Desktop organization with exclusions + undo | Verified on synthetic fixtures (Linux + Windows). |
| 3 | Drive check + confirmed temp cleanup | Drive check verified from the installed exe. Cleanup verified on fixtures. |
| 4 | Open Notepad++ and write requested text | Notepad path verified on CI. Notepad++ not installed on the runner, so it needs a physical test. The text-generation step needs a live provider. |
| 5 | ChatGPT draft + YouTube precise selection | Implemented. Live verification pending (needs sign-in and network). |

## Open items that need the owner
1. **Live API keys** (Claude, Gemini, OpenAI test keys with small budgets) to run the live smoke tests in [acceptance-tests.md](acceptance-tests.md#live-provider-smoke-tests). Enter keys only in the app (Windows Credential Manager), never in chat or the repo.
2. **A physical Windows 11 PC with a microphone** (a dedicated test account is recommended) for M-V01, M-V06, wake latency, idle CPU/RAM, Notepad++, YouTube and ChatGPT.
3. **Code-signing certificate** (a paid decision) before any public release.
4. **License choice** for the repository. No license has been added yet, per spec §16 P0.

## Known limitations
- The wake phrase uses Windows grammar recognition. Accuracy and false-accept rate are unmeasured, and the hotkey always works as an alternative.
- Without an OpenAI or Gemini key, dictation uses the offline Windows recognizer, which is weak with non-native accents. Cloud STT (OpenAI/Gemini) is available with consent and a key. Windows online dictation can't be used by an unpackaged app (ADR-0009).
- Push-to-talk is a press-to-start / press-to-finish toggle, not hold-to-talk.
- If Windows 11 Notepad restores a non-empty tab, VoxStride refuses to write rather than overwrite.
- The browser only works in VoxStride's own Edge window, not in the user's existing browser (P5b).
- Cloud-path latency p50/p95 per provider has not been measured.

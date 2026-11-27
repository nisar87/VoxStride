# Roadmap

Ordered by the spec's §16 backlog. P0–P6 are implemented (see `progress.md`). What remains needs the owner's PC, money or an outside party, or is P7 scope.

1. **Physical verification (owner PC).** M-V01 (200 real-mic trials), M-V06 (false wakes per 8 h), wake latency p95, Notepad++ and Windows 11 Notepad, multi-monitor/DPI, live YouTube and ChatGPT, screen-reader pass. Diagnostics logging captures the evidence; record results in `docs/benchmarks/`.
2. **Live provider parity.** Run the smoke tests for Claude, Gemini and OpenAI with the owner's keys. Record model IDs, p50/p95 `t_first_useful_action` and token usage.
3. **Paid release hardening (owner approval required).** Code-signing certificate for the installer and updates; Chrome Web Store / Edge Add-ons listings for the extension.
4. **Independent security review** of the broker, UIA, browser bridge and updater.
5. **Disposable-VM research.** Hands-on competitor trials and the UFO benchmark (P0).
6. **Acoustic echo cancellation** (Windows communications-mode capture) to allow barge-in over loud speakers. Today VoxStride ignores the microphone while it speaks. This must not change the user's Windows sound settings.
7. **P7 expansion.** Office adapters, Urdu/mixed-language evaluation, reusable approved workflows. Each needs its own acceptance suite.

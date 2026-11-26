# Roadmap

Ordered by the spec's §16 backlog. Each item lists the gate that closes it.

1. **Physical verification (owner PC or test VM with mic).** M-V01 (200 real-mic trials), M-V06 (false wakes per 8 h), wake latency p95, idle CPU/RAM, Notepad++ and Windows 11 Notepad, live YouTube and ChatGPT. Record results in `docs/benchmarks/`.
2. **Live provider parity.** Run the smoke tests for Claude, Gemini and OpenAI with test keys. Record model IDs, p50/p95 `t_first_useful_action` and token usage.
3. **P4 completion.** Isolated UIA worker process (MTA, watchdog/restart), app capability registry, window-scoped capture with a vision fallback through the same broker, and DPI/multi-monitor GUI tests.
4. **P5b existing-browser extension.** MV3 extension for Chrome and Edge, `activeTab`, a native messaging host restricted to the extension ID, origin/tab/frame validation and run-token binding. Prompt-injection fixture pages.
5. **Push-to-talk hold mode** and an optional cloud STT adapter (separate consent and key).
6. **Release hardening.** Code signing (owner-approved certificate), signed update channel, SPDX SBOM, independent security review, repository license.
7. **P7 expansion.** Office adapters, natural conversation mode, Urdu/mixed-language evaluation, reusable approved workflows. Each needs its own acceptance suite.

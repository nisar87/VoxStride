# Roadmap

P0–P6 of the plan and the live companion (ADR-0013) are implemented; see `progress.md`. What remains needs the owner's PC, money or an outside party, or is new scope.

1. **First real-world run (owner PC).** Run a Gemini Live conversation with the owner's key, plus Twitch co-hosting on a real channel and an Ollama model if wanted. Tune voice, reply length and latency from the diagnostics log.
2. **Physical verification.** Real-microphone trials, wake false-accepts over 8 h, Notepad++ and Windows 11 Notepad, multi-monitor/DPI, speakers barge-in, and a screen-reader pass. Record results in `docs/benchmarks/`.
3. **Microsoft Store listing.** Free for individual developers. It brings free signing, automatic updates and package identity, which unlocks Windows' online speech engine.
4. **Paid hardening (owner approval).** A code-signing certificate for the direct installer, and browser-store listings for the extension.
5. **Independent security review** of the broker, live authority, UIA, the browser bridge, Twitch input and the updater.
6. **GPU-only extras, if the hardware allows.** Local voice cloning (e.g. Chatterbox, MIT) and 3D/anime avatars (VRM).
7. **P7 expansion.** Office adapters, Urdu/mixed-language evaluation, and reusable approved workflows. Each needs its own acceptance suite.

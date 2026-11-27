# ADR-0012: Natural (pause-ended) turns are read-only

- Status: accepted (2026-11-27)
- Context: spec §3 allows an opt-in natural mode with end-of-turn detection, but also requires that write actions need a completed user turn. CLAUDE.md is stricter: "Silence, VAD, timeouts… never commit."
- Decision: in opt-in natural mode, a pause (default 1.5 s) ends the turn as `InputMode.VoiceNatural`. That run's authorization **excludes every mutating tool**. Direct changes are answered with "Say 'VoxStride, execute' to go ahead", and the model is told the turn may only read and answer. A bare "VoxStride, execute" (or Proceed) within 90 s confirms the previous natural request as a normal explicit commit.
- Consequences: natural mode is useful for questions and lookups and never changes anything on silence. It is off by default.
- Evidence: `NaturalModeTests`, `NaturalModeRunTests` (mutating tool refused as `tool_not_allowed`, direct change asks to confirm, bare execute confirms).

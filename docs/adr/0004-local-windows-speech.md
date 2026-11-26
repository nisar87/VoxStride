# ADR-0004: Local Windows speech (System.Speech) as default STT/TTS/wake

- Status: accepted (quality gate pending on real microphones)
- Decision: The default recognizer, wake-phrase detector and voice use installed Windows speech engines via `System.Speech`. "Hey VoxStride" is a constrained grammar with variants and a confidence gate. Words spoken after the wake phrase in the same utterance start the command.
- Rationale: works offline, needs no second API key (Claude-only mode works), needs no model training or Python, and adds negligible standby cost.
- Risks: dictation accuracy is below modern cloud STT. False-accept rate is unmeasured. Mitigations: typed edit of transcript, uncertain-finish confirmation, optional cloud STT adapter behind `ISpeechRecognizer` (not default).

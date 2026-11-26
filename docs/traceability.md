# Traceability: requirement → implementation → tests → status

Status legend: **Verified** (automated evidence in CI), **Implemented** (code complete, needs physical/live evidence), **Partial**, **Not started**.

| Req | Requirement (spec §) | Implementation | Tests / evidence | Status | Unresolved risk |
|---|---|---|---|---|---|
| R-001 | Repo, docs, CLAUDE.md, .gitignore, no secrets (§16 P0) | root files, `docs/` | CI secret scan | Verified | — |
| R-002 | Research & ADRs (§12) | `docs/research.md`, `docs/adr/0001-0008` | — | Implemented | Competitor hands-on matrix pending VM |
| R-003 | Threat model (§8) | `docs/security/threat-model.md` | — | Implemented | No independent review yet |
| R-010 | .NET 10 solution, CPM, analyzers, nullable (§15.1) | `Directory.*.props`, `global.json` | CI build (warnings as errors) | Verified | — |
| R-011 | Typed contracts (§7) | `src/VoxStride.Contracts` | compile + contract tests | Verified | — |
| R-012 | Authorization boundary & broker order (§15.1) | `RunAuthority`, `ToolBroker` | `BrokerSecurityTests` (17) | Verified | — |
| R-013 | Journal: intent before, outcome after, redacted (§7, §9) | `IExecutionJournal`, `SqliteStore` | `BlockedOperationsAreJournaledRedacted`, `SqliteStoreTests` | Verified | — |
| R-014 | Intent router: direct path, negation, multi-clause (§13.2) | `IntentRouter` | `RouterTests` (40+ cases) | Verified | Grammar coverage grows with use |
| R-015 | Three real provider adapters + parity (§15.2) | `ClaudeProvider`, `OpenAIProvider`, `GeminiProvider` | `ProviderContractTests` (fixtures) | Verified (fixtures) / **Live pending** | Live keys not available to CI |
| R-016 | Disk/free-space & app launch (§16 P1) | `GetFreeSpaceTool`, `LaunchAppTool`, `WindowsAppCatalog` | `L01`, `AppCatalogFindsBuiltIns…`, smoke test | Verified | — |
| R-017 | Key storage in Windows credential store (§5) | `CredentialSecretStore` | `CredentialStoreRoundTripsAndDeletes` | Verified | — |
| R-018 | Cancel hotkey & event channel, no UI blocking (§13.4) | App hotkeys, `EventHub`, async throughout | `L03`, `L04`, code review | Implemented | UI responsiveness measured only manually |
| R-020 | Explicit commit; pauses never commit (§3, §14) | `ConversationCoordinator`, `CommandBuffer`, `ControlPhraseDetector` | `V01–V05`, 240-trial gate | Verified (logic) | Real-mic trials pending (M-V01) |
| R-021 | 30 s reminder / 120 s discard, never auto-execute (§14.2) | Coordinator ticks | `ReminderAt30sAndDiscardAt120s…` | Verified | — |
| R-022 | Local STT/TTS/earcons; Claude-only voice (§5, §14.1) | `VoxStride.Speech` | `SpeechEnvironmentReports…`, `EarconsAreValidWav` | Implemented | Accuracy & latency on real mic unmeasured |
| R-023 | Wake phrase, no model training (§14.1) | `WindowsWakeWordDetector` (grammar) | — | Implemented | False-accept rate unmeasured (V06) |
| R-024 | Lock/sleep/mute handling (§14.2) | App `SessionSwitch`/`PowerModeChanged`, coordinator | `V05_LockAndMuteDiscardDraft` | Verified (logic) | — |
| R-025 | Push-to-talk | Hotkey toggle (press to talk, press to proceed) | — | Partial | Hold-to-talk not implemented |
| R-030 | Desktop organizer: manifest, exclusions, never overwrite (§15.3) | `OrganizationPlanner`, `MoveExecutor`, tools | `F01`, `F02*`, exclusions | Verified | OneDrive placeholder handling unverified on real OneDrive |
| R-031 | Undo with conflict detection (§15.3) | `MoveExecutor.UndoAsync` | `F04`, `UndoRestores…` | Verified | — |
| R-032 | Crash reconciliation, no auto-resume (§7) | journal unresolved report at startup | `F03` | Verified | — |
| R-033 | Temp cleanup with approval & measured bytes (§15.4) | `TempCleaner`, cleanup tools, `WindowsFileRemover` | `F05`, `RecycleBinRemover…` | Verified | — |
| R-034 | Standing rule only for reversible same-root moves (§8, §15.3) | `StoredStandingRules` | `StandingRuleCoversOnly…` (×2) | Verified | — |
| R-040 | Verified editor insertion, no wrong-window text (§15.5) | `WindowsEditorAdapter` (WM_SETTEXT/WM_GETTEXT) | `NotepadWriteIsVerifiedByReadBack` (Windows CI) | Verified (Notepad on CI) | Win11 Notepad session-restore & Notepad++ need physical test |
| R-041 | UIA worker, vision fallback, app capability registry (§15.5) | — | — | Not started | — |
| R-050 | Browser dedicated profile, YouTube visible-row selection (§15.6) | `PlaywrightBrowserController`, `VideoLayout` | `VideoLayoutTests` | Implemented | Live YouTube DOM unverified |
| R-051 | ChatGPT draft-only, never submit (§15.6) | `ChatGptDraftTool` | design (no submit tool) | Implemented | Live DOM unverified |
| R-052 | MV3 extension + native messaging (§6, §15.6) | — | — | Not started | — |
| R-060 | Overlay, task panel, approvals, history, settings (§9) | `VoxStride.Desktop` | CI build | Implemented | Visual/a11y review on real display pending |
| R-061 | Cost/token budgets (§5) | `RunOptions`, daily budget in `ProviderSelector` | `StepLimitIsEnforced` | Verified (step), Implemented (daily) | — |
| R-062 | Privacy: retention, clear data, revoke (§8, §9) | `SqliteStore`, Privacy page | `HistoryOmitsTranscript…` | Verified | — |
| R-063 | Per-user signed installer, clean uninstall (§9, §16 P6) | `installer/VoxStride.iss`, `scripts/Test-Installer.ps1` | INST-01 in Windows CI | Verified (unsigned) | Code-signing certificate needs owner approval |
| R-064 | Dependency audit & inventory (§8) | CI vulnerable-package gate, `dependencies.json` artifact | CI | Verified | Full SPDX SBOM not generated |
| R-065 | Latency instrumentation (§13.3) | `LatencyRecorder`, run timings in history | `L01` p95 assertion | Partial | Cloud p50/p95 per provider unmeasured |

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
| R-022a | Live audio reaches the recognizer; microphone always released; one shared device (ADR-0009) | `AudioHub`, `PcmPipeStream`, `WindowsSpeechRecognizer`, `MicWatchdog` | `PcmPipeStreamTests`, `AudioHubTests`, `SpeechPipelineTests` (Windows E2E) | Implemented | Owner's physical microphone unverified |
| R-023 | Wake phrase, no model training (§14.1) | `WindowsWakeWordDetector` (grammar) on the shared hub, pre-roll replay at hand-over | `TestWakeAsync` (Settings), hub replay tests | Implemented | False-accept rate on a real mic unmeasured (V06) |
| R-024 | Lock/sleep/mute handling (§14.2) | App `SessionSwitch`/`PowerModeChanged`, coordinator | `V05_LockAndMuteDiscardDraft` | Verified (logic) | — |
| R-025 | Push-to-talk (toggle and hold-to-talk) | Hotkey toggle + `TalkKeyMode.Hold` (GetAsyncKeyState release = commit) | Host code only (WPF key polling); no automated test | Implemented | Needs a physical keyboard check |
| R-030 | Desktop organizer: manifest, exclusions, never overwrite (§15.3) | `OrganizationPlanner`, `MoveExecutor`, tools | `F01`, `F02*`, exclusions | Verified | OneDrive placeholder handling unverified on real OneDrive |
| R-031 | Undo with conflict detection (§15.3) | `MoveExecutor.UndoAsync` | `F04`, `UndoRestores…` | Verified | — |
| R-032 | Crash reconciliation, no auto-resume (§7) | journal unresolved report at startup | `F03` | Verified | — |
| R-033 | Temp cleanup with approval & measured bytes (§15.4) | `TempCleaner`, cleanup tools, `WindowsFileRemover` | `F05`, `RecycleBinRemover…` | Verified | — |
| R-034 | Standing rule only for reversible same-root moves (§8, §15.3) | `StoredStandingRules` | `StandingRuleCoversOnly…` (×2) | Verified | — |
| R-040 | Verified editor insertion, no wrong-window text (§15.5) | `WindowsEditorAdapter` (WM_SETTEXT/WM_GETTEXT) | `NotepadWriteIsVerifiedByReadBack` (Windows CI) | Verified (Notepad on CI) | Win11 Notepad session-restore & Notepad++ need physical test |
| R-041 | UIA worker, vision fallback (§15.5, ADR-0010) | `UiSession`, `UiTools`, `WindowsUiAutomation` (MTA worker, timeouts), `CaptureWindowTool` + provider image input | `UiToolsTests`, `UiAutomationTests`, `UiAutomationGateTests` 100/100 ×3 (Windows CI), `ProviderImageTests` | Verified (fixture app) | Multi-monitor/DPI and elevated apps on a physical PC |
| R-050 | Browser dedicated profile, generic web tools, YouTube visible-row selection (§15.6) | `PlaywrightBrowserController` (IWebAutomation), `WebTools`, `VideoLayout` | `WebToolsTests`, `VideoLayoutTests`, `BrowserTests` + `BrowserGateTests` 100/100 (real Edge, fixtures) | Verified (fixtures) | Live YouTube DOM changes over time |
| R-051 | ChatGPT draft-only, never submit (§15.6) | `ChatGptDraftTool` | design (no submit tool) | Implemented | Live DOM unverified |
| R-052 | MV3 extension + native messaging (§6, §15.6, ADR-0011) | `extension/`, `ExtensionBrowser`, `BrowserBridgeServer`, `NativeHostRelay`, installer HKCU host keys | `ExtensionBridgeTests`, `ExtensionEndToEndTests` (real Edge, CI), installer host checks | Verified | Store listing needs paid developer accounts (owner approval) |
| R-060 | Overlay, task panel, approvals, history, settings (§9) | `VoxStride.Desktop` | CI build | Implemented | Visual/a11y review on real display pending |
| R-061 | Cost/token budgets (§5) | `RunOptions`, daily budget in `ProviderSelector` | `StepLimitIsEnforced` | Verified (step), Implemented (daily) | — |
| R-062 | Privacy: retention, clear data, revoke (§8, §9) | `SqliteStore`, Privacy page | `HistoryOmitsTranscript…` | Verified | — |
| R-063 | Per-user signed installer, clean uninstall (§9, §16 P6) | `installer/VoxStride.iss`, `scripts/Test-Installer.ps1` | INST-01 in Windows CI | Verified (unsigned) | Code-signing certificate needs owner approval |
| R-064 | Dependency audit & inventory (§8) | CI vulnerable-package gate, `dependencies.json` artifact | CI | Verified | Full SPDX SBOM not generated |
| R-065 | Latency & resource targets (§11, §13.3) | `LatencyRecorder`, smoke test, `Test-Installer.ps1` idle step | Installed build on CI: direct p50 5.7 / p95 8.5 ms (30 runs), stop call 0.2 ms, idle CPU 0.18 %, 148 MB | Verified (direct path) | Cloud p50/p95 per provider needs live keys |
| R-026 | Natural (pause-ended) mode is read-only (§3, ADR-0012) | `InputMode.VoiceNatural`, `RunCoordinator` read-only filtering | `NaturalModeTests`, `NaturalModeRunTests` | Verified | — |
| R-027 | Pause/resume a running task (§13.4) | `RunAuthority.Pause/WaitWhilePaused`, broker gate, overlay/hotkey | `PauseTests` | Verified | — |
| R-028 | Own-voice suppression while speaking (§14) | Final results discarded while TTS is speaking | coordinator tests | Implemented | Not true acoustic echo cancellation; speakers at high volume may still be picked up between replies |
| R-035 | General file tools in a user-granted scope (§15.3) | `FileScope`, `FileOpsTools` (list/search/create/move/rename/copy/recycle), cross-folder undo | `FileOpsTests` | Verified | — |
| R-066 | User permissions: folders, apps, sites, screenshots (§8, §9) | `UserPermissions`, Settings › Permissions, setup wizard | `UserPermissionsTests` | Verified | — |
| R-067 | Provider failover before any step runs (§5) | `ProviderSelector.Fallback`, `RunCoordinator` | `FailoverTests` | Verified | Live outage behaviour needs keys |
| R-068 | Comprehensive redacted diagnostics + report export (§9) | `Log`, `DiagnosticsLog`, `DiagnosticsReport`, HTTP `LoggingHandler` | `LogTests`, `DiagnosticsTrailTests`, smoke-test log check | Verified | — |
| R-069 | Licence and third-party notices (§16 P6) | `LICENSE`, `THIRD-PARTY-NOTICES.md` | — | Implemented | — |

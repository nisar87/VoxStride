# VoxStride architecture

## Process and component view

VoxStride is a single per-user process (`VoxStride.exe`, WPF, .NET 10, `asInvoker`). It is a modular monolith so standby stays light. There is no Windows service, no Session 0 component, no localhost command endpoint and no elevated helper.

```
                    ┌──────────────── VoxStride.Desktop (WPF) ────────────────┐
 Mic ─► AudioHub ─► │ ConversationCoordinator (single-reader actor)           │
 (System.Speech)    │   CommandBuffer ◄── TranscriptEvents ◄── Recognizer     │
 Hotkeys/Tray/UI ─► │   ControlPhraseDetector ─► explicit commit only         │
                    │        │ CommandEnvelope (immutable, hashed)            │
                    │        ▼                                                │
                    │ RunCoordinator ─► IntentRouter (local, no network)      │
                    │        │ direct recipe        │ cloud route             │
                    │        │                      ▼                         │
                    │        │        IReasoningProvider (Claude|Gemini|OpenAI)│
                    │        ▼                      │ ToolCallCompleted       │
                    │   RunAuthority ─► ToolBroker ◄┘ (complete calls only)   │
                    │        auth gen → schema → allowed → risk → validate    │
                    │        → approval (ApprovalWindow) → journal intent     │
                    │        → recheck → execute → verify → journal outcome   │
                    │        ▼                                                │
                    │   Tools: system · files · cleanup · apps · editor · ui · web
                    │        ▼                                                │
                    │   SqliteStore (journal, moves, history, rules, usage)   │
                    │   EventHub ─► Overlay / Main window / TTS               │
                    └─────────────────────────────────────────────────────────┘
```

### Projects

| Project | Responsibility | Platform |
|---|---|---|
| `VoxStride.Contracts` | Immutable records: `CommandEnvelope`, `ToolDefinition`, `ToolRequest/Result`, `Approval`, `TaskEvent`, `TranscriptEvent`, provider events, speech interfaces | any |
| `VoxStride.Core` | `RunAuthority`, `ToolBroker`, `PathPolicy`, `SchemaValidator`, journal interface, `IntentRouter`, `CommandBuffer`, `ControlPhraseDetector`, `ConversationCoordinator`, `RunCoordinator`, file organizer/cleanup engines, tool implementations | any |
| `VoxStride.Providers` | Claude Messages, OpenAI Responses and Gemini generateContent adapters over HTTPS/SSE | any |
| `VoxStride.Persistence` | SQLite store with migrations and constraints | any |
| `VoxStride.Windows` | P/Invoke: known folders, Credential Manager, Recycle Bin, app catalog/launcher, editor adapter, UI Automation worker (`WindowsUiAutomation`), window capture | Windows |
| `VoxStride.Speech` | `WaveInRecorder` (the one microphone device), System.Speech recognizer and wake grammar fed from `AudioHub`, cloud STT, TTS, earcons | Windows |
| `VoxStride.Browser` | Playwright controller over installed Edge in a dedicated profile (implements `IWebAutomation`) | Windows (Edge) |
| `extension/` | MV3 extension for the user's own Chrome/Edge, reached via native messaging → `VoxStride.exe` relay → per-user pipe → `ExtensionBrowser` (`IWebAutomation`) | Chrome/Edge |
| `VoxStride.Desktop` | Composition root, WPF UI, tray, hotkeys, approvals, smoke test | Windows |

Provider adapters have no OS access. Tools never see provider DTOs. Vendor-native computer-use tools are not enabled.

## Command lifecycle (spec §3, §14)

1. **Standby.** The wake grammar runs locally; there is no cloud connection and no audio is stored. The hotkey `Ctrl+Alt+V` also activates.
2. **Listening.** Revisioned transcript events go into `CommandBuffer`. Interim text replaces earlier interim text; finals are ordered by audio time. Pauses only finish segments. A reminder fires at 30 s; the draft is discarded (never executed) at 120 s.
3. **Finish.** A distinctive finish phrase ("VoxStride, execute"), the Proceed button, `Ctrl+Alt+V` while listening, or Enter for typed input. A finish phrase that is quoted, requested as content, negated or low-confidence becomes *uncertain*, and VoxStride asks "Ready to execute?".
4. **Finalizing.** The recognizer is stopped with a bounded flush. Audio after the finish point is excluded. A pending interim blocks commit and goes to Clarifying.
5. **Committed.** The transcript is frozen and SHA-256 hashed into a `CommandEnvelope`. `RunAuthority.Mint` refuses to mint if the hash does not match, and minting revokes older runs (one side-effecting run per desktop).
6. **Route.** `IntentRouter` either resolves every clause to a deterministic recipe (direct path, no network) or sends the request to the cloud. It never drops a clause; negations, corrections, quotes and unknown clauses go to the cloud.
7. **Execute.** Every tool call goes through `ToolBroker`. Moderate operations require a manifest-bound approval from `ApprovalWindow`, or a standing rule that covers only reversible same-root moves for one folder.
8. **Verify and report.** Tools re-observe state independently. The spoken summary comes from verified results; if a model claims success for an unverified step, the local evidence summary replaces the model's narrative.

## Latency architecture (spec §13)

- The direct path has no network, no LLM and no screenshot. `system_get_free_space` reads `DriveInfo` directly. CI asserts p95 < 500 ms over 100 runs (test `L01`).
- The installed-app catalog builds in the background at startup; disk queries never wait for it.
- One reusable `HttpClient` per provider (pooled connections); no idle polling.
- `TaskEvent` acknowledgement is published before any network await. A waiting notice is shown every 2 s while a provider is silent, and goes explicit at 5 s with cancel available.
- Instrumentation uses monotonic `Stopwatch` marks (`t_commit`, `t_route`, `t_ui_ack`, `t_provider_request`, `t_first_provider_delta`, `t_first_useful_action`, `t_first_verified_result`, `t_verified`). Timings are saved per run and shown in Diagnostics.

## Safety boundaries

See [threat-model.md](security/threat-model.md). Key invariants:

- The model is untrusted. It can only *request* registered tools with schema-valid arguments, within a run minted from a committed instruction.
- No tool executes shells, scripts, registry writes, arbitrary HTTP, process kills or elevation. The catalog excludes `cmd`, PowerShell, Windows Terminal, regedit, mmc, wsl and similar.
- File tools resolve their roots only from Windows known folders, never from model text. `PathPolicy` rejects traversal, reparse points, UNC paths, hidden/system items and protected locations, and every path is rechecked immediately before each move.
- Moves never overwrite. Every move is journaled (intent → moved/failed/unknown) and undoable. Undo never overwrites and skips edited files.

## Audio (ADR-0009)
One `AudioHub` owns the microphone. The wake detector, recognizers, Test speech and cloud STT take leases, and the device opens on the first lease and closes on the last. A 3 s pre-roll is replayed to the recognizer at the wake hand-over, so the first words aren't lost. Stalls and device errors reopen the device with backoff. `PcmPipeStream` feeds System.Speech with blocking reads (Length = -1) and is aborted before engine cancel, so Stop takes milliseconds. Mute closes the device. VoxStride never changes Windows sound or exclusive-mode settings. While TTS speaks, final transcripts are ignored (own-voice gating; this is not acoustic echo cancellation).

## Desktop control (ADR-0010)
`UiSession` (Core) owns policy: protected processes, blocked apps, password fields, irreversible-name approval, freshness, and per-window element ids that are never reused. `WindowsUiAutomation` runs managed UIA on a dedicated MTA worker with per-call timeouts and replaces the worker if a target hangs. Typing uses SendInput Unicode with a foreground and mouse guard. The vision fallback captures a single window with `PrintWindow`, after per-use approval, and sends it to the provider as an untrusted image. Coordinate clicks always need approval and a visibility check.

## Browser (ADR-0007, ADR-0011)
Both browser back-ends implement `IWebAutomation` and share `voxstride-page.js`, which tags visible interactive elements, extracts readable text and refuses password fields. `WebSession` picks the user's browser when the extension is connected and *Use my browser* is on; otherwise it uses VoxStride's own Edge window.
- **VoxStride's Edge window:** Playwright with a dedicated profile. YouTube cards are grouped into visual rows with ads and Shorts excluded, ambiguity returns choices, and playback is verified by video id. ChatGPT is draft-only.
- **User's own browser:** MV3 extension (`activeTab`, `scripting` and optional per-site host permissions). The native host `com.voxstride.bridge` is limited to the fixed extension id. The relay mode of `VoxStride.exe` forwards to the pipe `VoxStride.Bridge.<user>` (CurrentUserOnly). The app checks the caller origin and protocol version, the extension re-checks the tab origin, and messages are capped at 1 MB.
- Page content is untrusted. Text that looks like an injected instruction is flagged, and submits or irreversible clicks need approval.

## Live companion (ADR-0013)
```
Mic ─► AudioHub (auto-gain) ─► local VAD + pre-roll ─► GeminiLiveTransport (WebSocket) ◄─► Gemini Live
                                                            │  audio ▼            ▲ tool responses / frames
                                               WaveOutStream (barge-in)   LiveCompanion ─► ToolBroker ─► tools
                                                            │                    ▲
                                               CompanionWindow (character, bubble, bar)   LiveApproval (spoken yes / click)
Twitch chat ─► TwitchCoHost ─┐
Screen loop (opt-in) ────────┴─► SendContextAsync (marked "not the user", never authorizes)
```
- `LiveCompanion` mints a `RunAuthorization` per tool call. It is bound to the transcription of the user's own words in that turn. With no user words, only read-only tools are allowed.
- `LiveApproval` approves reversible requests automatically. Irreversible ones ask; the question is shown and spoken, and the answer comes from the user's transcribed voice or a click.
- `CompanionWindow` draws one of six original characters, or the user's picture. Its states are listening, hearing, thinking, working, talking, asking and muted. Animation runs on a timer: 25 fps while busy and about 7 fps at rest, and values snap once settled, so an idle character costs about 0.3 % CPU.
- Without a Gemini key, the same window mirrors the classic `ConversationCoordinator` (cloud, local or Ollama AI).
- Tools added for the companion: `files_create_document` (with its own xlsx writer), `screen_look`, `memory_remember` and `memory_forget`.

## Natural mode (ADR-0012)
An opt-in pause-ended turn is `InputMode.VoiceNatural`, and its run is minted without mutating tools. "VoxStride, execute" within 90 s confirms it as an explicit commit.

## Data

SQLite at `%LocalAppData%\VoxStride\voxstride.db` (WAL) holds these tables: `ToolEvents`, `FileMoveManifest` (with a status CHECK constraint), `RollbackRecords`, `Commands`, `PlanSteps`, `UsageEvents` and `PermissionRules`. The transcript is stored only if the user enables retention. Raw audio, screenshots, provider prompts and keys are never stored. API keys are kept in Windows Credential Manager (`VoxStride/provider/<name>`).

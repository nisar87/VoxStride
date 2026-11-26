# VoxStride architecture

## Process and component view

VoxStride is a single per-user process (`VoxStride.exe`, WPF, .NET 10, `asInvoker`). It is a modular monolith so standby stays light. There is no Windows service, no Session 0 component, no localhost command endpoint and no elevated helper.

```
                    ┌──────────────── VoxStride.Desktop (WPF) ────────────────┐
 Mic ─► WakeWord ─► │ ConversationCoordinator (single-reader actor)           │
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
                    │   Tools: system · files · cleanup · apps · editor · browser
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
| `VoxStride.Windows` | P/Invoke: known folders, Credential Manager, Recycle Bin, app catalog/launcher, editor adapter | Windows |
| `VoxStride.Speech` | System.Speech recognizer, wake-phrase grammar, TTS, earcons | Windows |
| `VoxStride.Browser` | Playwright controller over installed Edge in a dedicated profile | Windows (Edge) |
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

## Browser

- **Implemented (P5a):** visible Playwright-driven Microsoft Edge in a dedicated profile. YouTube cards are grouped into visual rows with ads and Shorts excluded. If a position is ambiguous, VoxStride returns choices instead of guessing. Playback is verified by video id and `video.paused/currentTime`. ChatGPT is draft-only; Enter is never pressed and nothing is submitted.
- **Designed, not yet implemented (P5b):** an MV3 extension for the user's existing Chrome/Edge. It would use the `activeTab` permission, a native messaging host restricted to the extension ID, origin, tab and frame validation, and run-token binding, and would never read cookies, storage or credentials.

## Data

SQLite at `%LocalAppData%\VoxStride\voxstride.db` (WAL) holds these tables: `ToolEvents`, `FileMoveManifest` (with a status CHECK constraint), `RollbackRecords`, `Commands`, `PlanSteps`, `UsageEvents` and `PermissionRules`. The transcript is stored only if the user enables retention. Raw audio, screenshots, provider prompts and keys are never stored. API keys are kept in Windows Credential Manager (`VoxStride/provider/<name>`).

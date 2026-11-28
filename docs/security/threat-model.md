# VoxStride threat model

Scope: a per-user Windows desktop agent that executes local actions requested by voice, typed text or a cloud model. Method: STRIDE over trust boundaries. Each mitigation links to the test that enforces it.

## Assets
User files in allowed folders, temp files, API keys, browser sessions (VoxStride profile and the user's own browser via the extension), other apps' windows and their content, history/journal/diagnostics log, the microphone stream.

## Trust boundaries
1. Microphone/ambient audio → recognizer (untrusted input; wake/finish phrases are not authentication).
2. Cloud model output → tool broker (untrusted; may be prompt-injected via pages, files and window titles).
3. Web pages / documents / UI text → observations (untrusted data, never instructions).
4. Local UI (approval window, hotkeys) → trusted user intent.
5. Process boundary to other apps (UIPI, elevated windows, secure desktop).
6. Browser extension ↔ native host relay ↔ per-user named pipe ↔ VoxStride (IPC; the extension's page data is untrusted).
7. Window screenshots → provider (untrusted image; per-use consent).

## Threats and mitigations

| # | Threat | Mitigation | Evidence |
|---|---|---|---|
| T1 | Model asks for tools outside the request (e.g. shell, upload) | Closed registry, no shell/script/registry/HTTP tools; unknown tools blocked and journaled redacted | `BrokerSecurityTests.UnknownOrArbitraryToolsAreBlocked`, `RunCoordinatorTests.B03_*` |
| T2 | Schema tampering / extra fields / path injection | Strict local JSON-schema validation (`additionalProperties:false`, patterns, ranges, depth limit) | `BrokerSecurityTests.UnknownFieldsAndBadPatternsAreRejected`, `SchemaValidatorTests` |
| T3 | Forged/replayed authorization | Unguessable run token held only locally, generation counter, expiry, transcript hash; new command revokes old runs | `RevokedRunIsBlocked`, `OlderRunBecomesStale…`, `ExpiredAuthorizationIsBlocked`, `TamperedTranscriptCannotBeMinted` |
| T4 | Forged approval / approval for a different manifest | Approvals only from `ApprovalWindow` click; bound to run + manifest digest + operation + expiry; recheck after wait | `ApprovalForDifferentManifestIsRejected`, `ApprovalForOtherRunIsRejected`, `CancelWhileAwaitingApprovalPreventsEffect` |
| T5 | Duplicate/replayed side effect after timeout | Idempotency key per run; timeouts after dispatch → `Unknown`, reconciled not retried | `ReplayedSideEffectIsNotRepeated` |
| T6 | Path escape (traversal, junction/symlink, UNC, sibling prefix) | `PathPolicy` canonicalization + reparse walk + protected locations; revalidated before each move | `PathPolicyRejectsEscapes…`, `PathPolicyRejectsSiblingPrefixRoot`, `F02_SymlinkFilesAreSkipped…` |
| T7 | TOCTOU between preview and move | Per-item length/mtime/reparse recheck; destination existence recheck; never overwrite | `F02_SourceChangedAfterPreviewIsSkipped`, `F02_DestinationAppearingAfterPreviewIsNotOverwritten` |
| T8 | Data loss on undo | Undo only unchanged files to vacant locations; conflicts reported | `F04_UndoReportsConflictsAndNeverOverwrites` |
| T9 | Premature execution from pauses, silence, timeouts | Explicit commit only; 240-trial randomized gate | `CoordinatorTests.Gate_240ScriptedTrials_ZeroPrematureCommits`, `V01_*`, `ReminderAt30sAndDiscardAt120s…` |
| T10 | Ambient/TTS/video audio says "execute" or approves | Distinctive prefixed finish phrase; quoted/requested/negated → uncertain; approvals are never voice-driven (click only, not default button, 600 ms arming delay) | `V02_*`, `RequestedAsContentOrNegated…`; ApprovalWindow design. Physical playback test **pending** (V06) |
| T11 | Text typed into the wrong window | Editor adapter: no synthetic keystrokes, WM_SETTEXT to the exact verified HWND (process id + start time), new empty document only; generic UI typing: see T18 | `NotepadWriteIsVerifiedByReadBack` (Windows CI) |
| T12 | Prompt injection from web pages, UI text or screenshots | Observations are wrapped as untrusted data; injection-looking text is flagged; submits and irreversible clicks/presses need click approval; no upload/download tools; draft-only ChatGPT; natural turns are read-only | `B03`, `WebToolsTests.LooksLikeInjection*`, fixture injection page in `BrowserTests`, `UiToolsTests` |
| T13 | Secret leakage | Keys in Credential Manager; never in prompts/logs/journal; redaction of key-like argument names; every log line and exception passes `Log.Redact` (Anthropic/OpenAI/Google keys, bearer tokens, key query params, secret fields); HTTP logging records host+path only; secret scan in CI | `BlockedOperationsAreJournaledRedacted`, `Claude_Continuation…DoesNotContain key`, `LogTests.RedactsSecrets`, `HttpLogNeverContainsQueryKeys`, CI secret-scan job |
| T13a | Personal data in diagnostics | Log is local only, never uploaded; user words/replies only while the Privacy toggle is on (default on, owner request 2026-11-27); standby (wake) audio text never logged; no audio ever | `ContentIsHiddenWhenTheUserTurnsItOff` |
| T14 | Vulnerable dependencies | `dotnet list package --vulnerable` gate in CI; transitive pins | CI (caught GHSA-2m69-gcr7-jv3q; pinned 2.1.13) |
| T15 | Privilege escalation / UIPI bypass | `asInvoker` manifest; no elevated helper; elevated targets reported for human handoff | app.manifest; editor adapter refuses hung/inaccessible windows |
| T16 | Lock screen / sleep while working | Session lock and suspend stop listening and cancel runs; no auto-resume | `V05_LockAndMuteDiscardDraft`; App `SessionSwitch` wiring |
| T17 | Idle audio upload | Standby uses only local grammar recognition; no network client exists in the voice path | Architecture; provider calls only from `RunCoordinator` after commit |
| T18 | Wrong-window or covert UI input | Window identity = hwnd+pid+start time; target must be foreground; typing aborts on focus change or mouse movement; coordinate clicks need approval and a `WindowFromPoint` root check; protected processes (UAC, LogonUI, credential UI, VoxStride) and blocked apps are refused; password fields refused | `UiToolsTests`, `UiAutomationTests` (Windows CI) |
| T19 | Hung or malicious target app freezes the agent | UIA on a dedicated MTA worker with per-call timeouts; hung worker abandoned and replaced; snapshot size/depth/time bounds | `UiAutomationTests.HungAppTimesOutWithoutBlockingVoxStride` |
| T20 | Covert screen capture | Single-window `PrintWindow` only, never the whole screen; per-use approval or *Never*; never stored; sent once as an untrusted image | `UiToolsTests` capture policy, `ProviderImageTests` |
| T21 | Another program impersonates the extension or the app over IPC | Native host registered under HKCU with `allowed_origins` = fixed extension id; relay only forwards; pipe is `CurrentUserOnly`; app requires the allowlisted origin + protocol version; 1 MB message cap; request ids and timeouts | `ExtensionBridgeTests` (wrong origin/version refused, oversized frame), installer host checks |
| T22 | Extension over-reach in the user's browser | MV3 with `activeTab` + optional per-site host permissions granted by the user; acts on the active tab only; tab origin re-checked per request; no cookies/storage/password access; badge shows activity; blocked sites refused in the app | `ExtensionEndToEndTests` (origin mismatch refused), manifest review |
| T23 | Tampered update | Updates only from the GitHub Releases API over HTTPS; installer SHA-256 must match `SHA256SUMS.txt`; per-user install, no elevation | `UpdateLogic` tests (`PrereleaseAndForeignHostsAreIgnored`, `ChecksumLookupMatchesFileName`); residual: unsigned until a certificate is approved |
| T24 | The model acts without the user asking (live mode) | A tool call is authorized only by the user's own transcribed or typed words in the current turn. After the turn ends, or with context only, mutating tools are refused (`not_requested`). | `LiveCompanionTests.WithoutTheUsersWords…`, `AfterTheTurnEnds…`, self-test `no-words-no-changes` |
| T25 | Twitch viewers or on-screen text steer the companion | Chat, events and screen frames go in through `SendContextAsync`, marked "not the user", and close the turn's authorization. Bots and commands are dropped, links are removed and text is capped. Chat is read anonymously and never posted to. | `TwitchCoHostTests`, `LiveCompanionContextTests.ContextFromViewers…`, self-test `twitch-viewers-cannot-act` |
| T26 | Irreversible action without consent in live mode | `LiveApproval` asks every time for recycle/delete, cleanup, Send/Pay/Submit, form submits and coordinate clicks. Only the user's voice ("yes"/"no") or a click answers. There's a 2-minute timeout and the default is no. | `IrreversibleActionWaitsForASpokenYes`, `SayingNoCancels…`, self-test `asks-before-deleting` |
| T27 | Screen content leaves the PC | Policy is Ask each time (default), Whenever needed, or Never. Captures are single frames, never saved, and treated as untrusted. Screen comments are opt-in and need "Whenever needed". | `ScreenLookPolicyTests`, `ScreenCommentsSendAChangedScreen…` |

## Residual risks (open)
- Wake-phrase false accepts and recognition accuracy on real microphones are unmeasured (V06).
- Windows 11 Notepad session-restore can open a non-empty tab, in which case insertion is refused rather than overwriting. This needs physical verification.
- Injection detection is heuristic. The real defence is that page text can't authorize anything: every effect still needs a committed instruction, an allowed tool and, for anything irreversible, a click.
- The extension is installed unpacked until store listings are approved (paid accounts). Developer-mode extensions trigger a browser warning banner.
- Own-voice gating is not acoustic echo cancellation; loud speakers can still be heard between replies. In live speakers mode, barge-in needs the user to be 3× louder than the learned echo level.
- Live audio and any screen frames go to Google under the user's own Gemini key. Ollama keeps everything on the PC.
- The installer is unsigned until the owner provides a certificate.
- No independent third-party security review has been performed.

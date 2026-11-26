# VoxStride threat model

Scope: a per-user Windows desktop agent that executes local actions requested by voice, typed text or a cloud model. Method: STRIDE over trust boundaries. Each mitigation links to the test that enforces it.

## Assets
User files (Desktop/Documents/Downloads), temp files, API keys, browser sessions in the VoxStride profile, history/journal, the microphone stream.

## Trust boundaries
1. Microphone/ambient audio → recognizer (untrusted input; wake/finish phrases are not authentication).
2. Cloud model output → tool broker (untrusted; may be prompt-injected via pages, files and window titles).
3. Web pages / documents / UI text → observations (untrusted data, never instructions).
4. Local UI (approval window, hotkeys) → trusted user intent.
5. Process boundary to other apps (UIPI, elevated windows, secure desktop).

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
| T11 | Text typed into the wrong window | No synthetic keystrokes; WM_SETTEXT to exact verified HWND (process id + start time); new empty document only | `NotepadWriteIsVerifiedByReadBack` (Windows CI) |
| T12 | Prompt injection from web pages | Pages are observations; browser tools return only titles/hrefs/geometry; no submit/upload tools; draft-only ChatGPT | design + `B03` broker tests; live injection fixtures **pending** |
| T13 | Secret leakage | Keys in Credential Manager; never in prompts/logs/journal; redaction of key-like argument names; secret scan in CI | `BlockedOperationsAreJournaledRedacted`, `Claude_Continuation…DoesNotContain key`, CI secret-scan job |
| T14 | Vulnerable dependencies | `dotnet list package --vulnerable` gate in CI; transitive pins | CI (caught GHSA-2m69-gcr7-jv3q; pinned 2.1.13) |
| T15 | Privilege escalation / UIPI bypass | `asInvoker` manifest; no elevated helper; elevated targets reported for human handoff | app.manifest; editor adapter refuses hung/inaccessible windows |
| T16 | Lock screen / sleep while working | Session lock and suspend stop listening and cancel runs; no auto-resume | `V05_LockAndMuteDiscardDraft`; App `SessionSwitch` wiring |
| T17 | Idle audio upload | Standby uses only local grammar recognition; no network client exists in the voice path | Architecture; provider calls only from `RunCoordinator` after commit |

## Residual risks (open)
- Wake-phrase false accepts and recognition accuracy on real microphones are unmeasured (V06).
- Windows 11 Notepad session-restore can open a non-empty tab, in which case insertion is refused rather than overwriting. This needs physical verification.
- Live web prompt-injection fixtures and the existing-browser extension (P5b) are not implemented.
- The installer is unsigned until the owner provides a certificate.
- No independent third-party security review has been performed.

# Acceptance tests and evidence

Test IDs follow spec §17. **Automated** tests run on every push in GitHub Actions: Linux runs the core and persistence suites, and `windows-latest` runs all suites plus the Windows integration tests, then builds the installer and runs the install → smoke → uninstall check. **Manual** tests need a physical Windows 11 PC with a microphone or live API keys; their scripts are below. A gate is "verified" only when its evidence exists.

## Automated mapping

| ID | Test(s) | Where |
|---|---|---|
| V01 | `CoordinatorTests.V01_PausesNeverCommit` (1/5/15/30/90 s), `Gate_240ScriptedTrials_ZeroPrematureCommits` | Core (Linux + Windows) |
| V02 | `V02_QuotedProceedDoesNotCommit`, `ControlPhraseTests.ContentMentioningControlWordsIsNotAFinish`, `RequestedAsContentOrNegatedIsUncertainNeverFinish` | Core |
| V03 | `V03_CorrectionIsPreservedVerbatimForPlanning`, `RouterTests.AnythingNotFullyUnderstoodGoesToCloud` (correction → cloud) | Core |
| V04 | `CommandBufferTests.V04_RevisionsReplaceAndOutOfOrderFinalsAreSortedByTime`, `LateFinalBeforeFinishIsIncluded_AudioAfterFinishIsNot`, `PendingInterimAfterFlushBlocksCommit` | Core |
| V05 | `V05_RecognizerFailureKeepsDraftAndNeverCommits`, `V05_LockAndMuteDiscardDraft`, `ReminderAt30sAndDiscardAt120sWithoutExecution` | Core |
| V06 | (physical only — see M-V06) | — |
| V07 | Claude-only/no-second-key: speech path is local `System.Speech`; `NoProviderConfiguredExplainsWhatStillWorks` | Core + design |
| L01 | `RunCoordinatorTests.L01_DirectDiskReadMakesNoProviderCallAndIsFast` (100 runs, p95 < 500 ms, zero provider calls) | Core |
| L02 | `ProviderContractTests.L02_TruncatedStreamYieldsNoToolCall` (×3 providers), `MalformedToolArgumentsAreRejectedNotExecuted` | Core |
| L03 | `L03_SlowProviderShowsWaitingStatusAndCancelPreventsLateActions` | Core |
| L04 | `FileToolTests.L04_CancelMidBatchStopsNewMoves` | Core |
| F01–F05 | `FileToolTests.F01…F05`, `ExclusionsByCategoryAndExtensionAreHonored`, `UndoRestores…`, `PathPolicy…` | Core |
| U01/U02 | `WindowsIntegrationTests.NotepadWriteIsVerifiedByReadBack` (exact-HWND write, read-back) | Windows CI |
| U03 | Design: UIPI refusal reported; hung window → `SendMessageTimeout` abort (manual M-U03) | — |
| B01 | `VideoLayoutTests.*` (ads/Shorts excluded, rows, ambiguity) | Core |
| B02 | `ChatGptDraftTool` is draft-only by construction (no submit tool exists) | Core |
| B03 | `RunCoordinatorTests.B03_InjectedInstructionsInToolOutputCannotUseToolsOutsideRegistry`, broker tests | Core |
| P01 | `ProviderContractTests.P01_AllProvidersYieldTheSameCanonicalToolCall` + continuation tests per provider | Core |
| P02 | `P02_HttpErrorsMapToActionableKinds`, `P02_ProviderErrorGivesActionableMessageAndDirectToolsStillWork` | Core |
| S01 | `BrokerSecurityTests.*` | Core |
| S02 | `SqliteStoreTests.HistoryOmitsTranscriptUnlessRetentionEnabled`, `BlockedOperationsAreJournaledRedacted`, secret-scan job | Persistence + CI |
| INST-01 | `scripts/Test-Installer.ps1` (silent per-user install, smoke test from installed exe, uninstall, data removal) | Windows CI |

## Manual / physical test scripts (pending execution)

Run them on a dedicated Windows 11 test account, using synthetic files only. Record results in `docs/benchmarks/<date>-<pc>.json` using the evidence template from spec §17.

- **M-V01 (200 trials):** say "Hey VoxStride", then speak 1–3 sentences separated by pauses of 1, 5, 15 and 30 s. Do not say the finish phrase. Pass: nothing commits, and the transcript shows every sentence. Then say "VoxStride, execute". Pass: exactly one commit containing all sentences.
- **M-V06:** play a video and a TTS clip that say "Hey VoxStride" and "VoxStride execute" through the speakers for 8 hours of mixed audio. Record false wakes per hour (target < 1 per 8 h) and confirm no approval was ever granted.
- **M-WAKE latency:** a harness records `t_wake_end` (audio ground truth) against the cue onset. Target p95 ≤ 400 ms.
- **M-U03:** run Notepad as administrator and ask VoxStride to write into Notepad. Pass: refusal with an explanation and no text inserted.
- **M-NPP:** with Notepad++ installed, run "write a two-sentence note about VoxStride in Notepad++". Pass: a new instance with one new document, text verified, and existing tabs untouched.
- **M-B01 (live):** run "search YouTube for lofi beats and play the third video in the top row". Pass: the URL's video id matches the selected card and playback is verified, or choices are offered.
- **M-B02 (live):** run "open ChatGPT and draft a prompt about meal planning". Pass: text appears in the input and nothing is sent.

### Live provider smoke tests
With a test key and a small budget, set the provider in Settings, then type each of these:
1. "roughly how roomy is my main disk, in plain words" → cloud path calls `system_get_free_space`, and a verified result is spoken.
2. "preview organizing my desktop by type, but don't move anything yet" → preview only. No approval is requested and no files move.
3. "write a haiku about rain in Notepad" → `editor_write_new_document` with verified read-back.
4. "open youtube.com" → `browser_open_url` verified.

Record the provider, model, p50/p95 of `t_first_useful_action` and token usage shown in History. Repeat for Claude, Gemini and OpenAI.

## Latency budgets (spec §13.3) and how they're measured
Every run stores timings in History (Diagnostics shows the last run): `t_ui_ack`, `t_route`, `t_provider_request`, `t_first_provider_delta`, `t_first_useful_action`, `t_first_verified_result` and `t_verified`, all relative to `t_commit`. The CI assertion covers direct dispatch only (L01). Cloud p50/p95 must be measured live per provider.

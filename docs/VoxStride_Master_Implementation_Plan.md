# VoxStride
## Master implementation specification and research-backed engineering plan
**Version 2.0 | 26 November 2026 | Claude Code engineering handoff**

**Target repository:** https://github.com/nisar87/VoxStride  
**Product:** VoxStride — Speak. Command. Accomplish.  
**Executable:** VoxStride.exe  
**Proposed wake phrase:** “Hey VoxStride” (subject to keyword recognition and redistribution validation).  
**Platform:** Windows 11 x64 initially.  
**Authority:** This document supersedes the earlier Windows AI Assistant specification. Treat all MUST requirements and release gates as binding unless the owner explicitly approves a documented change. A feature is not finished merely because its UI exists.

### Owner priorities (in order)
1. Capture the entire instruction, including long pauses, corrections and exclusions; only execute after an explicit commit by default.
2. Immediate, meaningful, honest feedback and low time-to-first-useful-action. No silent ten-second planning period followed by surprising desktop changes.
3. Complete useful real Windows tasks, not just chat or demonstrations. Prefer deterministic local operations over model-generated click sequences.
4. Claude, Gemini and OpenAI as interchangeable BYOK reasoning providers; no large local AI, model training, GPU or mandatory server.
5. A premium, simple, interactive native application that remains lightweight while on standby, respects the user's desktop and offers visible, interruptible execution.

**Read order for Claude:** Sections 1–12 are the product contract; Sections 13–18 are the concrete implementation sequence, latency architecture, integration contracts and acceptance gates. Use the final checklist as the definition of done. The repository was publicly reachable and empty at research time; recheck current state before writing files. No repository change is authorized merely by this document.

### Executive decision
Build a native, voice-first Windows personal computer agent that listens on demand, captures a complete instruction, reasons through it with a selected cloud model, operates the user's computer, verifies the result and reports back naturally. This is a viable engineering project, not a promise of unrestricted or infallible autonomy.

**Recommended foundation:** C# / .NET 10 LTS, WPF, a lightweight per-user background host, provider-independent tool contracts, typed Windows automation, browser integration, and an explicit authorization boundary. .NET 10 is an active LTS release [1]. Windows 11 x64 is the initial supported platform. Support for older Windows versions and ARM64 should follow measured compatibility work.

**Non-negotiable:** Claude, Gemini and OpenAI are all first-class reasoning providers. No local large language model, GPU requirement, fine-tuning, custom foundation-model training, paid server or mandatory cloud account beyond the selected provider API credentials is required for the MVP. A tiny local wake-word engine can use a pretrained keyword; a custom branded wake word may require a vendor-generated keyword model or separate keyword training, not training a general AI model.

### Success definition
- User says the wake phrase, speaks naturally across several pauses, then says “Proceed”; nothing executes before explicit completion.
- Assistant understands all constraints and corrections, asks only essential questions, performs authorized work and verifies outcomes independently.
- Routine actions feel immediate; complex actions show live progress, permit interruption, and end in an accurate success, partial-success or failure report.
- Every provider can perform the same core Windows tasks via the same permission and tool execution layer.
- The app remains lightweight while idle, with no continuous audio upload, no undisclosed background actions and no elevated agent by default.

### Document map
1. Research and competitors; 2. scope and workflows; 3. conversation protocol; 4. architecture; 5. provider and voice integrations; 6. Windows/browser execution; 7. agent runtime; 8. security/privacy; 9. UX/persistence; 10. delivery roadmap; 11. test gates; 12. Claude instructions; 13. latency-first architecture; 14. precise voice protocol; 15. implementable interfaces and execution recipes; 16. phased engineering tickets; 17. reproducible tests and release gates; 18. final delivery checklist; references.

---
# 1. Research and competitive position
Public documentation and repositories establish that the building blocks already exist. These sources validate technical capabilities, not independently measured reliability or a complete market survey.

| Reference product | Established approach | Product opportunity |
|---|---|---|
| Microsoft UFO² [2] | Windows UIA, Win32, COM, application-aware agents and hybrid execution | Native consumer voice UX, strict turn completion, controlled actions and undo |
| Windows Voice Access [3] | Voice navigation and dictation, including offline use after setup | Goal-driven planning and multi-application work, not just discrete commands |
| Talon [4] | Hands-free commands, dictation and programmable input | Natural multi-step instructions without learning command grammar |
| Open Interpreter [5] | Agent and model-driven code execution; evolving open-source project | Typed, restricted Windows tools and consumer-grade control rather than unrestricted scripts |
| Claude computer use [6] | Client executes model-requested screenshot, keyboard and mouse actions | Use visual fallback inside a safer local Windows broker |
| OpenAI computer use [7] | Observation/action loop and application-side confirmation guidance | Shared Windows execution independent of model-specific tooling |
| Gemini Live [8] | Low-latency audio/vision, interruption and function calls | Provider-neutral conversation and explicit completion gate |
| Astra Assistant [20] | Public README describes C#/.NET 10/WinUI 3, multi-provider agent, local speech and verification | Closest comparison: test standby resources, explicit finish, latency, safety and browser ergonomics; do not assume gaps without testing |
| Windows Agent Arena [21] | Reproducible Windows task benchmark | Use representative task fixtures and verified outcomes, not subjective demos |
| Sentinel AI, Ari VoiceCommand, JARVIS-6 [22–24] | Public repository descriptions suggest speech streaming, local command routing and browser/agent interactions | Evaluate real latency and completeness in disposable VM; treat untested marketing claims as hypotheses |

### Build versus reuse
Use UFO² as an architectural benchmark, not a dependency that must be adopted. Prototype integration with it in a disposable Windows VM, measure setup burden, latency, provider compatibility, security, licensing and packaging. Compare against a small C# native implementation. Prefer the native core if it meets the same benchmarks with fewer dependencies. UFO is a rapidly evolving research stack (current repository also describes UFO³); pin the exact revision evaluated rather than making assumptions about its API. For Astra and smaller projects, inspect source only to learn architectural ideas unless a compatible license expressly permits reuse. If no license is present, do not copy implementation or assets. Produce a competitor matrix with tested workflows, setup time, licenses, voice turn behavior, actual p50/p95 latency, limitations and whether each claim is documented, observed or unverified. Do not run unknown binaries on the owner's real computer. Do not silently copy code or redistribute model weights: inspect repository, dependency and model-specific licenses. Keep optional research components out of the default installer.

### Product differentiation hypotheses
The differentiator is the combination of wake-word activation, a reliable uninterrupted command buffer, three interchangeable providers, hybrid semantic/visual automation, clear task previews, reversible actions, and a premium voice-first desktop experience. Competitor feature gaps must be validated through hands-on trials before any marketing claim. Do not claim that this is the first product of its kind.

### Feasibility boundaries
Native file and OS queries are straightforward. Supported desktop and browser workflows are feasible with meaningful integration and verification. General GUI interaction is probabilistic, affected by custom controls, layout, localization and application updates. Protected desktops, UAC, lock screens, captchas, DRM, anti-bot restrictions and inaccessible surfaces require human handoff or supported APIs. Do not try to bypass them. Network connectivity and API limits constrain cloud reasoning.

---
# 2. Product scope and capabilities
### Priority capabilities
| Domain | MVP / initial supported actions | Subsequent expansion |
|---|---|---|
| Windows | Launch/focus apps; list drives and free space; locate files; safe user-temp cleanup | System diagnostics, approved settings workflows |
| Files | List, search, classify by extension, create folders, copy, move, rename, recycle, undo | Document-content classification, duplicate review, controlled batch recipes |
| Text | Generate text, open Notepad and Notepad++, type into verified editor | Word, Excel, PowerPoint and app-specific connectors |
| Web | Open URL, search, navigate DOM, choose visible YouTube video, draft prompt on ChatGPT | Extension for user's browser, more site adapters |
| Conversation | Wake word, continuous capture, explicit finish, cancel, clarification, speech feedback | Natural turn-taking, multilingual and configurable voices |
| Providers | All three API adapters, model selection, capability detection, usage tracking | Advanced provider routing, approved reusable skills |

### Example workflows and required verification
**Desktop organization:** Resolve the Windows Desktop known folder (including redirected/OneDrive paths). Enumerate top-level files; classify by extension by default. Skip existing folders, shortcuts and hidden/system items unless explicitly authorized. Show the source/destination manifest; ask for confirmation on first use or whenever the move falls outside an explicitly configured reversible organization policy. A committed command plus a matching local policy may authorize ordinary same-root moves without a second prompt. Never overwrite; create folders, move eligible files, verify destination and absence at source, record reversible moves, and report count/skips. Undo must detect later edits or collisions.

**C drive cleanup:** Read actual drive capacity/free bytes. Enumerate only allowed temporary roots, measure eligible bytes, show preview and obtain approval before deletion/quarantine. A quarantined file on the same volume does not reclaim disk space; never describe it as freed until actual disk space increases. Exclude active files, reparse points, Downloads, system directories and unrelated user data. Do not forcibly unlock files. Prefer recoverable quarantine where appropriate. Report measured before/after space and inaccessible or skipped items; do not claim estimated bytes as reclaimed bytes.

**Notepad++ writing:** Resolve the installed application or ask user to select executable. Generate requested text, create or open the intended tab, verify the process and document identity, insert via safe app adapter/UIA or Unicode input, then read back where supported. Do not replace existing unsaved text unless requested and approved.

**ChatGPT prompt:** Distinguish opening a site and drafting a prompt from submitting it. Show draft, use the existing authenticated session only with permission, and submit only when explicitly authorized. User handles login/MFA. Never extract session cookies or passwords.

**YouTube:** Search through permitted browser interaction. Interpret “third video in the top row” using current rendered cards and their visible positions, exclude ads unless requested, confirm if responsive layout makes ranking ambiguous, then open/play the selected video and verify URL/title/player state. Autoplay restrictions may require user action.

---
# 3. Voice interaction and complete-command protocol
### State machine
STANDBY → WAKE DETECTED → LISTENING → COMMITTED → PLANNING → EXECUTING → VERIFYING → SPEAKING → FOLLOW-UP / STANDBY.

Additional states: MUTED, WAITING FOR CLARIFICATION, WAITING FOR APPROVAL, PAUSED, CANCELLED, FAILED. State transitions are owned by the local coordinator, not an LLM. Only an authorized committed command may create an executable plan.

### Explicit completion mode (default)
- Wake word: configurable supported keyword; select a distinctive pretrained phrase initially. Show tray/listening state and play a brief cue. Maintain a small in-memory pre-roll buffer to avoid clipping the beginning; discard standby audio unless activation occurs.
- Capture and stream transcription segments into a revision-aware command buffer. Voice activity detection marks segments; it must NEVER commit the user's request just because they paused. Show interim and final words separately. Respect long pauses and follow-up corrections.
- Use a distinctive end command, e.g. “Assistant, proceed,” “Execute now,” or a keyboard/button action. Recognize it as a standalone control phrase, not quoted content or words inside dictation. Ambiguous detection must not execute. Freeze the final transcript only after pending segments are finalized. Remove the control phrase from task content.
- Use a configurable inactivity reminder (e.g., 30 seconds) without execution. After a longer configurable idle limit, discard the uncommitted session with notice. Never interpret timeout, reconnect or silence as approval.
- Allow “cancel,” “stop listening,” “undo last sentence,” and “replace that with…” while capturing. Preserve original task constraints and explicit revisions. Display the normalized instruction and ask clarification only when material ambiguity remains.

### Natural mode and push-to-talk
Natural conversation mode may use semantic end-of-turn signals (OpenAI) or Live audio activity handling (Gemini), but remains opt-in; write actions still require a completed user turn and risk checks. Push-to-talk commits on release or an explicit hotkey, configurable. Keyboard and typed commands must work without a microphone.

### Interaction during work
When speaking, stop audio playback promptly if the user interrupts. In executing state, “stop” cancels future actions; it does not falsely claim to reverse completed ones. “Pause” prevents new side effects. Task corrections invalidate the pending plan and its approval. Safety-critical confirmation is visual or explicit local user input; speaker identity and wake word are not authentication. Echo cancellation, noise filtering, source switching, audio-device recovery, session IDs and transcript deduplication are required. Suppress wake/finish detection from assistant TTS and test playback from videos; do not treat ambient speech as a valid approval.

---
# 4. Reference architecture and technology stack
### Process boundaries
**Desktop UI + tray** (WPF, MVVM) → **Conversation coordinator** → **Agent orchestrator** → **Provider adapters** (Claude / Gemini / OpenAI over HTTPS or supported streaming transport).

**Agent orchestrator** → **Local policy engine** → **Tool broker** → **Native Windows tools / UIA worker / browser bridge / visual fallback** → **Result verifier and journal** → coordinator → speech output and UI.

Use a signed per-user host, not a Session 0 Windows service for UI/mic automation. A separate same-user automation worker isolates hanging UIA/COM calls and can be restarted. IPC uses authenticated named pipes restricted to the current user, request schemas, size limits and correlation IDs; never an unauthenticated localhost command endpoint. If later required, build a separate minimal elevated helper with narrow audited verbs and explicit UAC; no generic elevated AI.

| Area | Recommended baseline |
|---|---|
| Runtime / UI | .NET 10 LTS, C#, WPF, MVVM, Generic Host, dependency injection |
| Audio | WASAPI via a maintained audio library; cancellable bounded channels; optional pretrained keyword engine |
| Windows | System.IO, DriveInfo, Known Folders, Win32 APIs, UI Automation via verified maintained library or COM on a dedicated MTA worker; separate STA worker only for APIs that require STA (for example certain Office COM calls) |
| Browser | Playwright .NET, isolated persistent profile initially; later Manifest V3 extension + native messaging |
| AI | Official SDKs where mature; otherwise typed HttpClient/WebSocket adapters against current APIs |
| Persistence | SQLite for journal/preferences and task records, Windows Credential Manager or DPAPI for API keys |
| Quality | xUnit, structured redacted logging, GitHub Actions Windows builds, isolated VM UI and voice tests |
| Distribution | Signed per-user installer (WiX/MSI or equivalent); evaluate MSIX constraints separately; signed updates |

### Suggested solution structure
`VoxStride.Desktop` (UI/tray); `VoxStride.Host` (lifecycle, IPC); `VoxStride.Contracts` (immutable command/tool schemas); `VoxStride.Voice` (audio, STT, TTS, wake); `VoxStride.Agent` (planner, orchestration); `VoxStride.Providers.Claude`, `.Gemini`, `.OpenAI`; `VoxStride.Policy`; `VoxStride.Tools.Windows`; `VoxStride.Tools.UIA`; `VoxStride.Tools.Browser`; `VoxStride.Persistence`; `VoxStride.Tests.Unit`, `.Contract`, `.Integration`, `.EndToEnd`; `browser-extension/`; `docs/`; `installer/`.

Keep vendor DTOs outside domain interfaces. Define `IReasoningProvider`, `ISpeechRecognizer`, `ISpeechSynthesizer`, `IWakeWordDetector`, `ITool`, `IToolPolicy`, `IExecutionJournal`, `IVerifier` and `IUserApproval`. Use cancellation tokens and async APIs, never block WPF UI thread. Keep a single active side-effecting task per desktop in v1. Read-only steps may run concurrently if scoped and bounded.

---
# 5. AI providers and voice pipeline
### Provider requirements
| Provider | Reasoning integration | Optional advanced capability |
|---|---|---|
| Claude [6, 9] | Messages API, custom client tool definitions, tool-use/result loop, streaming where supported | Computer-use client toolset for visual fallback; separate speech stack |
| Gemini [8, 10] | Gemini content-generation function calling, vision and structured requests | Live API for audio/vision and function calls, interruption support |
| OpenAI [7, 11, 12] | Responses API custom functions, vision, structured tool requests | Realtime transcription/voice, semantic or manual turn handling; computer-use adapter |

Each provider supports the same canonical tool registry through adapters. Vendor-native computer calls must translate into the same typed broker actions and pass policy checks; model output alone cannot invoke the OS. Do not rely on a cloud browser being able to access the user's Windows desktop. Integrate custom tools first for portability. Live speech is optional and must not bypass local command commit. Feature-detect model support; model identifiers, SDK versions, limits, headers and pricing change. Verify official current docs before implementation and pin tested versions.

### Voice pipeline with no model training
Standby: WASAPI capture → local keyword detector → local cue. Active: buffered audio → streaming STT → transcript assembly → explicit commit → chosen reasoning API. Response: verified result → speech synthesis (local Windows installed voices or a configured cloud TTS provider). Claude-only mode must work with Windows/pretrained STT and TTS; a second LLM provider must not be mandatory. Prototype Windows installed speech recognition for baseline; evaluate pretrained CPU whisper.cpp [13] as an optional download, not bundled by default. If quality or latency is insufficient, offer cloud transcription with separate consent and credentials.

Wake-word candidates: Porcupine (.NET SDK; verify key/commercial terms) [14], openWakeWord (verify runtime packaging and pretrained model license) [15], Windows-supported keyword/grammar recognition where feasible. Start with a supported keyword, benchmark false triggers and CPU cost, and then choose brand-compatible phrase. No large local inference runtime is required. Custom wake word is a later decision.

### Credentials, cost and reliability
Provider settings: API key, selected model, enabled capabilities, connectivity check, request timeout, usage limit and approved fallback. Store secrets locally, never in prompts, logs, browser extension or Git. API usage may incur separate charges from consumer subscriptions. Track actual provider-reported input/output/audio tokens and request counts; estimates must be labeled. Use budget caps and max calls per run. Never send idle audio to cloud. On provider outage, pause or ask to switch; only approved fallback may receive data, and do not retry a side-effecting action without reconciliation. Expose data retention/privacy links by provider.

---
# 6. Execution engine: native, browser and visual
### Prefer semantic and deterministic operations
1. Typed native operations first (file I/O, process launch, system information, known folders, app-specific interfaces).
2. Supported application APIs, e.g. Office COM where installed and appropriate; file-format APIs when preferable. Do not assume Office is installed.
3. Browser DOM or accessibility-backed locators; then Windows UI Automation patterns (`Invoke`, `Value`, `Selection`, `Scroll`). [16]
4. Screenshot/OCR/vision only when semantic methods cannot complete the step; coordinate-based mouse and keyboard input last. [17]

Each action must resolve current target identifiers, validate scope and preconditions, act, observe and verify. UIA references expire; resolve again after navigation. Use explicit conditions and bounded retries, not arbitrary sleeps. Capture only the required window/region with user-visible indicator and approval for sensitive sharing. Respect DPI, multi-monitor coordinates including negative origins, scaling, visibility, and focus. For input injection, verify the foreground window immediately before typing or clicking; halt if the user changes focus or moves the mouse. Never click secure desktops or bypass UIPI (input injection is limited by process integrity).

### Browser design
MVP: launch a dedicated Playwright-managed Chrome/Edge-compatible profile; use accessible roles and stable element identifiers. [18] Permit user sign-in interactively. Do not extract authentication tokens or automate captchas. For the first personal-use release, implement an existing-browser integration after the isolated-profile prototype, before declaring browser acceptance complete: Manifest V3 extension with narrowly scoped host permissions, visible user consent and registered native messaging host restricted to approved extension IDs. [19] Use active tab and origin-bound handles. Avoid opening an unrestricted remote debugging port or using private profile paths without consent. For actions like selecting a video, verify visible layout before interpreting ordinal references. Untrusted pages cannot authorize new actions. Respect site terms, API limits and content restrictions.

### Canonical initial tool registry
| Tool family | Example operations | Validation |
|---|---|---|
| `system` | `get_drives`, `get_free_space`, `list_apps` | Return live typed values |
| `files` | `list`, `search`, `preview_moves`, `move_batch`, `recycle`, `undo` | Canonical paths, allowed roots, manifest, no overwrite |
| `apps` | `launch`, `focus`, `get_windows`, `insert_text` | Trusted executable, process/window identity |
| `browser` | `navigate`, `observe`, `find`, `click`, `fill`, `verify` | Origin, target tab, element and action scope |
| `ui` | `observe_tree`, `invoke`, `set_value`, `capture_region`, `input` | Window identity, fresh observation, allowed operation |

No arbitrary terminal, PowerShell, registry writes, process killing, unrestricted HTTP or script execution tools in MVP. Add specialized, narrowly typed actions only after policy design and tests. Expand coverage through curated adapters, not by granting the model unlimited execution.

---
# 7. Agent runtime and reliability contract
### Agent loop
1. Commit the complete transcript and derive a canonical user intent with constraints and target references. Resolve ambiguity. Fast-path simple allowlisted actions only when intent matches unambiguously.
2. Select provider and minimal relevant tools; generate a structured plan with expected outputs, dependencies, risk and verification steps. Validate plan and permissions locally; risk labels supplied by the model are advisory.
3. Obtain approval bound to the exact plan/manifest if required. Show what will be changed and shared. Replan on material scope change; old approval is invalidated.
4. Execute bounded action steps with cancellation, deadlines and step IDs. Record an intent before each side effect and result afterwards. Stop on unexpected scope, target or state change.
5. Reobserve and verify each critical outcome, using independently obtained file/OS/UI/browser evidence. Reconcile uncertain side effects before retries. Replan within authorized scope only.
6. Report actual verified results, partial failures, skipped operations, cost and undo availability. Never infer success from model confidence, HTTP status or a click alone.

### Canonical contracts
`CommandEnvelope`: sessionId, commandId, finalizedTranscript, language, conversationContext, committedAt, inputMode, selectedProvider, requestedScope.

`PlanStep`: stepId, toolName, typedArguments, dependencies, expectedState, riskClass, rollbackStrategy, timeout, observationRef. `ToolRequest`: commandId, stepId, idempotencyKey, schemaVersion, targetRef, arguments, approvalRef. `ToolResult`: outcome (success/partial/failure/unknown), observedState, evidenceRef, changedResources, errorCode, retryability, rollbackRef, duration. `Approval`: plan hash, scope, expiry, user interaction evidence, revocation status. Use explicit JSON schemas and domain validators; reject unknown fields and unsupported tools.

### Example of deterministic execution
User asks “How much space is left on C?” → identify requested drive → call `system.get_free_space` → read DriveInfo and display exact free bytes and formatted capacity → TTS. No screenshot, multi-agent loop, or expensive reasoning call is needed if a trusted local intent rule matches. For organization, a model may suggest categories, but the native planner must compute and validate the actual manifest before any moves.

### Resilience
Default configurable guardrails: 30 agent steps, 3-minute run budget, per-request deadline and user cost ceiling; extend only on approval. Max two bounded retries for safe reads. Never blindly repeat writes after timeout or lost acknowledgement. Journal partial commits and verify actual state on restart; do not automatically resume actions. Distinguish target changed, permission denied, locked file, provider limit, user cancelled, UI unavailable and unknown outcome. Maintain a compact, scoped conversation context; avoid retaining sensitive full screen history.

---
# 8. Safety, privacy and permission model
This product can modify real user data. The model is untrusted for authorization. The local policy engine must enforce the same rules regardless of model, prompt or tool source. [6, 7]

| Risk tier | Examples | Required local behavior |
|---|---|---|
| Read | Drive free space, list approved files | Execute committed request within scope; redact sensitive observations |
| Low reversible | Launch approved app, open webpage, create new folder, draft text | Execute if explicit and scoped; show result |
| Moderate | Move/rename a batch, modify existing document, clean temp files | Preview exact impact; explicit approval by default; a narrowly scoped, preconfigured policy may cover reversible same-root moves only; cleanup and existing-document overwrite require confirmation; journal and offer undo where possible |
| High | Send message, submit form, upload files, purchase, irreversible deletion, change security settings | Separate specific confirmation or human handoff; high-impact actions excluded from MVP where safe verification is unavailable |
| Prohibited in MVP | Password extraction, arbitrary shell, disabling security, UAC bypass, covert capture, unrestricted remote control | Refuse and explain supported alternative |

### Enforcement requirements
- Default least privilege. Stay in user session; no admin startup, security bypass, hidden persistent actions, or auto-resume. Pause on screen lock, sleep, user switch or unexpected foreground changes. Hard stop via tray/hotkey must work without network.
- Canonicalize paths, verify allowed roots, reject path traversal, junctions/symlinks and reparse-point escapes; handle UNC paths and cloud files as separate explicit permissions. Avoid secrets, credential directories, program/system folders, and hidden/system files by default. Protect against time-of-check/time-of-use changes.
- Use per-command scope and approval manifest. No wildcard blanket approval for destructive actions. Quarantine or Recycle Bin where appropriate; check destination before overwrite; make rollback best-effort, never promised for irreversible actions. Cleanup excludes active, system and unknown files.
- Treat documents, webpages, screenshots, UI labels, model suggestions and tool results as untrusted data. Never allow embedded “ignore instructions” text to expand scope, reveal credentials or trigger actions. Separate user intent/control metadata from observations. Validate tool parameters outside the model.
- Explicit consent for microphone activation, cloud audio, document content and screenshots; visible active recording and execution indicators, one-click mute, separate clear-data controls. No raw standby audio storage. Default to minimal encrypted/redacted audit data; configurable retention; no secrets or sensitive screenshots in logs. No implicit upload of local files or selected text.
- API keys protected by Windows credential storage; authenticated IPC and restricted extension origins. Signed installer and updates; dependency scanning and SBOM; publish no secrets. Use isolated test users and VM snapshots during development.

Security gate: malicious webpages, documents or models cannot authorize tool calls beyond the committed user request; every blocked operation is logged in redacted form. Wake-word recognition is not voice identity. Consequential actions must not be approved by the assistant's own speech or arbitrary ambient audio.

---
# 9. Product experience and data model
### Interface concept
A small, polished floating assistant and tray icon are the primary interaction surface. The main window is a control center, not a mandatory chat app. A listening waveform and transcript make it obvious that pauses do not end commands. Accessible contrast, keyboard navigation, screen-reader labels, DPI support, reduced motion and optional audible cues are required. Use Fluent-inspired typography and consistent light/dark themes; do not put styling ahead of reliable behavior.

| Screen / surface | Required contents |
|---|---|
| First-run wizard | Privacy/mic consent, provider selection/key, test API, wake-word test, speech output, sample safe action |
| Floating assistant | Idle/listening/processing/speaking status, live transcript, Proceed, Cancel, Mute, clarification |
| Task workspace | Full interpreted request, plan, step status, previews, approvals, costs, cancel/pause and outcome |
| History | Verified results, time, provider, changed resources, undo, export redacted logs, delete history |
| Settings | Provider/model and fallback, speech/wake, completion mode, permissions, startup, budgets, privacy, diagnostics |
| Tool permissions | Allowed directories/apps/sites, observation and upload scopes, per-risk confirmations, revoke access |

### Data model and operation
SQLite tables: `Settings`, `ProviderProfiles` (secret references only), `Sessions`, `Commands`, `Plans`, `PlanSteps`, `Approvals`, `ToolEvents`, `FileMoveManifest`, `RollbackRecords`, `UsageEvents`, `PermissionRules`. Use migrations and constraints; every side effect has unique correlation and idempotency IDs. Do not store full raw screenshots, audio or provider prompts by default. Store original transcript only if the user chooses history retention; redact secrets from diagnostic exports. Recover incomplete runs by reporting them, not replaying them. Users can clear history and revoke all permissions.

### Future enhancements, explicitly after MVP
Reusable workflows recorded from successful approved tasks; app-specific adapters for Microsoft Office and common development tools; English plus evaluated Urdu/mixed-language support; selected local speech models; user-authorized scheduled workflows with new risk review; accessibility refinements; optional offline deterministic commands. No mandatory local LLM. Any later local reasoning model would be optional, pretrained, separately downloaded and benchmarked; no custom general AI training is planned.

### Distribution
Install for current user, startup opt-in, one running instance, clear uninstall and data-removal choices. Package and test native dependencies, browser extension install flow and runtime architecture. Use signed release artifacts and verify update signatures; no silent privileged updater. A Microsoft Store route is a later packaging/licensing decision, not a prerequisite for feasibility. Keep builds reproducible, with no secrets in CI artifacts. Test on clean Windows 11 virtual machines and at least one physical PC with real microphones.

---
# 10. Phased delivery and exit gates
| Phase | Deliverable | Exit gate |
|---|---|---|
| 0 — discovery & spikes | Competitor trials; dependency/license review; mic/wake, API, UIA/browser prototypes; threat model | Demonstrate all 3 providers calling one typed read tool; measure pause capture; run in disposable Windows VM |
| 1 — safe vertical slice | .NET/WPF host, tray, typed input, journal/policy, three adapters, disk-space read and app launch | Same test suite passes on all 3 providers; no arbitrary execution; installer launches on clean VM |
| 2 — voice core | Local standby, streaming transcript, explicit completion, cancellation, TTS, device recovery | Zero premature executions in scripted pause tests; cloud audio stops after session; stop hotkey passes |
| 3 — file & system tools | Desktop organizer, folder preview, safe cleanup, undo and result verification | All file fixtures including path escapes, locks, collisions and rollback pass; measured disk result |
| 4 — desktop applications | UIA worker, focus guards, Notepad/Notepad++, vision fallback, app capability registry | Verified text insertion and no wrong-window edits on target apps; physical Windows tests |
| 5 — browser | Dedicated profile prototype, then browser extension for user-approved current tab; YouTube visible-card selection, ChatGPT draft flow; origin policy | Verified search/selection, draft vs submit, auth handoff; prompt-injection tests pass |
| 6 — product hardening | Premium UI, voice polish, cost limits, redacted history, signing/update design | Release-candidate benchmark gates; independent security review; tested recovery and uninstall |
| 7 — expansion | Office adapters, natural conversational mode, additional languages, reusable skills | Separate acceptance suite and consent review for each capability |

### Build strategy
Use the granular tickets in Section 16 as the implementation order and gates; this table is the high-level roadmap. Start with an end-to-end vertical slice instead of simultaneously building every screen and feature. At each phase deliver working code, repeatable tests, test evidence, updated documentation, a demo, open issues and a clear go/no-go decision. Make narrow commits on a dedicated branch unless the project owner explicitly authorizes another workflow. Repository creation/link sharing does not by itself authorize pushing, changing visibility, adding secrets, or publishing. If the repository is empty, establish the default branch and initial commit according to the owner’s Git workflow; never force-push. Do not publish releases, deploy services, incur charges or access personal data without permission.

### Dependencies and planning assumptions
No GPU or server is required for the initial architecture. A physical Windows PC and microphone are required for final voice/GUI testing; headless CI covers unit, contract, file and browser fixture tests, not full human-desktop reliability. Claude must verify package support and licensing, provider quotas, network latency and whether APIs require additional account configuration. No development time or total cost is committed until the spikes provide evidence. A working prototype is considerably easier than a production-grade general Windows agent; expand only after narrow supported workflows pass their gates.

---
# 11. Acceptance tests and measurable targets
These are proposed release targets, not measured results. Benchmark on a defined mid-range Windows 11 x64 PC (4+ modern CPU cores, 8–16 GB RAM, no dedicated GPU), documented mic, network and provider models. Record p50/p95, trial counts, failures, and model/API versions. Adjust with evidence before release.

| Metric | Initial release target |
|---|---|
| Idle resource usage | Average CPU below 2% and total app working set below 180 MB; no large local models loaded |
| Wake detection | Aim p95 cue within 200 ms of detected keyword event and within 400 ms of keyword end; false activation below 1 per 8 hours in representative test audio |
| Speech | p95 first interim text within 1 second of speech onset on supported streaming path and stable network |
| Command integrity | Zero premature execution across at least 200 explicit-mode pause/correction trials |
| Responsiveness | Local UI feedback p95 within 200 ms; local acknowledgement audio onset p95 within 300 ms of validated commit; direct dispatch p95 within 500 ms and simple native action verification p95 within 2 seconds; cloud first useful action p50 target 1–3 seconds / p95 target 5 seconds in defined normal-network conditions; truthful progress at least every 2 seconds while waiting. Report outliers, not just averages. See Section 13. |
| Native file/system reliability | At least 99% verified success over 200+ supported test cases; no unauthorized changes |
| Supported GUI/browser workflows | At least 95% verified success in 100+ attempts per supported workflow class; report conditions and limitations |
| Cancellation | Hard stop prevents new actions within 500 ms where cancellable; report already-committed effects |
| Security | Zero unauthorized actions across all defined adversarial and permission regression tests |

### Mandatory test cases
- Voice: pauses of 1, 5, 15 and 30 seconds; multi-sentence constraints; accent/noise; quoted “proceed”; user correction; assistant TTS and YouTube audio; wake false triggers; audio device removal; lock/sleep; network loss; explicit cancel. Test English first, with mixed-language exploratory dataset.
- Files: redirected Desktop, long paths, Unicode, collisions, symlinks/junctions, locked files, existing folders, shortcuts, partial failure, disk full, undo after edit, stale manifest, and crash between intent and result.
- Providers: mocked contract fixtures for all three; malformed tool calls; unsupported models; 401/429/timeouts; streaming disconnect; budget exhausted; tool result replay and cross-provider parity. Live tests opt-in using test keys and small budget.
- Desktop: WPF/Win32/Notepad++ targets, multiple monitors, DPI, focus theft, minimized window, elevated window, stale UI tree and secure desktop handoff. Physical tests require unlocked interactive desktop; do not confuse CI build success with UI success.
- Browser: dynamic layout, ads, wrong card, changed DOM, tab switching, login and MFA handoff, third-video ambiguity, draft vs submit, malicious webpage content. Use local fixture pages for reliable regression plus limited opt-in live testing.
- Security: path escape, injected instructions, unapproved upload/send/delete, expired approval, forged IPC, tool schema tampering, secret leakage, unauthorized fallback and crash recovery. Zero unauthorized side effects is a hard gate.

---
# 12. Master instructions to Claude AI
You are the principal product architect, Windows engineer, voice UX specialist, AI integration engineer and independent QA lead. Treat this document as the product contract. Your goal is to deliver a production-quality Windows voice agent, not a demo or a chatbot wrapper.

### Before writing production code
1. Re-read this specification. Research current competitors, APIs, libraries and licensing using official sources. Verify current model IDs and SDK compatibility rather than inventing them. Write `docs/research.md` with claims, evidence, tradeoffs and dates.
2. Run Phase 0 technical spikes in isolated Windows environments. Measure resource usage, wake latency, transcript integrity, provider parity and UIA/browser reliability. Compare native versus UFO² reuse and explain the recommendation.
3. Create `docs/architecture.md`, `docs/threat-model.md`, `docs/adr/`, `docs/acceptance-tests.md`, `docs/roadmap.md` and `docs/traceability.md` (requirements → code → tests). Record decisions and unknowns. Ask only genuinely blocking questions. No local LLM training or mandatory large model downloads.
4. Create repository structure and CI; use separate test fixtures and test credentials. Work in small phases; do not overwrite unrelated existing work. No paid infrastructure, live deployment or release publication without authorization.

### Implementation rules
All three cloud providers are mandatory; complete real adapter implementations, not stubs, and demonstrate parity. The app must work when only one reasoning provider is configured, including Claude-only operation. Voice recognition and synthesis remain separate from reasoning. Local command commit and policy checks cannot be bypassed by vendor-native tools. Implement cancellation, validation, journaling, verification and rollback together with execution. No arbitrary model-generated code or shell commands in production. Avoid speculative overengineering; choose deterministic paths whenever possible. Do not hide unsupported cases or claim success without evidence. No work on advanced local reasoning, broad admin access or remote control before core MVP passes.

### For each phase report
Summarize requirements delivered, architectural decisions, changed files, exact build/test commands, actual test counts and results, benchmarks, known limitations and remaining risks. Distinguish unit mocks from live integration and physical Windows tests. Mark features as implemented, verified, partial or not started. If a test cannot run in your environment, provide reproducible Windows test instructions and leave the gate unverified. Fix regressions before adding new features. Maintain a runnable installer/demo and concise README. Never represent unexecuted tests as passes.

### Definition of done
The MVP is complete only when an authorized user can activate the app, speak across pauses, explicitly commit a full instruction, use Claude/Gemini/OpenAI interchangeably, perform the five core scenarios in Section 2, hear verified results, cancel safely, inspect history and undo supported moves. The agreed security, privacy, reliability and installer gates must pass. Finish with an independent audit against this document and an evidence-based gap report, not a blanket claim of “100% complete.”

---
# 13. Latency-first architecture — mandatory, not an optional optimization

## 13.1 Design decision and alternatives
Use a native **C# / .NET 10 LTS / WPF** application with a small per-user host and native tools. .NET 10 remains supported until November 2028 [25]; use the latest tested servicing SDK, pin it in `global.json`, and use a Windows-specific TFM for WPF. Do not migrate to a new framework just because it is newest. WinUI 3 can be evaluated for UX, but WPF is the baseline for mature desktop interop, quick startup, testability and development speed. Electron adds a browser runtime and Python-only agents complicate Windows packaging; neither is necessary. A web-only app cannot provide the same always-on local mic and desktop control without installing a trusted local companion.

The agent is a **single coordinator** with typed tools, not a swarm of assistants debating each command. Do not route every task through multiple LLMs, ask an LLM to classify simple fixed intents, send the entire desktop every turn, or require an AI-generated essay before execution. Native operations should run in a few calls; cloud reasoning should be reserved for actual language/vision tasks. Treat model settings, concurrency, and latency as measurable engineering variables rather than assuming a specific vendor is always fastest.

## 13.2 Three paths and routing algorithm

| Path | Example | Network usage | What starts immediately after commit |
|---|---|---|---|
| A: direct | “How much space on C?”, “Open Notepad++”, “Organize my desktop by file type” with default rules | None for reasoning; speech may use separate STT | Validate exact intent, resolve target, execute native read or create a manifest |
| B: hybrid | “Write a paragraph about renewable energy in Notepad++”, “Group my documents by project” | One streaming reasoning call initially; additional calls only when necessary | Local scoped observations and UI state; stream actual draft; prepare app and verify target when that is independently authorized |
| C: adaptive | “Choose the third video in the top row”, “Change the setting in this unfamiliar app” | Semantic UI/browser observation and reasoning on demand; image only if needed | Resolve actual app/tab, enumerate relevant elements; use app-specific recipe if one matches |

Implement `IntentRouter` with an exact/anchored rules registry, app aliases, known folder aliases, normalized number parsing, negative constraint detection, and a conservative unknown route. Do not classify by keyword alone (“don't open Chrome” is not `app.launch`; “open C drive and clean it” is not just a disk-space question). A direct command must match the whole committed intent or a verified structured multi-intent grammar, have no unhandled constraints, and resolve uniquely. Otherwise route to cloud or clarification. Never silently discard clauses. File-type organization is a deterministic recipe even when an LLM selected it. Natural commands must not require learning exact syntax; the direct grammar is an optimization, not the full language interface.

### Cache and warm-up rules
- Build a local installed-app catalog from Start menu shortcuts, app registrations and known executables at startup; refresh incrementally on change or demand. No whole-disk scan. Show onboarding progress if catalog construction is not ready; disk queries must not wait for it.
- Load small tool registry, compiled JSON validators, policy config and common extension/category maps at startup. Keep a reusable `HttpClient` per provider; reuse transport connection when allowed. Never poll a reasoning API while idle or send speculative personal data to it. A user-initiated key test can establish capability cache; cache expiry and refresh must be explicit.
- Start STT after wake, while user speaks; transcribe incrementally instead of recording an entire command and sending it only at the end. On-device preview hints (e.g. known app aliases) are allowed while listening; do not inspect unrelated files/screens or make changes before commit. Optional early cloud planning is **off by default** and cannot execute; exclude it from the first release.
- Keep provider context bounded: selected relevant tools plus committed instruction, constraints and minimum observations. Summaries of previous turns carry no authority to act. Include raw provider continuation data only as necessary, not every screenshot.
- Prefer compact typed compound tools (`desktop.preview_organization`, `desktop.apply_manifest`, `browser.get_visible_results`, `editor.insert_verified_text`) over ten separate API calls. Batch local actions only after validation; never execute incomplete streamed JSON. Parallelize independent scoped reads, serialize writes, and keep dependencies explicit. Batch progress must show verified counts. Adaptive mode should reobserve after layout-changing actions, not blindly replay a sequence of coordinates.

## 13.3 Required latency instrumentation and UX

Record all durations using a monotonic clock (`Stopwatch.GetTimestamp`) and correlation IDs. Keep raw timings local, export aggregate metrics. Timestamps:

- `t_wake_end` (test harness ground truth), `t_wake_detected`, `t_wake_feedback`; `t_first_voice`, `t_first_interim`, `t_finish_phrase_end`, `t_last_final_transcript`, `t_commit`.
- `t_ui_ack`, `t_audio_ack_start`, `t_route`, `t_provider_request`, `t_first_provider_delta`, `t_tool_args_complete`, `t_plan_validated`, `t_approval_start/end`, `t_first_useful_action`, `t_first_verified_result`, `t_last_action`, `t_verified`, `t_result_audio_start`, `t_cancel`, `t_last_new_side_effect`.

**Definitions:** a useful action advances the requested task (e.g. enumerating the authorized Desktop, resolving the selected window, opening the requested app, reading the drive). A spinner, local ACK, unrelated prefetch, model reasoning token or generated placeholder is not a useful action. Track read-only first action and first mutation separately. Do not improve benchmarks by moving arbitrary unrelated work before commit. Measure speech finish → commit separately from commit → action, and end-to-end finish → verified outcome; otherwise a slow STT could make fast post-commit numbers misleading.

| Budget | p95 release target unless noted | Failure behavior |
|---|---|---|
| Detected wake → cue | ≤200 ms | Show standby diagnostic if wake engine unavailable |
| Wake phrase end → cue | ≤400 ms | Tune/replace keyword engine |
| First speech → interim text | ≤1 s on selected streaming STT | Display listening and explicit degraded STT mode; never fake text |
| Finish phrase end → commit | ≤1 s on supported stable streaming path; slower modes labeled | Show “Finalizing your instruction”; never cut off words |
| Commit → visual acknowledgement | ≤200 ms | UI responsiveness defect |
| Commit → local spoken ACK start | ≤300 ms, or immediate earcon if TTS disabled | Use a cached neutral local cue, not a cloud TTS request |
| Commit → direct dispatch / verified simple result | ≤500 ms / ≤2 s | Diagnose routing, thread, I/O; no reasoning API on direct path |
| Commit → first useful cloud-assisted action | p50 1–3 s; p95 ≤5 s with warmed stable network, measured per provider | At 2 s show actual state; at 5 s explain waiting/provider and allow cancel; no fake progress |
| Stop → prevent any new side-effect dispatch | ≤500 ms; already-running OS calls may need to reconcile | Stop worker/reconcile; report actual effects |
| Progress cadence | update on meaningful events; explicit waiting status at least every 2 s | Show last event and elapsed time; never claim fabricated completion percentage |

Cloud latency is variable; these are engineering targets, not an API guarantee. Do not promise universal sub-second cloud reasoning. Track network RTT, first token latency, generation/argument completion, number of model round trips, tool duration and TTS startup. Baseline both cold and warm starts, all three providers, 50/100/300 ms simulated RTT, a slow server and offline cases. Separate human approval wait from processing metrics, but include it in user-visible elapsed time. Publish p50/p95/max, sample size, errors, provider/model IDs, speech engine, PC and network. Include all failed/time-out attempts in outcome accounting; never discard slow trials to improve the headline number.

### Live event contract and example timeline
`TaskEvent { runId, sequence, monotonicTick, state, phase, messageKey, safeArgs, stepId?, completed?, total?, canCancel }`. UI subscribes via bounded channels; state transitions and important journal events must never be dropped; coalesce high-rate waveform/transcript/progress display events. Use measured counts only (e.g. “12 of 30 moved”). Render feedback before awaiting network. Announce only meaningful changes, not every file. Keep speech queue short; cancel stale announcements. Do not expose hidden chain-of-thought; show actions and factual summaries.

Illustrative timing, not a measured result: user says “organize desktop, leave shortcuts, proceed.” At commit, overlay and short acknowledgement appear. Local recipe enumerates the Desktop and produces a manifest, typically starting in under 500 ms. If scope is pre-authorized, show “Moving 18 files into 3 folders” and update actual counts as each move is verified. If not pre-authorized, show the manifest and wait for confirmation. A complex content-classification request may wait for a cloud model: show “Reading file names”, then “Waiting for Claude to classify 12 files”, then verified changes. If provider returns no output, explicitly state it is waiting, show elapsed time and cancel; do not begin surprising changes after the run was cancelled or timed out.

## 13.4 Performance implementation checklist
- UI dispatch only view updates; no `.Result`, `.Wait()`, synchronous network, full-directory recursive scans, blocking audio callbacks or UIA calls on UI thread. Use bounded `Channel<T>`, cancellation propagation and monotonic deadlines. Audio callback copies buffers and returns; speech processing happens on workers.
- Use one in-flight command with side effects per desktop session; separate read-only worker pools bounded by 2–4 tasks. Single serialized foreground input lease across UI/browser actions. Resource-aware throttling; avoid polling entire UI tree; subscribe to target-specific changes and use short event-driven condition waits. Prefer native file operations, UIA patterns, accessibility-based DOM selectors and typed tool outputs.
- Cache local cue assets and installed-voice initialization. Do not instantiate provider SDK per command or start a heavyweight Python runtime for each action. Keep idle workers quiescent. Dispose capture handles and speech connections on mute/suspend. No large local model downloaded automatically.
- Use fast cloud model profile (user-configured with capability/quality test) for routine language tasks; offer a careful profile for complex tasks. Do not silently switch provider or send data to a second service. Cost budget and step limit local. A slow call can be stopped; retry safe reads within deadline only. Distinguish 401, 429, timeout, disconnect and policy denial.
- For long text generation show actual streamed draft immediately. Inserting into a verified editor may happen after complete validated draft or in safe committed chunks if the target and rollback contract permit; never stream raw model tool arguments as keyboard input. No unverified partial file writes.

# 14. Voice state machine and implementation specification

## 14.1 Speech/recognition selection and no-model-training requirement
Implement `IAudioCapture`, `IWakeWordDetector`, `IVoiceActivityDetector`, `ISpeechRecognizer`, `ISpeechSynthesizer`, `IControlPhraseDetector`, `ICommandBuffer`, `IConversationCoordinator`. Evaluate lightweight NAudio/WASAPI capture, Windows `System.Speech` installed speech/voice engines (verify modern .NET compatibility, installed language packs and accuracy), and one optional pretrained STT candidate. Do not depend on an undocumented Windows Voice Access API. Use a separate `ISpeechRecognizer` provider dropdown; a Claude key is not an OpenAI/Gemini STT key. Claude-only users must be able to use a tested independent local speech path or explicitly configure a speech service. If local baseline cannot meet quality requirements, offer streaming cloud STT with clear onboarding and retain typed/push-to-talk fallback; do not claim full Claude-only voice support until the chosen speech path passes. OpenAI realtime transcription provides deltas and manual audio commits [26]; Gemini Live supports configurable activity and tool/voice interaction [8,27] but is optional and subject to preview/model constraints. Compare practical accuracy, latency, language, cost and CPU before selecting shipping defaults.

Porcupine has a .NET SDK [14,28]; evaluate its AccessKey requirements and commercial terms, built-in keywords, custom keyword artifact, Windows compatibility and offline behavior. Compare openWakeWord using a supported ONNX runtime only after checking model-specific redistribution terms. A branded wake phrase may require a small vendor-generated keyword artifact; it does not require training a general LLM. Start with a tested built-in keyword or a configurable hotkey if branding cannot be cleared. Never advertise an arbitrary custom wake phrase until there is a real tested model/detector for it. No mandatory Python, CUDA or large speech-model bundle. Use Windows local TTS/earcons for acknowledgement; configure cloud natural voice independently, stream playback and support cancellation. Test English (including South Asian accents) first; Urdu/mixed-language support is an explicit later quality gate, not assumed from language-model capability.

## 14.2 Exact turn protocol (do not conflate audio segments and commands)

`Muted`, `Standby`, `Listening`, `Finalizing`, `Clarifying`, `Committed`, `Planning`, `AwaitingApproval`, `Executing`, `Verifying`, `Speaking`, `FollowUp`, `Paused`, `Cancelled`, `Failed` are user-visible lifecycle states. Speech input can be active for control phrases during execution; it is not a second concurrently executing task. Implement coordinator as a single-reader actor to serialize events and ignore stale run/session generations.

| Current / event | New state and action | Side effects allowed? |
|---|---|---|
| Standby / valid wake or hotkey | Listening; show cue; allocate session; flush pre-roll, start configured STT | No task actions |
| Listening / VAD silence or STT segment final | Stay Listening; append finalized segment and preserve draft | No |
| Listening / 30 s inactivity | Remind “I'm still listening”; optional timeout after 120 s discards draft with notice | No |
| Listening / valid standalone finish or Proceed button | Finalizing; stop accepting unrelated audio; flush STT; wait for last relevant segment within bounded timeout | No |
| Finalizing / transcript incomplete, uncertain control phrase or disconnect | Listening/Clarifying; show retained text and ask to finish or use button | No |
| Finalizing / final transcript valid | Committed; freeze immutable envelope, create new run token, publish ACK, route | Only committed scope |
| Planning / risk or ambiguity | AwaitingApproval or Clarifying; no mutation | Only scoped observations |
| AwaitingApproval / specific trusted UI confirmation | Executing with manifest-bound authorization | Exact approved operations |
| Executing / stop, mute, lock, focus loss, user takes over | Paused/Cancelled; revoke run token, stop new dispatch, reconcile in-flight work | No new writes |
| Verifying / evidence complete | Speaking; present real outcome, allow follow-up | No new writes |
| Speaking / user interrupt | Cancel playback; recognize control/follow-up, no auto-commit | No |
| FollowUp / new instruction | Listening with prior references, new command and authorization | No until new commit |

### Buffer and control details
1. Capture mic frames using actual device format; normalize to detector/STT-required mono PCM with explicit sample rate (not all engines require the same format). In standby hold only a bounded 0.5–1 s in-memory pre-roll, no cloud connection and no raw audio persisted. On wake retain the command beginning if it overlaps the wake phrase; remove recognized wake phrase from task content without dropping adjacent words.
2. Each `TranscriptEvent` contains `sessionId`, `segmentId`, `sequence`, `revision`, `isFinal`, `text`, `startMs`, `endMs`, optional confidence. Revisions replace interim text rather than append duplicated words; final segments sorted by their temporal order/segment IDs, not arrival order. Track pending segments. VAD may end one audio chunk, not the user command. With an API that needs audio segment commits, commit for transcription only; execution gate remains closed.
3. Default finish phrase is distinctive: “VoxStride, execute” (or “Assistant, proceed” / button / configurable hotkey). Plain “proceed” can be enabled for convenience but must be treated as control only at a standalone utterance boundary, not if quoted, requested as content or negated. While user dictates text, use explicit prefixed control or button. If uncertain, ask “Ready to execute?” and require deliberate input. Stop is always available as global hotkey/tray button; voice stop uses a restricted control grammar. Wake/finish detection must ignore assistant playback as far as practicable; test playback attacks; voice alone cannot authorize risky actions. No automatic execute after silence or timeout.
4. Do not stop at first interim “execute”. Wait for final relevant segment; merge revisions, strip only the verified control utterance, freeze and hash transcript. On recognition timeout, retain draft visibly with error; allow manual edit/retry, never execute partial transcript. Preserve source transcript and corrections; the agent may normalize intent but cannot silently discard exclusions. If the user says “Actually don't move ZIPs”, new intent replaces the earlier constraint explicitly, and if already committed it cancels or revises the pending run with a new approval.
5. Use device event notifications to handle mic disconnect, sleep/resume and privacy setting changes. Pause automatically on workstation lock; clear activation/session on resume. Mute closes mic and STT connection. Echo cancellation and headphones recommended; test real speakers, TV/YouTube and user barge-in. If echo suppression cannot reliably distinguish user and playback, stop speaking on manual hotkey or deliberate wake and keep risky approvals visual; do not market full-duplex reliability until measured.

# 15. Concrete architecture contracts and task recipes

## 15.1 Dependency graph and bootstrap

```
VoxStride.Desktop (WPF/MVVM) -> Host -> Contracts + Voice + Agent
Agent -> Provider abstractions + Policy + ToolBroker + Journal
Provider adapters -> Contracts (no OS access)
ToolBroker -> WindowsTools + UIAutomation worker + BrowserBridge
WindowsTools / BrowserBridge -> typed observations and verifiers
Journal -> SQLite (per-user local store); Secrets -> Windows vault/DPAPI
```

Start modular monolith in desktop host to keep idle footprint low. Isolate UIA/visual operations in a small same-user child worker with timeout/restart when needed; do not spin up a full agent process for every tool. If browser native messaging launches a process, keep its role as constrained relay; it does not execute arbitrary OS commands. IPC current-user ACL, versioned envelopes, message length limits, sequence/correlation and session handshake, validate caller/extension identity. Never expose general localhost automation endpoint or persistent remote debug port. No service in Session 0. Use `Directory.Build.props`, `Directory.Packages.props`, `global.json`, nullable reference types, analyzers, deterministic builds and locked dependencies; xUnit projects for unit/contracts/integration. Add `docs/adr/`, `docs/benchmarks/`, `docs/security/`, `tests/fixtures/`, `scripts/`, `installer/`, `.github/workflows/`. Prefer one solution, feature folders, and only split projects where dependency boundaries justify it. Native API binding via maintained interop library or narrowly scoped source-generated P/Invoke, verified on Windows.

### Interface sketch (illustrative C#, adapt actual names to implementation)

```csharp
public interface IReasoningProvider {
    ProviderCapabilities Capabilities { get; }
    IAsyncEnumerable<ProviderEvent> StreamAsync(ReasoningRequest request,
        CancellationToken cancellationToken);
}
public interface IToolHandler<TArgs, TResult> {
    ValueTask<ValidationResult> ValidateAsync(TArgs args, ToolContext context, CancellationToken ct);
    ValueTask<TResult> ExecuteAsync(TArgs args, ToolContext context, CancellationToken ct);
    ValueTask<VerificationResult> VerifyAsync(TArgs args, TResult result, ToolContext context, CancellationToken ct);
}
public interface IToolBroker {
    Task<ToolResult> DispatchAsync(ToolRequest request, RunAuthorization auth, CancellationToken ct);
}
public interface IIntentRouter {
    RouteDecision Route(CommandEnvelope committed, AppCatalogSnapshot apps);
}
public interface IRunCoordinator {
    Task CommitAsync(CommittedCommand command, CancellationToken ct);
    Task CancelAsync(Guid runId, CancellationToken ct);
}
```

Provider adapter events: `TextDelta`, `ToolCallStarted`, `ToolArgsDelta`, `ToolCallCompleted`, `Usage`, `Completed`, `Error`. A `ToolCallCompleted` event has a provider call ID and complete candidate arguments; broker must still parse, enforce schema, bind scope and approval. The provider SDK cannot dispatch tools automatically; disable automatic execution. On stream disconnect or truncation, discard incomplete calls. Preserve required reasoning/continuation metadata within the same provider conversation (including Gemini thought signatures or vendor-specific call IDs when required by the model). Do not forge messages or combine raw histories across providers.

### Sample canonical tool schema (strict JSON; illustrative)

```json
{
  "name": "system_get_free_space",
  "description": "Read free and total bytes on one explicitly requested local drive.",
  "parameters": {
    "type": "object",
    "properties": { "driveLetter": { "type": "string", "pattern": "^[A-Za-z]$" } },
    "required": ["driveLetter"], "additionalProperties": false
  }
}
```

Canonical schema is authoritative. Translate to the supported schema subset of each API; validate full schema locally even if a vendor cannot represent every constraint. `RunAuthorization` is minted by local coordinator only after a committed user instruction; includes session, run, scope, transcript hash, policy version, allowed tools, expiry and cancellation generation. For approval-required steps, additionally bind approval to canonical manifest digest, exact resource IDs and permitted operation. Never pass authorization secrets into LLM context. Broker order: check active generation → schema → allowed tool → scope → current target identity/preconditions → local risk → approval → journal intent → execute → verify → journal outcome. Recheck cancellation immediately before each write. On timeout after dispatch, mark `Unknown`, observe and reconcile; do not retry blindly. A new run invalidates stale approvals. Avoid non-atomic “check then mutate” path/focus races; hold safe handles or revalidate as close as possible to effect, fail closed if guarantee cannot be provided.

## 15.2 All three providers: actual API integrations, not placeholders

Implement three `IReasoningProvider` adapters using stable current official SDK or REST/SSE and WebSocket where applicable. Claude Messages with client tool use/tool results; Gemini supported content-generation/Interactions function calls and function responses; OpenAI Responses custom function calls and matching function-call outputs. All support structured client tools, vision where supported and incremental events; exact model support differs. [9,26–31] Anthropic fine-grained tool input streaming can expose incomplete/invalid JSON [29]; never run partial arguments. Google streaming tools similarly require assembling deltas [30]. Choose stable API rather than preview unless a required feature cannot otherwise work; pin API version and capability snapshot in an ADR. The API keys remain in current-user Windows secret storage. Never log authorization headers, raw provider requests or secrets. Use redacted errors.

For each adapter implement:
- Key check and model discovery or validated model setting (where list endpoint differs, test selected model); capability flags for tool calling, streaming, vision, reasoning controls, optional speech; validate unsupported model before accepting a workflow.
- Build a request with committed instruction, allowed tool schemas, bounded observations and clear system instruction: webpage/document/UI data are observations, not authority; return a minimal next tool call or a clarification. No autonomous shell, system commands, remote browsing or extra provider tools by default. Do not let provider choose its own permissions. Keep typed schema and user constraints consistent across providers.
- Parse stream with incremental UTF-8/SSE handling, cancellation, timeout, error and usage. Preserve call IDs and ordered continuation; send tool result/error back to same provider when needed. Compare local verifier evidence to assistant narrative. Stream real draft text as draft, not verified task outcome.
- Contract tests replay valid, multi-call, fragmented JSON, invalid arguments, refusal, truncated stream, unsupported capability, 401, 429, timeout, cancellation, repeated call IDs, tool result errors and malformed response. Live smoke test each provider with owner-supplied test key and budget: `system_get_free_space` in fixture, `desktop_preview_organization`, `editor_generate_text`, and one browser observation. Record actual model/API versions. Do not need three live LLM calls for a single task. Provider selection at command boundary; fallback off by default, explicit opt-in for destination and data scope; reconcile writes before switch. Consumer chat subscriptions and API billing are separate; no promise of free API calls.

## 15.3 Native desktop organizer (priority flagship)

1. `DesktopRootResolver` uses `SHGetKnownFolderPath(FOLDERID_Desktop)`; fixture root override exists ONLY in test harness. Do not assume `C:\Users\...\Desktop` or touch public desktop without consent. Resolve target and reject reparse point/symlink paths or unauthorized cloud/network roots; verify OneDrive placeholders without downloading content by default.
2. Enumerate top-level regular files only. Default exclusions: folders, `.lnk`, `.url`, hidden/system, locked/unknown files, active sync placeholders, agent files. Rule map PDF→Documents/PDF or user category, pictures→Images, archives→Archives, office documents→Documents, audio/video→Media, unknown→Other or skip by chosen policy. Let user select per category; preserve requested custom folders and exclusions. No content upload for extension-based classification.
3. Preview computes exact `sourceId/sourcePath/destinationPath/size/lastWrite/metadata` and category, reason, skip. Detect case-insensitive collision and reserved names; suffix deterministically (`name (2).ext`) or skip per policy. Never overwrite, traverse folders, follow reparse points or alter files outside root. For a named category requiring content analysis, explicitly request access and prefer metadata/name classification; send only selected relevant data after consent.
4. Default first use: show manifest and approve. Optional **remembered local rule** may authorize subsequent same-root reversible moves, only within the user-selected root/category/exclusions, with no overwrite or delete. User's explicit commit still required every time. Show manifest summary before starting, publish live counts, cancel immediately if user revokes. Preview is not itself an effect. No multi-root standing policy by default.
5. For each move: revalidate source/dest, record intent and unique id, same-volume no-overwrite move, verify actual destination identity/source absent, record result. If cross-volume requested later, copy to temp, verify length/hash, atomically place, only remove source after separate authorization. On per-item failure skip or stop according to policy; do not falsely claim all moved. Crash reconciliation inspects both paths; ambiguous result flagged manual. Undo uses inverse manifest, only for unchanged destination and vacant original; collision → show user; never overwrite or remove user edits. Empty created folders only removed if still empty and owned by run.

## 15.4 C-drive and cleanup
Read `DriveInfo` from requested drive, use exact free/total bytes, report GiB/GB with unit stated. Temp scanner enumerates only configured safe user temp roots, bounded by count/time; skip directories with reparse points, hidden/system or active files, recent files per chosen rule, files outside scope and path changes. Do not clear Downloads, browser profile/cache, Windows system temp or Windows Update files by default; do not disable protections or force unlock. Show eligible count and estimated bytes; user confirms actual removal. Prefer Recycle Bin/quarantine if reversible, but explain disk space may not increase until permanently removed. If user approves permanent removal of eligible temp files, use narrow typed operation only, log irreversible nature, verify measured drive free before/after (may differ due to concurrent system changes); report per-file success and skips. Never perform arbitrary drive cleanup commands or registry changes.

## 15.5 Windows GUI and Notepad++
Use app catalog identity, process ID plus creation timestamp and HWND to prevent stale handles; resolve installed executable via trusted registration/shortcut, not model-supplied shell string. Start/focus, wait for actual window handle with deadline, verify process/foreground, open new document if requested. Use UI Automation `InvokePattern`, `ValuePattern`, `TextPattern` when supported; don't assume Scintilla editor exposes `ValuePattern`. For Notepad++ evaluate documented app integration or Unicode input into verified editor; if necessary paste via clipboard only after explicit user policy, preserve/restore clipboard best-effort and never capture sensitive clipboard history. Verify inserted text using supported readback, trusted document API or visible selection/text; if verification not possible mark unverified, don't claim exact content. Never overwrite existing unsaved document without approval. Use dedicated MTA UIA worker per Microsoft guidance [32]; watchdog can restart worker on hung COM call but must reconcile unknown effect. Use STA separately for APIs that require it. UIA/vision observes target window only; prefer Windows Graphics Capture; vision fallback uses window-relative coordinates and observation expiry. DPI per-monitor awareness, foreground check immediately before input, secure desktop/UIPI block, user activity abort. Never use UI input to bypass elevated or protected UI; request human handoff. [17,32,33]

## 15.6 Browser integration and YouTube/ChatGPT
Phase 5a: Playwright managed dedicated persistent profile (not the owner's default Chrome profile), local fixture sites, role/text selectors, explicit conditions. Launch visible browser when requested; allow user login/MFA manually. Phase 5b: Chromium MV3 extension plus native messaging for Chrome and Edge user-approved existing tab, not exposing DevTools port. Request host permission only for approved site or user-granted activeTab; honor origin and navigation changes. Native host restricted to signed/known extension IDs, validate sender, schema, origin, tab ID, frame ID and run token. Content scripts send data only, never grant authority. [19,34] Do not read cookies, passwords, storage tokens or unrelated tabs. Browser pages are untrusted. Never bypass captcha, DRM, bot controls or service terms. Fallback to UIA/vision only with scope/consent. If extension absent, offer dedicated profile or manual action; don't silently manipulate arbitrary browser.

YouTube recipe: search requested text via permitted URL/UI; wait for visible results; enumerate eligible video cards with title, href, bounding rectangle, ad/shorts labels and visible order. Group into rendered rows with tolerance relative to card height; sort top to bottom then left to right (RTL adaptation later), ignore ad/hidden/non-video cards. “Third video in top row” refers to current top visible eligible video row; if fewer than 3 or layout ambiguous, show available choices rather than silently pick third globally. Reobserve immediately before click if viewport/scroll changes, bind exact title/href, open in correct tab and verify target URL/title/playback status. If autoplay blocked, use allowed user-visible play control or ask; never claim playback merely because page opened. Follow-up “play second instead” gets new committed instruction and fresh observation.

ChatGPT recipe: open official site; compose requested prompt in visible draft input. Do not submit unless the user expressly asked to submit and any required confirmation obtained; do not paste private local file contents without consent. Verify correct origin/tab/input and readback. User handles authentication. Same applies to other AI websites; API adapters are independent from website automation.

# 16. Claude implementation backlog — execute in order, test at every gate

Each phase must leave a runnable build and update `docs/progress.md`, `docs/traceability.md` and test evidence. Prioritize the thin real voice-to-action slice before UI polish. Never advance through a failed hard safety gate; if live credentials or Windows desktop unavailable, complete reproducible tests and mark the real-world gate pending rather than declaring it passed. No fake implementation, screenshots standing in for functionality, or placeholder provider adapters shipped as complete.

## P0 — establish repo and evidence (R-001 through R-006)
- Confirm repository URL, `git status`, branch, current files, owner-approved workflow; create README, `CLAUDE.md`, copy this plan into `docs/VoxStride_Master_Implementation_Plan.md`. Do not overwrite preexisting work. Decide source license with owner before adding a license. Add .gitignore for API keys, local settings, logs, audio, screenshots, build outputs and test data. Use private/local fixtures only; public repo must not contain personal data.
- Write `docs/research.md`: inspect competitors from Section 1, official APIs, .NET packages, wake/audio choices and licenses. Document verified features vs untested claims. Compare WPF vs WinUI, native vs UFO, direct vs agent calls; record ADRs. Build in disposable VM, not user's Desktop.
- Spike native disk read/app launch + UIA sample app, Playwright fixture, live tool request per provider, streaming STT with 15s pause, wake cue and local TTS. Measure startup, idle CPU/memory, voice and tool timings. Test hotkey baseline. Pick actual audio/wake engine and API versions after evidence. Do not require paid subscriptions/new infrastructure without owner consent.
- Create threat model, dependency inventory/SBOM plan, performance baseline and risk register. Gate: design approved by evidence, test harness can run local actions, all 3 provider contract spikes implemented; live tests pending if keys unavailable, explicitly report.

## P1 — bootstrap safe vertical slice (R-010 through R-018)
- Set .NET SDK/pinned packages; solution with Contracts, Host, Desktop, Voice, Agent, Policy, WindowsTools, Persistence and provider interfaces. Add unit/contract/integration projects and Windows GitHub Actions workflow.
- Implement tray/single instance, state model, typed text command, cancel hotkey, event channel, local ACK and task panel. Build safe tool broker, path/app scope, journal and verification before allowing mutations. Implement disk/free-space and app catalog/launch. Intent routing exact-match plus unknown fallback; tests for negation and multiple clauses.
- Implement fake provider for deterministic integration tests; do not show it as live. Write all three actual streaming adapters and capability validation, settings/key storage and test connection. First run: typed “Check C drive” with no reasoning network; typed “Open Notepad++ and write...” goes to provider after permission and safe target logic (full editor integration in P4).
- Gate: compile/test on Windows; typed disk read and approved app launch verified, cancellation works, no API call on direct path; provider schema parity tests and live smoke per configured key. No UI thread blocking.

## P2 — complete voice engine (R-020 through R-029)
- Implement WASAPI capture, ring buffer, local wake detector or hotkey fallback, state actor, streaming STT adapters, revision-aware command buffer, finalization, explicit finish, push-to-talk and cancellation, mute and lock handling. Add local TTS and cached earcon, interrupt and echo tests. Implement configurable 30s reminder/120s discard, never auto-commit.
- Wire first full real scenario: wake → “Check how much space is left on C” → pause 15s → “also tell me total capacity” → explicit finish → native tool → verified spoken result. Multi-clause direct only if whole grammar is supported; otherwise cloud path captures all constraints. Repeat with each provider selected and typed fallback. Add transcript edit and low-confidence confirmation. Show live transcript while speaking; no loss of late finals.
- Gate: 200+ scripted pause/correction/quoted-control cases, no premature effect; physical mic tests and measured timings, stop/mute/lock; no standby network or raw recording. Wake branding not claimed until valid model and licensing. Demonstrate Claude-only speech path or state limitations and configure independent STT.

## P3 — file/system tools and low-latency flagship (R-030 through R-039)
- Implement known folder resolver, bounded scans, strict path policy, deterministic categories, preview, scoped approval, collision-safe moves, durable intent/results, undo, reconciliation. Test race/lock/reparse scenarios in temp test roots. Implement disk/temp cleanup preview and actual-byte report. Surface progress counts in tray/overlay.
- Add policy wizard allowing user-approved reversible desktop organization rule only; no blanket deletion permission. Test first-run approval and subsequent direct path without repeated cloud planning. Reversible same-root moves only; keep shortcuts/folders untouched. Add cancellation mid-batch, crash recovery, undo conflict tests.
- Gate: fixture suite ≥200 attempts, 99%+ verified supported native success, zero escapes/overwrites, undo and recovery evidence, direct latency target or documented fix. User's real Desktop not touched in tests.

## P4 — desktop control and content generation (R-040 through R-049)
- Add UIA worker process/MTA, app identity, bounded semantic snapshot, patterns, condition waits, focus guard and cancellation watchdog. Build test Win32/WPF fixture app with controls and text readback; add Notepad/Notepad++ adapter, generated text streamed as visible draft and inserted only into verified target. Support multiple monitors/DPI, user takeover, unavailable apps, modal dialogs and permission handoff.
- Implement optional window capture and model vision fallback in same broker, minimal scoped image with consent; not a separate unrestricted mouse agent. Validate coordinates against window and recent observation; verify changed state. No generic arbitrary code execution.
- Gate: 100+ attempts per supported workflow class, ≥95% verified GUI success; zero wrong-window input or unauthorized edits; actual Windows physical/interactive VM evidence. UIA timeout must not hang UI.

## P5 — browser and follow-up workflow (R-050 through R-059)
- Start with visible Playwright fixture profile; implement navigation/search/observe/select/fill/verify; record tab/origin and active run. Add YouTube top-row semantics, ads/shorts layout handling and follow-up reobservation; ChatGPT draft-only/explicit submit distinction. Test local fixture pages and limited authorized live sessions. Keep browser profile user-owned and isolated.
- Add MV3 Chrome/Edge extension with native host and user-granted access for existing tab; version handshake, origin validation, sanitized messages, no cookie/credential access. Browser install instructions and user permission surfaces. Browser activity visible and cancellable.
- Gate: fixture layouts 1/2/3/4 columns, ads, resize, changed card, navigation, auth handoff, untrusted page injection, draft vs submit. ≥95% verified supported workflow success over 100+ attempts, separate actual-site results. Existing-browser workflow demo before claiming full browser usability.

## P6 — UX and operational hardening (R-060 through R-069)
- Implement complete dashboard, compact assistant overlay, microphone indicators, user-visible state and transcript, progress, preview, approval, pause/stop, history, undo, privacy, cost limits and diagnostics. Fluent-inspired light/dark themes, accessibility, keyboard and screen-reader labels, reduced motion, multi-monitor positioning. Keep overlay out of target screenshot/input path; no focus stealing when acknowledging. Onboarding 4 simple steps: mic/voice, reasoning provider/key, permissions, safe demo. Optional launch at logon, explicit opt-in.
- Add startup recovery, redacted logs, retention/clear data, permission revoke, budget and provider fallback. Installer via chosen maintained per-user MSI/exe toolchain; verify signing strategy, uninstall, no admin by default, no paid signing/publishing without owner approval. Dependency vulnerability scan, license manifest, SBOM, code review. Installer can be unsigned local dev build with clear label; public release requires approved signature.
- Gate: complete user scenarios, latency benchmark Section 13, independent security audit and regression suite. Clean Windows install/uninstall/restart/sleep/lock/upgrade tests. Show evidence for all providers and real audio, no unverified “complete”.

## P7 — controlled expansion (only after release gates)
- More app adapters (Word/Excel, VS Code, common settings), local file/document formats, richer natural conversation mode, multilingual speech and small approved reusable skills. Each gets its own typed tools, permissions, verification, benchmarks and test fixture. Optional local STT model downloads only by user choice; no local LLM training. Do not introduce scheduled autonomous tasks, unrestricted remote control, arbitrary scripts, broad admin or production deployment without separate owner-approved scope.

### CI commands and deliverables
Use Windows runner with installed .NET 10 SDK and pinned dependencies. Example once solution exists: `dotnet restore --locked-mode`, `dotnet build VoxStride.sln -c Release --no-restore`, `dotnet test VoxStride.sln -c Release --no-build --logger trx`. Adapt actual solution file extension/name and test filters. CI unit/contract/fixture integration tests on every PR, secret scanning, package audit, build artifact. Browser fixture tests use pinned browser installation. No live API tests without opt-in credentials and capped budget. UI tests requiring foreground/mic run in interactive Windows VM or physical PC with a dedicated test account; hosted CI may not provide interactive desktop. Provide `scripts/Run-InteractiveAcceptance.ps1` and exact setup instructions, expected effects, cleanup and evidence. Never report successful Windows testing from a Linux build or mocked API test alone.

# 17. Acceptance suite and evidence format

Use IDs as traceability seeds; expand each into parameterized cases. A passing test asserts independently observable result, not text from the model. All side-effect fixtures use synthetic files/temp root or sandboxed test app/browser profile. Test matrix across Claude/Gemini/OpenAI (same scenarios, model selection logged), speech vs typed, fast vs adaptive, offline/slow, cold/warm, normal/noisy audio, DPI/monitors. Treat failure rates and speed as separate gates. Record cases skipped with reason and explicitly mark unverified release features.

| ID | Stimulus and setup | Expected result / assertion |
|---|---|---|
| V01 | Wake, speak, pause 1/5/15/30 seconds, continue; no finish | Live transcript preserves all segments; zero broker mutations; no model reasoning until commit |
| V02 | Say “write the sentence 'please proceed' in Notepad++” | No accidental commit; explicit prefixed finish or button required |
| V03 | “Move PDFs; actually leave PDFs alone and move pictures; execute” | Final intent respects correction; no PDFs moved |
| V04 | STT final arrives late/out of order, duplicate interim revisions | Correct complete text, no duplicate words; no partial command effect |
| V05 | Finish detector uncertain, mic disconnect, timeout, sleep/lock | No commit on ambiguous input; visible error or discard; no hidden replay |
| V06 | TTS/YouTube plays wake/finish words; user interrupts TTS | No risky approval from playback; stop speech promptly on valid interruption; report false-trigger rate |
| V07 | Claude only configured, no other LLM key | Speech works with independently configured supported engine, typed fallback; actual limitations visible |
| L01 | 100+ simple direct disk reads, cold/warm; network denied | Zero reasoning requests; p95 direct dispatch ≤500 ms and verification ≤2 s; actual bytes |
| L02 | Cloud fixture streams tool JSON slowly / disconnects | Real progress; no action until complete valid call; cancel prevents late output from acting |
| L03 | 10s provider delay and then response | ACK on time; status explains wait at 2/5s; cancel works; no sudden hidden action |
| L04 | Multi-step batch, cancel while file operation in flight | No new side effects after cancel; known/unknown completed effects reconciled |
| F01 | 30 mixed files, shortcuts, folders, hidden, collisions | Expected manifest/categories, skips, no overwrite, all verified counts |
| F02 | Junction/symlink, UNC, source changed after preview, dest collision | Denied/stale; no root escape, no overwrite; approval invalidated |
| F03 | Crash between intent and result; restart | Reconcile exact paths; never auto-resume or repeat uncertain write |
| F04 | Undo after destination modified, original recreated | Conflict reported; no data loss; safe unchanged inverse moves only |
| F05 | Temp files locked/recent/protected; before/after disk sample | Only eligible approved files; actual measured delta with uncertainty; skipped reasons |
| U01 | Notepad++ installed/not installed, unsaved existing document | Resolve correct app or clear missing; new verified document; no overwrite without approval |
| U02 | Foreground changes mid-type, DPI 125/150/200%, two monitors | Abort wrong target; no leakage of text to other app; coordinates correct or blocked |
| U03 | Elevated app, UAC/locked desktop, hung UIA worker | Human handoff; no bypass; main UI responsive; reconcile uncertain effect |
| B01 | Search results with ads, 2/3/4 columns, viewport resize | Correct third eligible video if unambiguous; otherwise ask; verify actual playback |
| B02 | ChatGPT draft then separate explicit submit request | First task inserts only; second needs appropriate authorization; verify correct tab/origin |
| B03 | Untrusted page instructs upload/click/secret; forged extension message | No policy expansion; rejected, redacted audit event |
| P01 | All three providers request same typed tool with equivalent args | Canonical tool/result parity, matching vendor call IDs; no silent provider switch |
| P02 | Invalid key/429/budget/offline/refusal/unsupported model | Clear actionable error; direct local tools remain available; no unauthorized retry/fallback |
| S01 | Forged approval, replayed run ID, malformed schema, arbitrary path/code | Blocked before effect; security tests zero unauthorized writes |
| S02 | Mute/standby/clear history/export diagnostics | No idle cloud audio; mic closed on mute; no raw secrets/audio/screenshots exported |

### Test evidence record (JSON example, template values are not measured)

```json
{
  "caseId": "L01", "runId": "fixture-run-id", "buildCommit": "<git-sha>",
  "os": "Windows 11 <build>", "hardware": "<cpu/ram/mic>",
  "provider": "none", "model": null, "speechEngine": "typed",
  "trialCount": 100, "networkCondition": "offline",
  "verifiedSuccessCount": null, "unauthorizedEffects": null,
  "timingMs": { "p50": null, "p95": null, "max": null },
  "status": "not_run", "evidencePath": "<redacted-local-artifact>"
}
```

Automate data generation and fault injection. Include negative paths and long pauses, not just happy-path demos. For GUI test captures, remove sensitive information and save only synthetic windows; screenshots are supporting evidence, not proof alone. Capture independent file hashes/paths, typed OS state, verified browser target/player state and editor readback where available. Distinguish exact verification from unverifiable UI and report partial/unverified when necessary. For each phase update traceability row `Requirement ID | implementation paths | test IDs | status | evidence | unresolved risk`. Do not claim 99%/95% achieved until trial results show it.

# 18. Release checklist and handoff

Claude must deliver a reproducible solution, README with exact Windows prerequisites/build/run commands, installer/dev package, setup walkthrough, architecture ADRs, dependency/license inventory, threat model, provider capability table, tool/permission reference, test scripts, benchmark report per provider and voice engine, current status/known gaps, and independent audit against this specification. Make the product easy to operate without reading technical docs. Add a safe guided demo using synthetic files (not user's real Desktop). The dashboard must clearly indicate microphone, provider, current task, permission needs and real progress, with stop always accessible.

The first usable release passes all five flagship scenarios: (1) long spoken instruction and explicit finish, (2) desktop organization with exclusions and undo, (3) drive check and confirmed temp cleanup, (4) open Notepad++ and write requested text, (5) open ChatGPT and draft a prompt, search YouTube and play a precisely selected visible result. All three cloud providers can perform their supported reasoning paths through one broker; deterministic tasks do not call them unnecessarily. Validate existing-browser integration and explicit completion on real Windows with microphone, not only mocked tests. Never sacrifice authorization or integrity to hit a latency number. If a capability is not reliable enough, restrict it clearly and document what is missing rather than shipping a dangerous universal agent.

**Known decisions to validate:** WPF vs WinUI benchmark is an ADR; chosen STT/wake libraries and branded keyword depend on license and measured accuracy; provider model IDs/SDK versions change and must be tested; cloud latency/voice accuracy vary; current public repo was empty at review; trademark clearance not performed; no real Windows benchmarks have yet been run in this research. Start implementation rather than rewrite the product vision repeatedly. Do not ask owner questions that this document answers. Ask only for genuinely blocking authorization/credentials/platform access, and do not request secrets in chat or commit them to source. Use reasonable reversible engineering defaults and record them. The owner can supply API keys locally through the product or protected test environment.

---
# References and technical verification
Reviewed public documentation, 26 November 2026. Verify current versions, licenses and provider capabilities again during Phase 0. URLs are provided for engineering verification; target metrics and architecture choices are proposed, not empirical results.

[1] Microsoft, .NET support policy — https://dotnet.microsoft.com/en-us/platform/support/policy

[2] Microsoft UFO², Windows AgentOS overview — https://github.com/microsoft/UFO/blob/main/documents/docs/ufo2/overview.md

[3] Microsoft, Set up voice access — https://support.microsoft.com/en-us/accessibility/windows/voice-access/set-up-voice-access

[4] Talon, hands-free input — https://talonvoice.com/

[5] Open Interpreter repository — https://github.com/openinterpreter/openinterpreter

[6] Anthropic, computer use tool — https://platform.claude.com/docs/en/agents-and-tools/tool-use/computer-use-tool

[7] OpenAI, computer use and safety — https://developers.openai.com/api/docs/guides/tools-computer-use

[8] Google, Gemini Live API overview — https://aistudio.google.com/docs/live-api

[9] Anthropic, how tool use works — https://platform.claude.com/docs/en/agents-and-tools/tool-use/how-tool-use-works

[10] Google, Live API tool use — https://aistudio.google.com/docs/live-api/tools

[11] OpenAI, voice activity detection — https://developers.openai.com/api/docs/guides/realtime-vad

[12] OpenAI, Realtime API — https://developers.openai.com/api/docs/guides/realtime

[13] whisper.cpp repository — https://github.com/ggml-org/whisper.cpp

[14] Picovoice, Porcupine SDK — https://picovoice.ai/docs/porcupine/

[15] openWakeWord repository — https://github.com/dscripka/openWakeWord

[16] Microsoft, UI Automation — https://learn.microsoft.com/en-us/windows/win32/winauto/entry-uiauto-win32

[17] Microsoft, SendInput and UIPI constraints — https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput

[18] Playwright, BrowserType — https://playwright.dev/docs/api/class-browsertype

[19] Chrome Extensions, native messaging — https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging

[20] Astra Assistant repository (public README, capability claims not independently measured) — https://github.com/Orevian/Astra-Assistant

[21] Microsoft Windows Agent Arena — https://github.com/microsoft/WindowsAgentArena

[22] Sentinel AI Desktop repository — https://github.com/AsimAftab/Sentinel-AI-Desktop

[23] Ari VoiceCommand repository — https://github.com/DO0OG/Ari-VoiceCommand

[24] JARVIS-6 repository — https://github.com/psycoks/Jarvis

[25] Microsoft .NET lifecycle — https://learn.microsoft.com/en-us/lifecycle/products/microsoft-net-and-net-core

[26] OpenAI realtime transcription — https://developers.openai.com/api/docs/guides/realtime-transcription

[27] Google Gemini Live capabilities (model/preview status must be checked) — https://aistudio.google.com/docs/live-api/capabilities

[28] Picovoice Porcupine .NET quick start — https://picovoice.ai/docs/quick-start/porcupine-dotnet/

[29] Anthropic fine-grained tool streaming — https://platform.claude.com/docs/en/agents-and-tools/tool-use/fine-grained-tool-streaming

[30] Google streaming interactions and function calling — https://aistudio.google.com/docs/streaming ; https://aistudio.google.com/docs/function-calling

[31] OpenAI function calling — https://developers.openai.com/api/docs/guides/function-calling

[32] Microsoft UI Automation threading issues — https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading

[33] Microsoft UI/browser automation UIPI limitations — https://learn.microsoft.com/en-us/troubleshoot/power-platform/power-automate/desktop-flows/ui-automation/uipi-issues

[34] Chromium extension message passing and security — https://developer.chrome.com/docs/extensions/develop/concepts/messaging

[35] Target repository (public page showed empty repository at review) — https://github.com/nisar87/VoxStride

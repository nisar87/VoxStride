# VoxStride — Phase 0 research and technology verification

Research date: 26 November 2026. Sources are official documentation unless noted. "Documented" means stated by the vendor; "observed" means exercised in this repository's CI; "unverified" means not yet tested by us.

## 1. Platform and runtime

| Claim | Evidence | Status |
|---|---|---|
| .NET 10 is an LTS release supported until 10 Nov 2028 | Microsoft .NET blog (.NET 10 announcement); .NET support policy | Documented |
| .NET 10 servicing is current (Nov 2026 update released 10 Nov 2026) | Microsoft Support KB "NET 10.0 Update – November 10, 2026" | Documented |
| .NET 11 GA packages exist on NuGet (11.0.0) | nuget.org flat-container indexes for System.Speech / Microsoft.Data.Sqlite | Observed (version index) |

**Decision (ADR-0001):** stay on .NET 10 LTS (pinned with `global.json`, `rollForward: latestFeature`) as the spec requires; do not migrate to .NET 11 merely because it is newer. Package versions pinned centrally in `Directory.Packages.props` (10.0.x line for framework packages).

## 2. Reasoning providers (BYOK)

| Provider | API used | Key facts verified | Default model (user-changeable) |
|---|---|---|---|
| Claude | Messages API `POST /v1/messages`, `anthropic-version: 2023-06-01`, SSE streaming, client tools (`tool_use` / `tool_result`) | Fine-grained tool input streaming can expose partial JSON → never execute partial arguments | `claude-sonnet-5-5` (also `claude-haiku-5-5`, `claude-opus-5-5`) |
| OpenAI | Responses API `POST /v1/responses`, SSE events `response.output_item.added`, `response.function_call_arguments.delta/.done`, `response.output_item.done`, `response.completed` | `function_call` items carry `call_id`; continue with `function_call_output`. `store:false` + `include:["reasoning.encrypted_content"]` keeps the loop stateless | `gpt-6.1-terra` (also `gpt-6.1-sol`, `gpt-6-astra`) |
| Gemini | `POST /v1beta/models/{model}:streamGenerateContent?alt=sse`, `functionDeclarations` with `parametersJsonSchema`, `functionResponse` parts | Gemini 3+/4 return **thought signatures** on function-call parts that must be returned exactly; omitting them yields 400 errors | `gemini-3.8-flash` (also `gemini-3.5-flash-lite`, `gemini-4-argon`) |

Model identifiers change frequently (OpenAI released GPT-6.2 models on 17 Nov 2026; Gemini 3.7 Flash requests are now routed to 3.8 Flash). VoxStride therefore never hard-codes a single model: Settings offers suggestions, accepts any ID, and **Test connection** validates the key and model against each provider's model-lookup endpoint (`GET /v1/models/{id}` or `GET /v1beta/models/{id}`) without sending user data.

**Implementation choice:** typed `HttpClient` + SSE adapters (no vendor SDKs). Rationale: identical event semantics across providers, no automatic tool execution by an SDK, small dependency surface, and contract tests against recorded-shape fixtures. All three adapters are complete implementations, not stubs (see `src/VoxStride.Providers`, `tests/VoxStride.Tests/ProviderContractTests.cs`).

Live calls are **not** made in CI (no keys are stored in the repository). Live smoke steps are in [acceptance-tests.md](acceptance-tests.md#live-provider-smoke-tests).

## 3. Speech, wake word and TTS (no model training)

| Option | Finding | Decision |
|---|---|---|
| Windows `System.Speech` (NuGet `System.Speech` 10.0.0) | Supported on .NET (Windows only); in-process `SpeechRecognitionEngine` with `DictationGrammar`, hypotheses (interim) and recognized (final) events, grammar-constrained phrases, local and offline; `SpeechSynthesizer` uses installed voices | **Default** STT, wake phrase and TTS. Works in Claude-only mode with no second key. |
| Picovoice Porcupine (.NET SDK) | Requires an AccessKey and commercial terms for custom keywords | Not bundled. Candidate if measured false-accept rate of the grammar wake word is unacceptable. |
| openWakeWord | Python/ONNX runtime; model-specific licenses must be checked | Not bundled (spec: no mandatory Python). |
| whisper.cpp | Optional pretrained CPU STT; large download | Deferred optional download (P7). |
| OpenAI Realtime / Gemini Live | Cloud streaming STT/voice; separate consent and cost | Not default; adapters can be added behind `ISpeechRecognizer` without touching the commit gate. |

Wake phrase **"Hey VoxStride"** is implemented as a constrained Windows speech grammar with variants ("hey vox stride", "okay voxstride") and a confidence gate. This requires no keyword model training. Its false-trigger rate is **unverified** until measured on real microphones (gate V06).

## 4. Desktop and browser automation

- **Editor text insertion:** Notepad (`Edit`/`RichEditD2DPT`) and Notepad++ (`Scintilla`) accept `WM_SETTEXT`/`WM_GETTEXT`, which Windows marshals across processes. VoxStride sends them to the *exact* verified HWND with `SendMessageTimeout(SMTO_ABORTIFHUNG)`. No synthetic keystrokes means focus changes cannot leak text into another window (U02). UIPI blocks this for elevated targets, which is reported, not bypassed (U03). Notepad++ is started as `-multiInst -nosession` so the user's open tabs are never touched.
- **Browser:** Microsoft.Playwright 1.65.1 (.NET) with `Channel = "msedge"` uses the Microsoft Edge already present on Windows 11, so no browser download is needed. It runs a dedicated persistent profile under `%LocalAppData%\VoxStride\BrowserProfile`, visible and never headless. The MV3 extension + native messaging path for the user's existing browser is designed in [architecture.md](architecture.md#browser) and is **not yet implemented** (P5b).
- **UFO² comparison:** UFO²/UFO³ is a Python research stack. Adopting it would add a Python runtime and a broader action surface. The native C# core already meets the typed-tool, journal and verification requirements with fewer dependencies (ADR-0003). UFO was not executed on the owner's machine, and we have no benchmark of it.

## 5. Competitors (documented claims only; none tested hands-on yet)

| Product | Documented approach | Hands-on status |
|---|---|---|
| Microsoft UFO² | UIA/Win32/COM agents, hybrid GUI+API | Unverified (requires disposable VM) |
| Windows Voice Access | Offline voice navigation/dictation | Unverified |
| Talon | Programmable voice commands | Unverified |
| Open Interpreter | Model-generated code execution | Unverified; VoxStride deliberately excludes arbitrary code execution |
| Claude / OpenAI / Gemini computer use | Screenshot-driven action loops | Documented; VoxStride keeps any future visual fallback inside the same broker |
| Astra Assistant | README: C#/.NET 10/WinUI 3, multi-provider | Unverified; no code copied |

No competitor gap is claimed as fact. The comparison matrix with measured p50/p95 latency is a pending P0 item that requires a disposable Windows VM.

## 6. Packaging

- **Installer:** Inno Setup 6 per-user installer (`PrivilegesRequired=lowest`, installs to `%LocalAppData%\Programs\VoxStride`). It is built in CI from a self-contained `win-x64` publish, so the target PC needs no .NET install. CI installs it silently, runs `VoxStride.exe --smoke-test`, uninstalls it, and checks that it was removed.
- **Signing:** the installer is unsigned (dev build). Public release requires an owner-approved code-signing certificate, which is not purchased or configured without authorization.

## 7. Environment constraints during this build

The authoring sandbox could not reach NuGet. The .NET SDK was obtained from conda-forge, and cross-platform libraries were compiled and tested locally with an xUnit-compatible shim (`tools/LocalTestHarness`). Everything Windows-specific is compiled and tested only on GitHub Actions `windows-latest`. No physical microphone or interactive Windows 11 desktop was available, so the voice and GUI gates that need real hardware are marked **unverified** in [progress.md](progress.md).

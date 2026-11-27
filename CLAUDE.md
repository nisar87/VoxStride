# VoxStride — standing rules for Claude

Full contract: [docs/VoxStride_Master_Implementation_Plan.md](docs/VoxStride_Master_Implementation_Plan.md). Status: [docs/progress.md](docs/progress.md). Traceability: [docs/traceability.md](docs/traceability.md).

## Non-negotiables
- Nothing executes before an explicit commit (finish phrase, Proceed button, push-to-talk release or typed Enter). Silence, VAD, timeouts and reconnects never commit.
- Every OS effect goes through `ToolBroker` (auth generation → schema → allowed tool → risk → validation → approval → journal → recheck → execute → verify → journal). Models never authorize anything.
- No arbitrary shell, PowerShell, registry writes, process killing, unrestricted HTTP or script execution tools.
- Deterministic local paths first (`IntentRouter`); cloud reasoning only when needed. Claude, Gemini and OpenAI are interchangeable through `IReasoningProvider`.
- Never claim success without independent verification. Report partial/unverified honestly.
- Never commit secrets, personal data, audio, screenshots or real-user files. Tests use temp/synthetic fixtures only.
- Mark features implemented / verified / partial / not started with evidence. Never report unexecuted tests as passes.

## Build and test
- Windows: `dotnet restore VoxStride.sln`, `dotnet build VoxStride.sln -c Release`, `dotnet test VoxStride.sln -c Release`.
- Cross-platform core only: `dotnet test tests/VoxStride.Tests`.
- Offline sandbox without NuGet: `dotnet run --project tools/LocalTestHarness` (xUnit-compatible shim; CI uses real xUnit).

## Layout
`src/VoxStride.Contracts` (immutable contracts) · `src/VoxStride.Core` (policy, broker, journal, router, voice coordinator, run coordinator, tools) · `src/VoxStride.Providers` (Claude/OpenAI/Gemini over HTTPS+SSE) · `src/VoxStride.Windows` (speech, known folders, app catalog, editor, secrets) · `src/VoxStride.Browser` (Playwright/Edge) · `src/VoxStride.Persistence` (SQLite) · `src/VoxStride.Desktop` (WPF app) · `tests/` · `installer/` · `docs/`.

## Workflow
Small, focused commits on `main` (owner-authorized). Never force-push or incur costs without explicit permission.

**Releases:** the owner has given standing permission (2026-11-27) to publish a release after every verified fix, so users get it from the in-app update button. Process: bump `<Version>` in `Directory.Build.props` (must be higher than the last release), add `docs/releases/v<version>.md`, push to `main`, confirm CI is green, then trigger the Release workflow with `gh api -X POST repos/nisar87/VoxStride/actions/workflows/release.yml/dispatches -f ref=main -f 'inputs[version]=<version>'` (tag pushes are blocked by the session proxy). Verify the release has `VoxStride-Setup-<version>-x64.exe` and `SHA256SUMS.txt`.

**User's machine:** never change Windows or app settings on the owner's computer; suggest changes for them to make.

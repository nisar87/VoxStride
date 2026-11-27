<p align="center"><img src="src/VoxStride.Desktop/Assets/voxstride.png" width="96" alt="VoxStride"></p>

# VoxStride

**Speak. Command. Accomplish.** A voice-first Windows agent. Say "Hey VoxStride" (or press **Ctrl+Alt+V**), speak naturally with as many pauses as you need, then say **"VoxStride, execute"**. VoxStride carries out real tasks on your PC, verifies the result, and tells you what actually happened.

- **Nothing runs until you finish.** Pauses, silence and timeouts never execute anything.
- **Local first.** Drive checks, opening apps, organizing folders, temp cleanup and undo run on your PC with no AI call.
- **Bring your own AI.** Claude, Gemini and OpenAI are interchangeable (with an optional backup provider) for writing, browsing, controlling apps and open-ended tasks.
- **Works in your apps and your browser.** Reads and operates other apps' controls through Windows accessibility, and works in your own Chrome/Edge through the VoxStride extension. Risky buttons and screenshots always ask first.
- **Safe by construction.** Typed tools only: no shell, scripts or admin rights. File changes show an exact preview and need your click. Moves never overwrite, everything is journaled, and you can say "undo that".

[![CI](https://github.com/nisar87/VoxStride/actions/workflows/ci.yml/badge.svg)](https://github.com/nisar87/VoxStride/actions/workflows/ci.yml)

## Status
Phases P0–P6 of the plan are implemented, with about 340 automated tests passing on Windows and Linux CI. Those tests include real UI Automation, a real Edge browser with the extension, real speech recognition on live audio, and the installed build's latency and idle measurements. The owner's physical microphone, live provider keys, code signing and store listings are still open. See **[docs/progress.md](docs/progress.md)** for the audited status.

## Get it
Download `VoxStride-Setup-<version>-x64.exe` from the latest [release](https://github.com/nisar87/VoxStride/releases/latest). After that, VoxStride updates itself. The installer is unsigned for now; check it against `SHA256SUMS.txt`. See [docs/installation.md](docs/installation.md).

## Try saying
- "How much space is left on C?"
- "Organize my desktop by file type, but leave the zip files alone" … then "undo that"
- "Clean up my temp files"
- "Open Notepad++ and write a short note about today's plan" *(needs a provider key)*
- "Search YouTube for lofi beats and play the third video in the top row" *(needs a provider key)*
- "Open ChatGPT and draft a prompt asking for a weekly meal plan" *(drafted, never sent)*
- "Move the PDFs in Downloads into a folder called Invoices" *(needs a provider key)*
- "In Calculator, work out 7 times 6" *(needs a provider key)*

## Documentation
| | |
|---|---|
| [User manual](docs/user-manual.md) | Using VoxStride day to day |
| [Installation & build](docs/installation.md) | Install, build from source, package |
| [Architecture](docs/architecture.md) | Components, command lifecycle, latency design |
| [Threat model](docs/security/threat-model.md) | Trust boundaries and mitigations, mapped to tests |
| [Acceptance tests](docs/acceptance-tests.md) | Automated mapping and manual scripts |
| [Traceability](docs/traceability.md) | Requirement → code → test → status |
| [Research](docs/research.md) · [ADRs](docs/adr/) | Verified technology choices |
| [Specification](docs/VoxStride_Master_Implementation_Plan.md) | Product contract |

## Build
```powershell
dotnet restore VoxStride.sln
dotnet build VoxStride.sln -c Release
dotnet test VoxStride.sln -c Release
dotnet run --project src/VoxStride.Desktop
```
Requires Windows 10 2004+/11 x64 and the .NET 10 SDK.

## Licence
Proprietary, all rights reserved. See [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

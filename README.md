<p align="center"><img src="src/VoxStride.Desktop/Assets/voxstride.png" width="96" alt="VoxStride"></p>

# VoxStride

**Speak. Command. Accomplish.** A voice-first Windows agent. Say "Hey VoxStride" (or press **Ctrl+Alt+V**), speak naturally with as many pauses as you need, then say **"VoxStride, execute"**. VoxStride carries out real tasks on your PC, verifies the result, and tells you what actually happened.

- **Nothing runs until you finish.** Pauses, silence and timeouts never execute anything.
- **Local first.** Drive checks, opening apps, organizing folders, temp cleanup and undo run on your PC with no AI call.
- **Bring your own AI.** Claude, Gemini and OpenAI are interchangeable for writing, browsing and open-ended tasks.
- **Safe by construction.** Typed tools only: no shell, scripts or admin rights. File changes show an exact preview and need your click. Moves never overwrite, everything is journaled, and you can say "undo that".

[![CI](https://github.com/nisar87/VoxStride/actions/workflows/ci.yml/badge.svg)](https://github.com/nisar87/VoxStride/actions/workflows/ci.yml)

## Status
Working Windows build with a per-user installer. All automated tests pass on Windows and Linux CI. Voice on real microphones and live provider and browser runs still need verification on a physical PC. See **[docs/progress.md](docs/progress.md)** for the audited status.

## Get it
Download the **VoxStride-Setup-x64** artifact from the latest green [CI run](https://github.com/nisar87/VoxStride/actions/workflows/ci.yml). The installer is unsigned for now. See [docs/installation.md](docs/installation.md).

## Try saying
- "How much space is left on C?"
- "Organize my desktop by file type, but leave the zip files alone" … then "undo that"
- "Clean up my temp files"
- "Open Notepad++ and write a short note about today's plan" *(needs a provider key)*
- "Search YouTube for lofi beats and play the third video in the top row" *(needs a provider key)*
- "Open ChatGPT and draft a prompt asking for a weekly meal plan" *(drafted, never sent)*

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

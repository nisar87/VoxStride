# Third-party notices

VoxStride includes or uses the following components. Each is licensed under its own terms. The full, generated list with exact versions (including transitive packages) is in the SPDX SBOM attached to each release (`VoxStride-<version>.spdx.json`).

| Component | Version | Licence | Use |
|---|---|---|---|
| .NET runtime and Windows Desktop runtime (Microsoft) | 10.0 | MIT | Runtime, shipped self-contained |
| System.Speech (Microsoft) | 10.0.0 | MIT | Offline Windows speech recognition and voice |
| Microsoft.Data.Sqlite (Microsoft) | 10.0.0 | MIT | Local history and journal |
| SQLitePCLRaw (Eric Sink) | 2.1.13 | Apache-2.0 | SQLite native binding |
| SQLite | bundled with SQLitePCLRaw | Public domain | Database engine |
| Microsoft.Playwright | 1.65.1 | Apache-2.0 | Controls VoxStride's own Microsoft Edge window |
| Inno Setup (Jordan Russell, Martijn Laan) | 6.x | Inno Setup licence (free incl. commercial) | Builds the installer; not shipped as a library |
| xUnit.net | 2.9.3 | Apache-2.0 | Tests only; not shipped |

Microsoft Edge, Google Chrome, Windows UI Automation and the Windows speech engines are part of the user's system and are not redistributed.

No third-party source code, models or assets are copied into VoxStride. Competitor projects were studied for ideas only (`docs/research.md`).

Online services (Anthropic Claude, Google Gemini, OpenAI) are used only with the user's own API keys, under each provider's terms.

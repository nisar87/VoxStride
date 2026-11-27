# Installation and build guide

## Install (end users)
1. Download `VoxStride-Setup-<version>-x64.exe`. Get it from the latest [release](https://github.com/nisar87/VoxStride/releases/latest), next to `SHA256SUMS.txt` and the SPDX SBOM. Installed copies update themselves from the same page.
2. Run it. No administrator rights are required; it installs to `%LocalAppData%\Programs\VoxStride`.
3. The installer is currently **unsigned**, so Windows SmartScreen may warn. Choose *More info → Run anyway* only if you downloaded it from the repository above.
4. Requirements: Windows 10 2004+ or Windows 11, x64. Microsoft Edge (preinstalled on Windows 11) for browser tasks. An English Windows speech recognizer for voice (Settings › Time & language › Speech).

**Silent install:** `VoxStride-Setup-0.1.0-x64.exe /VERYSILENT /CURRENTUSER`
**Uninstall:** Windows Settings › Apps › VoxStride. You'll be asked whether to remove settings, history, the browser profile and saved keys.

## Build from source (developers)
Prerequisites: Windows 11 x64, .NET 10 SDK (see `global.json`), and optionally Inno Setup 6 for the installer.

```powershell
git clone https://github.com/nisar87/VoxStride.git
cd VoxStride
dotnet restore VoxStride.sln
dotnet build VoxStride.sln -c Release
dotnet test VoxStride.sln -c Release
dotnet run --project src/VoxStride.Desktop -c Release
```

Package the installer:
```powershell
dotnet publish src/VoxStride.Desktop -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o artifacts/publish/win-x64
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 "/DSourceDir=$PWD\artifacts\publish\win-x64" installer\VoxStride.iss
scripts\Test-Installer.ps1 -Installer (Get-ChildItem artifacts\installer\*.exe)[0].FullName
```

Headless smoke test of a built or installed app (no network, never touches your Desktop):
```powershell
VoxStride.exe --smoke-test --out $env:TEMP\voxstride-smoke.json
```

Cross-platform core tests also run on Linux/macOS: `dotnet test tests/VoxStride.Tests` and `dotnet test tests/VoxStride.Tests.Persistence`.

## Configuration locations
| Item | Location |
|---|---|
| Settings (non-secret) | `%LocalAppData%\VoxStride\settings.json` |
| History, journal, undo data | `%LocalAppData%\VoxStride\voxstride.db` |
| Browser profile | `%LocalAppData%\VoxStride\BrowserProfile` |
| API keys | Windows Credential Manager → Generic credentials → `VoxStride/provider/*` |

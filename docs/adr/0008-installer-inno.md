# ADR-0008: Per-user Inno Setup installer from a self-contained publish

- Status: accepted
- Decision: Inno Setup 6 with `PrivilegesRequired=lowest`, installing to `%LocalAppData%\Programs\VoxStride` from a self-contained win-x64 ReadyToRun publish. Optional startup task. Uninstall removes app files and, after asking, user data and saved keys.
- Rationale: no admin rights and no .NET prerequisite, and it is verifiable in CI (silent install → smoke test → uninstall). MSIX was not chosen because of its packaging constraints on global hotkeys and microphone behaviour; it can be re-evaluated later.
- Consequences: the installer is unsigned until the owner supplies a certificate. Windows SmartScreen will warn.

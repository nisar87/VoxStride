# ADR-0001: .NET 10 LTS, WPF, modular monolith

- Status: accepted (2026-11-26)
- Context: The spec requires a native, lightweight, voice-first Windows app with mature desktop interop. .NET 10 is LTS (supported to Nov 2028); .NET 11 GA exists but is STS.
- Decision: C# / .NET 10 (pinned via global.json), WPF with the built-in Fluent `ThemeMode="System"`, a single per-user process. No Electron or Python runtime, no Windows service.
- Consequences: fast startup and low idle memory; WinUI 3 is not used, and it can be re-evaluated for UX later without touching Core. `ThemeMode` is an evaluation API in .NET 10 (WPF0001 suppressed).

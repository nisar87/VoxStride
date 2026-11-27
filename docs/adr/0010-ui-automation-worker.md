# ADR-0010: In-process UI Automation on a dedicated MTA worker; vision fallback with per-use consent

- Status: accepted (2026-11-27)
- Context: P4 needs reading and operating other apps' controls without letting a hung target freeze VoxStride, without wrong-window input, and with a screenshot/vision fallback that is never covert (spec §6, §8, P4).
- Decision:
  - Managed UI Automation (`System.Windows.Automation`) runs on a **dedicated MTA worker thread** with a per-call timeout (8–10 s). On timeout the worker is abandoned and replaced, so later calls (and other windows) keep working. A separate worker process was considered. The in-process worker gives the same isolation from hangs with far less IPC surface, and a process is only needed if UIA crashes the host, which hasn't been observed.
  - Windows are identified by **hwnd + pid + process start time**. A closed window or a reused handle is detected before every action.
  - Snapshots are bounded (≤400 elements, depth 14, 6 s). Element ids are **never reused within a window**, so an id from an older observation can't alias a different control; it is refused as stale.
  - Policy lives in Core (`UiSession`, testable without Windows):
    - Never touched: Windows security and sign-in UI (consent, LogonUI, CredentialUIBroker…), VoxStride itself, and user-blocked apps.
    - Password fields are refused.
    - Irreversible-sounding buttons (send, delete, pay, submit…) and every coordinate click need on-screen approval.
    - Actions need a fresh (≤2 min) observation.
  - Input guard: typing and clicking require the target to be in front, and typing stops if the user switches windows or moves the mouse. Clicks also require the window to be where the screenshot saw it, with the point not covered (`WindowFromPoint` root check).
  - Vision fallback: `ui_capture_window` captures one window with `PrintWindow`, encoded with a built-in PNG encoder, after **per-use approval** (or never, per Settings). The image is sent to the AI once as an untrusted observation, captioned as such, and never saved. Image input was added to all three providers.
- Consequences: apps that run elevated can't be typed into (UIPI). VoxStride says so instead of trying to bypass it. Multi-monitor and DPI correctness relies on PerMonitorV2 physical coordinates. It isn't tested on multiple monitors in CI.
- Evidence: `UiToolsTests` (policy), `UiAutomationTests` and `UiAutomationGateTests` (real UIA against `tests/VoxStride.UiFixture` on Windows CI: 100/100 for set-value, invoke+verify and toggle, a hung-app timeout, and stale-id refusal).

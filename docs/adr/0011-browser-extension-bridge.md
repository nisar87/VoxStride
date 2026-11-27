# ADR-0011: Existing-browser integration via an MV3 extension, native messaging relay and per-user pipe

- Status: accepted (2026-11-27)
- Context: spec §6 and P5b require working in the user's own Chrome/Edge (their signed-in sessions) without a remote-debugging port, without private profile access, and with narrowly scoped, user-granted site access.
- Decision:
  - **Extension (Manifest V3):** permissions are `nativeMessaging`, `activeTab`, `scripting`, `storage` and `alarms`. Site access is *optional* host permissions, granted per site by the user from the popup (the browser shows its own prompt). The extension acts only on the active tab, re-checks the tab's origin on every request, and shows a badge while connected and while acting. It never reads cookies, storage, passwords or other tabs.
  - **Fixed extension id:** a public key in `manifest.json` gives the deterministic id `ljfgkidknpjooifilobgenemahnnefop`. The private key is not kept, because unpacked loading doesn't need it.
  - **Native host:** the installer registers `com.voxstride.bridge` for Chrome and Edge under HKCU, with `allowed_origins` limited to that id. The browser starts `VoxStride.exe chrome-extension://…/`, which runs only a **relay** (`NativeHostRelay`) between stdio and a per-user named pipe (`PipeOptions.CurrentUserOnly`). The relay holds no logic or authority.
  - **App side:** `ExtensionBrowser` accepts the connection only if the caller origin matches the allowlisted id **and** the protocol version matches. Requests are correlated and time out, and messages are capped at 1 MB. Page data is re-validated: element ids must match `^b\d+$` and strings are capped.
  - The same page script (`voxstride-page.js`) powers VoxStride's own Playwright window and the extension. A test asserts the two copies are identical.
  - Publishing to the Chrome Web Store or Edge Add-ons requires paid developer accounts and review. Per CLAUDE.md no costs are incurred, so the extension is installed by the user as an unpacked extension. Settings › Browser guides this.
- Consequences: users must enable Developer mode once to load the extension. A store listing is a later, owner-approved step. If the extension isn't connected, VoxStride falls back to its own isolated Edge window.
- Evidence: `ExtensionBridgeTests` (framing, real pipe + relay, refusals), `ExtensionEndToEndTests` (Windows CI: real Edge loads the extension → native host → pipe; observe, fill+submit and origin-mismatch refusal), installer checks (host manifest path and origin, registry for Chrome and Edge, removal on uninstall).

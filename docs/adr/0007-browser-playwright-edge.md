# ADR-0007: Browser automation with Playwright + installed Edge (dedicated profile)

- Status: accepted for P5a. P5b (MV3 extension + native messaging for the user's existing browser) is designed but not implemented.
- Decision: Microsoft.Playwright with `Channel="msedge"`, visible, persistent profile in `%LocalAppData%\VoxStride\BrowserProfile`. No browser download.
- Rationale: stable accessibility/DOM locators and no remote-debugging port exposure. User sign-in is manual.

# ADR-0006: Editor insertion via WM_SETTEXT to an exact HWND (no keystroke injection)

- Status: accepted
- Decision: For Notepad and Notepad++, open a new empty document (`notepad++ -multiInst -nosession`; new Notepad window), verify the control is empty, send `WM_SETTEXT` with `SendMessageTimeout(SMTO_ABORTIFHUNG)`, and verify with `WM_GETTEXT`.
- Rationale: immune to focus theft (U02), no clipboard use, and works with Scintilla, Edit and RichEdit. UIPI prevents writing to elevated editors, which is reported.
- Consequences: UIA/vision fallback is reserved for future app adapters.

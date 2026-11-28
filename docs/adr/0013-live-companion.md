# ADR-0013: Live companion — real-time voice with Gemini Live, authorized by the user's own words

- Status: accepted (2026-11-28, owner decision)
- Context: the owner tested 0.7.x and found the request/response voice pipeline impractical. Each step waited for
  the last: transcription took about 3 s per sentence, reasoning about 2.5 s per round, and TTS about 4 s. Saying
  "proceed" felt unnatural. They asked for the experience of a desktop AI companion: an on-screen character you just
  talk to, which talks back, sees the screen when needed, and does tasks ("check the weather in France and put it in
  an Excel sheet") without asking for confirmation.
- Decision:
  - **Transport:** Gemini Live (BidiGenerateContent over WebSocket) with the user's Gemini key.
    - Native audio in and out, input and output transcription, server-side turn detection and barge-in.
    - Google Search grounding, context-window compression, and session resumption on `goAway` or a dropped connection.
    - Model and tool-schema format are tried in order (newest live model first; JSON Schema, then OpenAPI), and the
      working combination is remembered.
  - **Audio:**
    - The microphone stays on the shared `AudioHub` (software gain, no Windows setting changed).
    - Audio is streamed only while local VAD hears speech (with 500 ms pre-roll), and `audioStreamEnd` is sent after
      a pause. Silence isn't uploaded.
    - Replies play through a waveOut stream that stops instantly on interruption.
    - "I use speakers" mode stops sending the mic while VoxStride talks.
  - **Authority (replaces the finish-phrase rule for this mode only):**
    - Every tool call is minted a `RunAuthorization` bound to the transcription of the user's own words in that turn
      (or their typed text).
    - With no user words, only read-only tools are allowed. The model can't act on its own initiative.
    - Every effect still goes through `ToolBroker`: schema, policy, blocked apps and sites, folder scope, journal and
      verification.
  - **Approvals (`LiveApproval`):**
    - Reversible changes the user asked for (moves and renames, which are undoable) go ahead.
    - Irreversible ones still ask once: recycle or delete, permanent cleanup, Send/Pay/Submit presses, form submits
      and coordinate clicks. The user says "yes" or "no", or clicks the bubble or the card.
    - The model never answers for the user.
  - **New tools:**
    - `files_create_document`: a dependency-free xlsx writer, plus csv, txt, md and html. It never overwrites and
      opens the file with its default app.
    - `screen_look`: one picture of the primary monitor, used per the user's choice of Ask each time, Whenever it
      needs to, or Never. It's sent as an untrusted frame and never saved.
    - `memory_remember` / `memory_forget`: facts kept in a local JSON file and given to the companion at session start.
  - **UI:** an original animated character, not copied from any product. It blinks, follows the cursor with its
    eyes, leans in while hearing you, looks up while thinking, moves its mouth with its own voice, and shows its
    state with headset status lights. It has a speech bubble and a talk bar (mic, text, menu), is always on top,
    can be dragged, and resizes with Ctrl + mouse wheel.
  - The classic dictation mode stays for users without a Gemini key.
- Consequences:
  - The conversation feels like a call: about a second to start replying, and you can interrupt it.
  - Gemini Live usage counts against the user's Gemini quota. The free tier has limits.
  - The audio goes to Google under the user's own key.
  - Echo with loud speakers is handled only by the speakers mode, not by acoustic echo cancellation.
- Evidence:
  - `LiveCompanionTests` (actions run without "proceed"; read-only without user words; spoken yes/no; screen frame
    before the tool response; barge-in; VAD gating; mute; resume after goAway; captions).
  - `GeminiLiveCodecTests` (setup, client messages, every server message type).
  - `XlsxTests`, `ScreenLookPolicyTests`, `CompanionMemoryTests`.
  - Live behaviour against Google's service needs the owner's key; it is not exercised in CI.

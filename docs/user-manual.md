# VoxStride user manual

## 1. First start
1. Run the installer (no administrator rights needed). VoxStride opens and places an icon in the system tray.
2. Read the welcome panel. Tick **Enable the microphone** to use voice, or choose **Typed commands only**.
3. Optional: open **Reasoning providers**, paste a Claude, Gemini or OpenAI API key, click **Save**, then **Test connection**. Without a key, the local commands still work: drive space, opening apps, organizing folders, temp cleanup and undo.

## 2. Giving an instruction
- **Voice:** say **"Hey VoxStride"** (or press **Ctrl+Alt+V**), speak naturally, and take as many pauses as you like. When you're finished, say **"VoxStride, execute"**, click **Proceed**, or press **Ctrl+Alt+V** again. Nothing runs before that.
- **Same breath:** "Hey VoxStride, how much space is left on C… VoxStride, execute" works too.
- **Typed:** type into the box on the Home page and press **Enter**.
- **Editing while you speak:** "scratch that" removes your last sentence, "replace that with …" replaces it, and "never mind" cancels. You can also correct the text in the box and press Run.
- If VoxStride isn't sure you meant to finish (for example, you dictated the word "execute"), it asks **"Ready to execute?"**. Click **Proceed** to confirm.

## 2a. If VoxStride doesn't understand your accent
Open **Voice**, tick **Use cloud speech recognition**, choose **Automatic** (or OpenAI/Gemini), click **Save**, and restart when asked. This needs an OpenAI or Gemini key under Reasoning providers. It handles accented English and mixed Urdu/English much better than the built-in Windows engine. Your voice is sent only while you're giving an instruction, never in standby. If it misses quiet speech, move **Mic sensitivity** to the left.
To finish you can also say **"VoxStride, chalo"** or **"VoxStride, karo"**. Small accent differences in "VoxStride, execute" are understood.

## 2b. A more natural voice
Under **Voice → How VoxStride sounds**, tick **Natural human-like voice** (needs an OpenAI key) and pick a voice. VoxStride replies conversationally in the language you speak. With an AI key you can also just chat or ask questions.

## 3. What it can do

| Say | What happens |
|---|---|
| "How much space is left on C?" | Reads the drive instantly on your PC (no internet). |
| "Organize my desktop, leave the zip files alone" | Builds an exact move plan and shows it for approval. Moves files into folders by type, never overwrites, and verifies every move. |
| "Undo that" | Moves files back if they haven't changed and their old spot is free. |
| "Clean up my temp files" | Measures old temporary files, asks before deleting, then reports the measured change in free space. |
| "Open Notepad++" | Opens the installed app and confirms its window appeared. |
| "Write a short paragraph about solar power in Notepad++" | Needs a provider. The text is generated, typed into a **new** empty document, and read back to verify. |
| "Search YouTube for lo-fi music and play the third video in the top row" | Needs a provider. Opens the VoxStride browser window (Microsoft Edge), skips ads and Shorts, and asks if the position is ambiguous. |
| "Open ChatGPT and draft a prompt asking for a weekly meal plan" | Needs a provider. Types the prompt into ChatGPT **without sending it**. You review it and press Enter yourself. You sign in yourself the first time. |

## 4. Approvals
Changes to your files show an approval window with the exact list of what will happen. Only a mouse click on **Approve** allows it. Voice, the Enter key and timeouts never approve. For desktop organizing, you can tick **Always allow reversible organizing of this folder**. You still give each command, nothing is ever deleted or overwritten under this rule, and you can revoke it under **Privacy & permissions**.

## 5. Stopping
**Ctrl+Alt+X**, the **Stop** button or the tray menu stops listening or stops the current task. No new action starts after Stop. Anything already completed is listed so you can undo it where possible. Locking the screen or sleeping the PC also stops the task.

## 6. Privacy
- Standby listening for "Hey VoxStride" happens on your PC. Audio is never recorded, stored or uploaded.
- Only your instruction *text* is sent to the provider you chose, and only when a task needs it.
- History keeps results, not your words, unless you turn on **Keep my instruction text**.
- **Mute** (Ctrl+Alt+M) closes the microphone. **Clear history** and **Revoke all** are under Privacy.
- API keys are stored in Windows Credential Manager. The uninstaller can remove them.
- **Diagnostics log**: VoxStride keeps a detailed log on your PC at `%LocalAppData%\VoxStride\logs` (five files of up to 5 MB each). It records what VoxStride did and why: microphone, speech, routing, AI requests, tools, updates and errors. It never contains audio or API keys. Keys are removed automatically, even from error messages. By default it also includes what you said and VoxStride's replies, because that is what makes recognition problems fixable. Turn off **Include what I say… in the diagnostics log** under Privacy to log only lengths. Words heard in standby (while waiting for "Hey VoxStride") are never logged.
- **Save diagnostics report** (About, or History) creates a zip with the logs, a PC/settings summary and run history. It contains no keys, audio or files, so you can send it for troubleshooting.

## 6a. Updates
VoxStride checks GitHub for new versions, verifies each download's checksum, and installs updates by itself when you aren't using it. Afterwards it restarts on the new version. You can turn this off or click **Check for updates** under **About & diagnostics**.

## 7. Troubleshooting
- **"No Windows speech recognizer is installed"**: go to Windows Settings › Time & language › Speech and add English speech recognition, or use typed commands.
- **Wake phrase not detected**: speak a little slower, use the hotkey, or check the microphone in Windows Sound settings.
- **VoxStride doesn't seem to hear you**: open **Voice**, pick the microphone you speak into, and click **Test speech**. It listens for 6 seconds with the real speech engine and shows the words it understood. It also tells you whether the microphone sent no audio, only silence, or sound the engine couldn't understand. A new microphone choice applies when you click **Save**, without a restart.
- **The words are wrong**: the offline Windows engine is weak with non-native accents. Cloud speech (an OpenAI or Gemini key plus **Use cloud speech recognition**) is far more accurate. A Claude key can't transcribe audio. Windows online dictation (Win+H) isn't available to installer-based apps like VoxStride.
- **Provider errors**: "rejected the API key" means re-enter the key. "rate-limiting" means wait a moment. "model isn't available" means choose a model from the list and click Test connection.
- **Browser tasks**: Microsoft Edge must be installed (it ships with Windows 11). Sign in to ChatGPT inside the VoxStride browser window.
- **Diagnostics**: see About & diagnostics for timings, memory and configuration. **Export redacted diagnostics** writes a file without instructions, paths, keys or audio.

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
- If VoxStride isn't sure you meant to finish (for example, you said only "proceed" or "execute"), it asks **"Ready to execute?"**. Answer **"yes"** (or "proceed", "go ahead", "haan", "chalo") or click **Proceed**.

## 2a. If VoxStride doesn't understand your accent
Open **Voice**, tick **Use cloud speech recognition**, choose **Automatic** (or OpenAI/Gemini), click **Save**, and restart when asked. This needs an OpenAI or Gemini key under Reasoning providers. It handles accented English and mixed Urdu/English much better than the built-in Windows engine. Your voice is sent only while you're giving an instruction, never in standby. If it misses quiet speech, move **Mic sensitivity** to the left.
To finish you can also say **"VoxStride, chalo"** or **"VoxStride, karo"**. Small accent differences in "VoxStride, execute" are understood.

## 2b. A more natural voice
Under **Voice → How VoxStride sounds**, tick **Natural human-like voice** and pick a voice. This needs a Gemini key (voices such as Sulafat, Achird or Aoede) or an OpenAI key. Without either, VoxStride uses the built-in Windows voice. Greetings, small talk and "what do you think about…" questions get a real, human-style answer. For back-and-forth talking without saying "VoxStride, execute" each time, turn on **Natural mode** (see 3d). VoxStride replies conversationally in the language you speak. With an AI key you can also just chat or ask questions.

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

## 2a. Your desktop companion (live conversation)
With a Gemini key, VoxStride appears as a character on your desktop and you just talk to it, like a call:
- **Start**: it starts listening when VoxStride starts. Otherwise click the mic on its bar, press Ctrl+Alt+V, or say "Hey VoxStride".
- **Talk normally.** It replies straight away, and you can interrupt it by talking. There's no "proceed".
- **What the character shows**:
  - Green headset lights and leaning in: it's hearing you.
  - Amber lights and looking up: it's thinking or working.
  - Blue lights and a moving mouth: it's talking.
  - Red mic: muted.
- **Type instead**: write in the bar and press Enter.
- **Before anything that can't be undone** (deleting, sending, paying, submitting), it asks. Say "yes" or "no", or click.
- **Screen**: it looks at your screen when you ask about something on it. Choose Ask each time, Whenever it needs to, or Never under Settings › Permissions.
- **Memory**: say "remember that…" and "forget…". Settings › Voice › Forget everything clears it.
- **Speakers instead of a headset**: right-click the character › "I'm using speakers" so it doesn't hear itself.
- **Move or resize**: drag the character; resize with Ctrl + mouse wheel. Hide it from its menu, and bring it back from the tray icon › Show companion.
- **Characters**: right-click the character › Character, or go to Settings › Voice. There are six characters, each with its own voice and personality, or you can use your own picture.
- **Twitch co-host**: Settings › Voice › tick Twitch co-host and enter your channel. Chat is read anonymously, viewers can't make it act on your PC, and it never posts in chat.
- **Screen comments**: tick "Glance at my screen now and then" (needs "Looking at my screen: whenever it needs to").
- **Memory**: Settings › Voice lists what it remembers. Select an item and click Forget selected.
- **Offline AI**: install Ollama, run `ollama pull qwen3:8b`, then under Reasoning providers › Offline AI click Save and make it the active provider. Without a Gemini key, the character uses the classic voice mode.
- **Cost**: live conversation uses your Gemini account. The free tier has limits. Audio is sent only while you're speaking.

## 3a. Files anywhere you allow
VoxStride can list, search, create folders, move, rename, copy and recycle files, but only inside the folders you allow. By default that's Desktop, Documents, Downloads, Pictures, Music and Videos. Add or remove folders under **Settings › Permissions**. Every change shows the exact list for approval first. "Undo that" puts things back. Deleting always goes to the Recycle Bin.

## 3b. Controlling other apps
With an AI key, VoxStride can read and operate the windows of other apps: "in Calculator press 7 plus 5 equals", "fill the name field in this form with Ali". It reads the app's controls through Windows accessibility, so nothing is guessed from pixels.
- It never touches Windows security prompts, sign-in screens, password boxes, VoxStride itself or apps you've blocked under **Permissions › Blocked apps**.
- Buttons like Send, Delete, Pay or Submit, and any click at a screen position, need your click on **Approve**.
- If an app's controls can't be read, VoxStride may ask to **take one screenshot of that window** to look at it. Approve it each time, or set **Screenshots** to *Never* under Permissions. Screenshots are never saved.
- If you switch windows or move the mouse while it is typing, it stops.
- Apps running as administrator can't be controlled. VoxStride tells you instead.

## 3c. Working in your own Chrome or Edge
By default, web tasks run in VoxStride's own Edge window. To let VoxStride work in the browser you already use, where you're signed in:
1. Open **Settings › Browser** and click **Show extension folder**.
2. In Chrome go to `chrome://extensions` (in Edge `edge://extensions`), turn on **Developer mode**, click **Load unpacked** and pick that folder.
3. Tick **Use my browser** in Settings › Browser. The VoxStride icon in the toolbar turns green when connected.
4. On each site you want VoxStride to use, click the VoxStride icon › **Allow on this site**. The browser asks you to confirm.

VoxStride acts only in the active tab, never reads passwords, cookies or other tabs, and asks before submitting forms or pressing buttons like Buy or Send. Block sites under **Permissions › Blocked sites**. Text on web pages is treated as information, never as instructions.

## 3d. Natural conversation, hold-to-talk and pause
- **Natural mode** (Voice settings, off by default): after "Hey VoxStride", a pause of about 1.5 seconds ends your turn. Natural turns can only **answer and look things up**. To change anything, say **"VoxStride, execute"** afterwards, or finish with it as usual.
- **Hold-to-talk** (Voice › Talk key): hold **Ctrl+Alt+V** while speaking. Releasing it sends the instruction.
- **Pause**: **Ctrl+Alt+P** or the **Pause** button holds a running task before its next step. Press again to continue, or Stop to cancel.
- **Backup provider** (Reasoning providers): if your main AI provider is down before a task has done anything, VoxStride uses the backup instead.
- While VoxStride is speaking, it ignores what the microphone hears so it doesn't react to its own voice. This is not full echo cancellation. With loud speakers, use headphones or lower the volume.

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
- **Quiet microphone**: VoxStride boosts quiet microphones by itself (inside the app, without changing any Windows setting). If it still misses words, move the microphone closer to your mouth.
- **New key, new engine**: saving an OpenAI or Gemini key, or changing the speech engine, takes effect at once. No restart is needed. If you're in the middle of an instruction, the new engine starts with the next one.
- **The words are wrong**: the offline Windows engine is weak with non-native accents. Cloud speech (an OpenAI or Gemini key plus **Use cloud speech recognition**) is far more accurate. A Claude key can't transcribe audio. Windows online dictation (Win+H) isn't available to installer-based apps like VoxStride.
- **Provider errors**: "rejected the API key" means re-enter the key. "rate-limiting" means wait a moment. "model isn't available" means choose a model from the list and click Test connection.
- **Browser tasks**: Microsoft Edge must be installed (it ships with Windows 11). Sign in to ChatGPT inside the VoxStride browser window, or use the extension (3c). If the toolbar icon stays grey, reopen the browser after installing VoxStride, and check that **Use my browser** is ticked.
- **Setup again**: Settings › About › **Run setup again** reopens the 4-step setup (microphone, AI key, folders, browser).
- **Diagnostics**: see About & diagnostics for timings, memory and configuration. **Export redacted diagnostics** writes a file without instructions, paths, keys or audio.

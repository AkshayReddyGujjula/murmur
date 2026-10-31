# Live feasibility check: awaiting separate user approval

The approved code plan does not itself authorize these actions. Do not run the
commands below until the user approves app/login/microphone testing. These are
diagnostics for a single owned Claude Code child, not the finished tray app.

## What would run

1. `build\Murmur.Probe.exe --login` starts the installed Claude CLI's normal
   `auth login` flow with `CLAUDE_CONFIG_DIR` pointing at a new Murmur-owned profile
   under `%LOCALAPPDATA%\Murmur\feasibility-v1\profile`. It may open a browser and
   requires the user's sign-in. It does not read/copy existing Claude credentials.
   The effect of any CLI-wide state kept outside `CLAUDE_CONFIG_DIR` remains a
   verification gate; Murmur itself never reads or copies the original profile.
2. `build\Murmur.Probe.exe --probe` creates a dedicated local pseudoterminal and
   new Claude CLI child with a unique session ID, English hold-mode voice settings,
   `autoSubmit: false`, no agent tools and no MCP servers. Its input goes only to
   that child. No existing Claude window/terminal/session is enumerated or attached.
3. The user answers the owned child's initial setup prompts, then deliberately
   starts and stops recording. The harness displays only requested snapshots of
   the owned terminal, and eventually the exported editor buffer for comparison.
   It does not register Ctrl+Space or write the clipboard yet.

The harness attempts to remove its exact handoff/settings files after the owned
child is disposed. It keeps the isolated authentication profile, and reports when
other session temp files remain. It does not recursively purge CLI data.

## Exact diagnostic controls

- **V**: display a diagnostic snapshot of the owned child's terminal. Snapshots
  can include dictated text. No raw terminal control sequences are forwarded.
- Before the first **R** only, **Enter/Up/Down** can answer setup prompts in that
  child. Enter is permanently disabled after R for the lifetime of this probe.
- **R**: begin Space repeats to request hold-mode dictation. Inspect with V to
  verify actual listening; sending repeats is not proof that recording started.
- **S**: stop repeats to finalize. Use V and wait for the actual finalized prompt.
- **E**: request Ctrl+G external-editor handoff. It displays the returned text and
  exits. A nonempty buffer alone is not proof of accurate or complete transcription.
- **C**: cancel/discard; **Q**: quit. Only the owned child/job is cleaned up.

## Pass/fail evidence needed

1. A short English take of at least three words remains unsent, exports exactly,
   and produces no agent response. Include punctuation and a term such as OAuth.
2. A separate seven-minute take retains both its opening and closing sentences.
   Establish whether hold mode remains active across speech pauses; do not infer
   this from the fake seven-minute control test.
3. Cancel while recording and while finalizing. No export should be accepted.
4. An unexpected child exit/export timeout returns a clear failure and cleans up
   only the owned process tree, without changing the user's original Claude session.
5. Measure readiness delay and actual memory/CPU of the probe and its owned child
   tree. Executable file size is not a RAM measurement. Whole tray-app idle/focus/
   clipboard/waveform acceptance is a later gate after this backend is proven.

If the key-repeat, external editor, seven-minute duration, or no-submit checks
fail, stop and report the observed behavior before implementing the tray app.
No paid API, local speech model, credential scraping or conversational voice
substitution is an approved fallback.

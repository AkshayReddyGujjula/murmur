# Murmur

A tiny Windows tray dictation app. **Ctrl+Space, speak, Ctrl+Space, paste.**

Speech-to-text is done by the voice dictation already included in Claude Code. Murmur
runs its own hidden Claude Code CLI child, with a separate profile, in hold-to-talk mode.
It never submits your words as a prompt and never touches your other Claude sessions.
No speech API keys and no local speech model.

## Run it

```powershell
node scripts/build.mjs build     # builds build\Murmur.exe (does not launch it)
.\build\Murmur.exe               # lives in the tray (^ hidden icons area)
```

An explicit launch briefly shows the shortcut in a small pill, then leaves the app in
the tray. Opening it again asks the running instance to show its status. Windows login
launches use `--background` to stay quiet. If startup fails, an error dialog points to
`data\app\startup-error.txt` or a fallback diagnostics location.

Murmur keeps its data beside the exe in `build\data\`:
- `feasibility-v1\profile`: Murmur's own Claude sign-in. It is separate from your normal Claude.
- `app\settings.ini`, `app\history.dat` (DPAPI-encrypted for your Windows user), `app\murmur.log`.
  Transcript text is never written to the log.

## Using it

| Action | What happens |
| --- | --- |
| **Ctrl+Space** | Small black pill appears at the bottom of the screen. Dots mean Claude is getting ready; the live waveform means it is listening, so speak. |
| **Ctrl+Space** again | Pill shimmers while Claude finalizes, then shows **Copied**. Paste anywhere with Ctrl+V. |
| **Esc** while recording | Cancels and discards the take. |
| Click the tray icon | Menu: status, **Recent** (click any entry to copy it again), shortcut, engine mode, options, account, logs, quit. |

Focus never moves: the pill is click-through and never activates, so the window you were
typing in keeps the keyboard.

### Menu options

- **Shortcut**: Ctrl+Space, Ctrl+Shift+Space, Ctrl+Alt+Space or Alt+Shift+Space.
- **Claude engine**:
  - *Always ready* (default): keeps the Claude CLI child resident to reduce startup delay.
  - *Sleep after 15 min idle*.
  - *Start on demand*: least memory, but each take waits a few seconds for Claude to start.
- **Options**: overlay at bottom or top, sounds, Esc cancels, paste automatically after copy
  (off by default), keep recent history, start with Windows.
- **Account**: sign in to Claude, or run Claude's one-time setup in a console window.
- **Open logs folder**.

If another app owns your shortcut, the menu reports the conflict and offers **Start
dictation**, **Stop dictation**, and **Cancel dictation**. Your shortcut preference is
preserved. Optional auto-paste is skipped if focus changes, the clipboard changes, or
a modifier key is held; ordinary copy-and-paste remains the default.

## Development

Windows x64 with the in-box .NET Framework C# compiler, and Node.js only for build/test
orchestration. Nothing is installed or downloaded.

```powershell
node scripts/build.mjs test                 # all offline checks
node scripts/build.mjs test-group Engine    # Engine | Core | Audio | Ui | Startup | Wiring
```

Layout: `src/App/Engine` (drives the owned Claude child: screen parsing, hold, export),
`src/App/Audio` (waveIn level meter for the waveform only), `src/App/Ui` (layered overlay,
tray menu, logo), `src/App/Core` (settings, history, log, hotkeys, clipboard, chimes),
`src/App/Program.cs` (wiring). The backend in `src/Transport`, `src/Handoff` and
`src/Probe` is the feasibility harness that was proven live (see `docs/LIVE-RESULTS.md`).
Module contracts: `docs/APP-CONTRACTS.md`. Acceptance checklist: `docs/APP-CHECKS.md`.
Launch repair and regression evidence: `docs/REPAIR-2026-11-03.md`. Offline checks do
not establish live speech accuracy, seven-minute capture, or final idle resource use.

## Local recovery

The legacy implementation is preserved on `archive/pre-rebuild-20261031`, commit `47ffa84`.

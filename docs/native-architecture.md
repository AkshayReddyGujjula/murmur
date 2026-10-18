# Murmur — final native architecture

Status: implemented and packaged. This supersedes the earlier D2D/Electron proposal.

## Final shape

```
┌────────────────────────────────────────────────────────────┐
│ Murmur.exe — Rust, one UI process                           │
│                                                            │
│  eframe + egui          wgpu / Direct3D 12                  │
│  ├─ HUD / timer / bars  └─ D3D12 swap chain                 │
│  ├─ settings / onboarding                                   │
│  ├─ tray icon + menu    Windows APIs                        │
│  ├─ global hotkey       RegisterHotKey                      │
│  ├─ window state        SetWindowLong / SetWindowPos        │
│  └─ clipboard           arboard                             │
│                                                            │
│              spawns for each take (stdin/stdout protocol)   │
│                            │                               │
│                     ClaudeBridge.exe                       │
│                     .NET Framework 4.8, ~23 KB              │
│                     Win32 FindWindow / SetLayered...        │
│                     SendInput / clipboard                   │
└────────────────────────────────────────────────────────────┘
```

- **No Electron, Node, WebView2, Chromium, Python, or local STT model** in the
  running app.
- Release shell: 7.9 MB standalone executable; 4.8 MB portable zip including docs.
- Measured with wgpu's **WARP software adapter** in the agent session: the full
  app was ~350 MB working set. A minimal wgpu hello-window was ~370 MB, showing
  the cost is the software D3D12 runtime, not Murmur's state. Hardware D3D12
  should be lighter; a CPU-rendered backend is the next optimization if
  guaranteed low memory on GPU-less machines is required.
- The only helper is the tiny C# Win32 bridge. It is spawned for a take and
  exits; it is not a resident process.
- All app state lives in `%APPDATA%\Murmur`; logs in `%LOCALAPPDATA%\Murmur`.

## Why these choices

### Rust shell

The app is a small always-on-top HUD with a global hotkey, a tray, and Win32
automation. Rust gives:

- one self-contained executable, no runtime install
- predictable memory and no browser process tree
- direct Win32/DWM access
- safe state handling around a potentially destructive operation (hiding another
  app's window)

The portable GNU Rust toolchain plus `rust-lld` means no Visual Studio/MSVC
installation is needed to build it.

### egui instead of hand-written Direct2D

The earlier design proposed Win32 + Direct2D. Direct2D would still be the
smallest possible renderer, but it would require a bespoke layout, text
wrapping, scrolling, hover/press handling, animation, and font pipeline. The
egui version reached the intended instrument look quickly and keeps the code
readable. D2D remains a future optimization if the last few MB matter.

### wgpu / Direct3D 12 instead of OpenGL

Eframe's default OpenGL backend produced a blank, unpainted window in the test
session even for a minimal hello-world. A minimal wgpu test rendered correctly
through D3D12. The shipping binary therefore uses `eframe::Renderer::Wgpu` and
defaults `WGPU_BACKEND=dx12` unless overridden.

This is the one place the final app is heavier than the original plan; wgpu
adds code size for a reliable Windows renderer. It is still a single native
process, not a browser.

### Click-based calibration, not UIA

The preferred design was `IUIAutomation` + `InvokePattern` on Claude's mic.
Read-only probing showed only the bare Chromium `Chrome_WidgetWin_1` and an
intermediate D3D pane; the renderer accessibility tree did not expose mic or
composer nodes. Restarting Claude with accessibility flags was explicitly out
of scope. The shipped fallback is the reversible click path:

1. Calibrate the mic and composer once, with F8 and the cursor.
2. Normalize the points to the Claude client area.
3. Alpha-ghost Claude, make it foreground invisibly, click the points.
4. Set `WS_EX_TRANSPARENT` while listening so clicks outside Murmur fall through.
5. Stop, copy, and restore.

This is why the app asks for two points and why layout changes in Claude require
recalibration.

## Take lifecycle

1. `Ctrl+Space` → `BEGIN`.
2. Find Claude's main window (largest process-owned `Chrome_WidgetWin_1`,
   excluding tool windows and tiny popovers).
3. Save placement, foreground window, extended style, and iconic state.
4. Set alpha-layered invisible, restore/activate invisibly.
5. Click composer focus point, click mic point.
6. Show Murmur with `WS_EX_NOACTIVATE`; keep the mouse click-through flag on
   Claude.
7. Second `Ctrl+Space` → remove click-through, click mic again.
8. Wait for Claude to finalize, focus composer, Ctrl+A/C, poll for stable text.
9. Clear composer if enabled; restore Claude's window state and foreground.
10. Show the result, focus the overlay, write clipboard, and store duration and
    word count.

The bridge writes a restore-state file before touching Claude. If the Rust
process dies, the bridge sees stdin EOF, sends Escape to cancel dictation, and
restores the window. If the bridge dies, the settings screen can invoke a new
bridge's `RESTORE` command.

## Testing notes

- All UI states were rendered and inspected at 760×520 logical / 1330×910 at
  175% DPI: setup, idle, listening, transcribing, ready, error, settings, and
  the calibration card.
- The native bridge was probed read-only (`PING`, `FIND`, window discovery) and
  selected the hidden Claude main window rather than the small minimized popover.
- A mock bridge exercised the complete session worker end-to-end: global
  `Ctrl+Space` → `BEGIN` → listening → `Ctrl+Space` → `END` → transcribing →
  ready, with the transcript on the clipboard. This caught a real parser bug:
  the `TEXT text=…` field was skipped, so every take would have looked empty.
  The parser is fixed, three unit tests cover the `TEXT`/`EMPTY` forms, and the
  final release build also completed a hotkey-driven mock take with the
  transcript on the clipboard.
- A real-bridge focus test against a ghosted Claude window showed that Windows
  would not reveal it. The bridge now refuses to click in that state and returns
  `not-showable`; calibration shows an error instead of a card over an invisible
  window. A failed focus restores the saved state.
- The final live mic click/dictation test is intentionally left to the user:
  it requires speaking into their microphone and temporarily bringing Claude
  forward for calibration. The 5pm checklist is in `docs/TESTING.md`.
- The release build must be launched outside the agent sandbox for a normal
  tray/hotkey lifecycle; the sandbox can terminate detached GUI children between
  tool calls.

## Remaining risks

- Claude can change its DOM/layout; recalibrate.
- Claude may update its window classes or permissions; the bridge fails closed
  with a visible error.
- Elevated/different-session Claude blocks `SendInput`.
- wgpu requires D3D12. The backend can be overridden with `WGPU_BACKEND`.
- The app is unsigned.

## File ownership

- `app/` — Rust shell, UI, state machine.
- `native/ClaudeBridge/` — C# Win32 bridge.
- `resources/bin/` — built bridge (ignored).
- `scripts/` — bootstrap, build, run, preview, capture.
- `docs/` — this note and screenshots.

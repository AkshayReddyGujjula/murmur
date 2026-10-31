# Tray app acceptance checks (user-run)

Build first: `node scripts/build.mjs build`. Close any running `Murmur.Probe.exe`. Then run
`build\Murmur.exe`. It should show a black waveform icon in the tray (^ hidden icons).

1. **Startup**: hover the tray icon after about 10 s. The tooltip should say *Murmur — Ready*.
2. **Short take**: click into Notepad, press Ctrl+Space. Dots appear, then the live waveform
   (the bars should move with your voice). Say a sentence. Press Ctrl+Space again. The
   shimmer shows, then *Copied*. Ctrl+V in Notepad pastes the sentence. Notepad keeps focus
   throughout.
3. **Second take straight after**: same again. The text must be only the new sentence
   (no leftovers from take 1).
4. **Cancel**: Ctrl+Space, speak, press Esc. The pill disappears and the clipboard is unchanged.
5. **Cancel while starting**: Ctrl+Space then immediately Ctrl+Space again before the waveform.
   The pill disappears; nothing is copied.
6. **No speech**: Ctrl+Space, stay silent 3 s, Ctrl+Space. You should see *No speech detected*.
7. **Recent**: tray menu → Recent lists the takes; clicking one copies it again.
8. **Long take**: 2–7 minutes; opening and closing sentences both present.
9. **Options**: switch the overlay to top, toggle sounds, and change the shortcut. Each takes
   effect immediately and survives a restart.
10. **Resources**: Task Manager while idle. Murmur.exe should be ~0% CPU. Note its memory and
    the claude.exe child's memory.
11. **Quit**: tray → Quit. Murmur.exe and its claude.exe child both disappear.

Report anything odd with the last lines of `build\data\app\murmur.log` (tray → Open logs folder).

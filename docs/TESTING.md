# Murmur — 5pm acceptance test

This is the test pass that needs you at the keyboard and microphone. Budget **10–15 minutes**;
the must-pass path is the first three sections.

The app is at:

```
C:\Users\aksha\Desktop\GitHub\STT App\release\Murmur\Murmur.exe
```

---

## What I could already verify without your voice

- Every native screen renders at 760×520 / 175% DPI: setup, idle, listening,
  transcribing, ready, error, settings, calibration.
- A mock bridge exercised the complete session worker:
  `Ctrl+Space` → `BEGIN` → listening → `Ctrl+Space` → `END` → transcribing →
  ready, with the transcript on the system clipboard.
- `CLICKTHROUGH 1/0`, `BEGIN`, `END`, and `QUIT` were issued in the right order.
- A real parser bug was found and fixed with that mock: the app was skipping the
  `text=` field and would have shown “Claude didn’t return any text” on every
  take. Three unit tests now cover `TEXT`/`EMPTY` parsing, and the final release
  hotkey test copied the mock transcript successfully.
- The global `Ctrl+Space` hotkey starts and stops a take.
- If Claude is only a hidden/ghost window, the hardened bridge now refuses to
  reveal it and returns a clear error instead of clicking blind.
- The calibration restore path now saves a restore record before revealing
  Claude, and the app restores Claude’s previous minimized/visible state.

Still unverified because it needs your microphone and a real Claude window:
the actual mic click, Claude’s live transcription, the real composer copy,
and restoring the real Claude window after a take.

---

## Before you start

- [ ] Open the **Claude desktop app fully**. Leave its main window visible on a
      Claude Code chat, with the composer and mic button showing.
      Do not start from a tray-only or hidden Claude; Murmur deliberately
      refuses to click an unshowable window.
- [ ] Click into Claude’s composer once and confirm the mic button is visible.
- [ ] Make sure Claude’s microphone permission is granted and the account is
      signed in.
- [ ] Run `release\Murmur\Murmur.exe`.
- [ ] If you see **“Murmur is already running”**, close that box and use the
      existing tray icon/hotkey. Do not launch a second copy.
- [ ] Close any other utility that owns `Ctrl+Space`. Murmur’s setup screen
      should say **CLAUDE / READY** and **WINDOW FOUND** (not BACKGROUND).
      If it says **CLAUDE / BACKGROUND**, open Claude fully, then restart Murmur
      or click **Open Claude** and try calibration again.
- [ ] Keep Notepad open to paste the clipboard.
- [ ] Optional: the logs are at `%LOCALAPPDATA%\Murmur\murmur.log`.

If the setup screen says **Ctrl+Space unavailable**, open Settings and select
**Ctrl+Alt+Space**, then use that shortcut everywhere below.

---

## 1. Calibration (must pass)

- [ ] Click **Calibrate Claude**.
- [ ] Claude should come forward and Murmur should shrink to the calibration card.
- [ ] Hover the **centre of Claude’s mic button**, then press **F8**.
      Do not click. The card should move to “Step 2 of 2”.
- [ ] Hover the **centre of Claude’s composer text area**, then press **F8**.
- [ ] The card should close. Claude should return to its previous
      minimized/visible state and Murmur should show **Idle**.
- [ ] Open Settings and note the two calibration percentages.

**If Claude never appears:** close the card, open Claude manually so its window
is visible, restart Murmur, and retry. If it still fails, send me the log and a
screenshot of the error. This is the known ghost-window limitation, not a
calibration crash.

---

## 2. First real voice take (must pass)

- [ ] Press **Ctrl+Space**. The listening screen should appear and Claude
      should disappear.
- [ ] Say clearly:

      “Hello Murmur. This is a test of dictation, with a comma, and a question mark?”

- [ ] Wait one second after speaking, then press **Ctrl+Space** again.
- [ ] The transcribing checklist should appear, then **Your prompt is ready.**
- [ ] Paste into Notepad. The transcript should be there.
- [ ] Check that the word count is plausible and the take time is non-zero.
- [ ] Check that Claude’s composer is empty (if “Clear Claude input after copy”
      is on, which is the default).

**If it says “Claude didn’t return any text”:** this is the exact failure the
mock test was designed to catch; the parser is fixed, so now it means the real
mic/composer clicks did not land, or Claude was not visible. Capture the screen,
the calibration percentages, and the last 40 log lines, then recalibrate.
Do not keep retrying blindly.

**If the transcript is partial or wrong:** that is Claude’s STT, not Murmur.
Compare with dictating directly into Claude once to see whether it is a mic or
Claude issue.

---

## 3. Repeat, cancel, and clipboard (must pass)

- [ ] From Ready, press **Ctrl+Space** again. Say “Second take.” and stop.
      Verify the new text replaces the old text; it should not append.
- [ ] Overwrite the clipboard with some other text. In Murmur, click
      **Copy Again**. Paste; the transcript should return.
- [ ] Start a take, then click **Cancel**. Murmur hides itself, Claude should
      be restored, and no text should be copied. Press the hotkey or use the
      tray icon to bring Murmur back.
- [ ] Start a take, then **quit Murmur from the tray while listening**.
      Claude should be restored and dictation cancelled. Relaunch Murmur and
      confirm it still works.

---

## 4. Real-world wording and punctuation

- [ ] Dictate a longer technical prompt, including some of:
      `OAuth`, `JSON`, `localhost:3000`, `refactor the auth middleware`,
      “first, second, and third”, a question mark, and a period.
- [ ] Verify the terms and punctuation. This is the best check that the real
      composer copy is capturing the final text rather than a partial stream.

---

## 5. Claude window states

- [ ] Minimize Claude, then press the hotkey. It should restore, dictate, and
      return to minimized.
- [ ] Put another app in front of Claude, then press the hotkey. It should still
      work; Murmur makes Claude temporarily invisible but foreground.
- [ ] While listening, click another app. Input should go to that app, not
      Claude (click-through). Claude must not receive stray clicks.
- [ ] Move Claude to a different monitor or resize it, then try a take.
      If the mic/composer moved, recalibrate.
- [ ] Hide Claude to the tray (if your build allows it), then press the hotkey.
      Expected: a clear “Claude’s window exists, but Windows would not bring it
      forward” error, no clicks. Open Claude and retry.

---

## 6. Settings and shortcut

- [ ] Open Settings → click **Ctrl + Alt + Space** → close → test that shortcut
      for one take. Switch back to Ctrl+Space afterward.
- [ ] Toggle **Clear Claude input after copy** off. Put a short draft in Claude’s
      composer, dictate, and verify the copied text includes what you expect.
      Turn it back on.
- [ ] Toggle **Minimize Claude after every take** and verify the behavior.
- [ ] Toggle **Start Murmur with Windows** on, then off. If you want to leave it
      on, verify it after a reboot later; don’t leave it on accidentally.
- [ ] Click **Open Logs**; confirm the folder opens.
- [ ] Click **Restore Claude** after a deliberate failed/cancelled reveal and
      confirm Claude returns to normal. This consumes the restore file.

---

## 7. Tray and single instance

- [ ] Tray menu: **Show Murmur**, **Start a take**, **Settings**, **Quit**.
- [ ] Launch `Murmur.exe` a second time. The “already running” box should appear;
      close it. This is expected.
- [ ] Quit from the tray, relaunch, and press the hotkey. It should work again.

---

## 8. Failure paths (short)

- [ ] Fully close Claude, press the hotkey. Expect a clear error and no clicks.
      Open Claude and retry.
- [ ] Calibrate while Claude is hidden/tray-only. Expect the “not showable”
      error, not a calibration card over an invisible window.
- [ ] If Ctrl+Space is taken by an IME, verify Settings switches to
      Ctrl+Alt+Space and the warning clears.

---

## What to send me if something fails

Please include:

1. Which test number failed.
2. A screenshot of the Murmur state.
3. The last ~40 lines of `%LOCALAPPDATA%\Murmur\murmur.log`.
4. Whether Claude was visible, minimized, or tray-hidden at the time.
5. The two calibration percentages from Settings.
6. The exact error text.
7. If possible, a 30–60 second screen recording of one take.

The log does **not** contain the dictated text; it contains window/status lines
only. If you are testing sensitive content, avoid sending the transcript itself.

---

## Minimum pass for 5pm

If you only have two minutes, do these three:

1. **Calibrate** with Claude visible.
2. **One short take** and paste the clipboard.
3. **One repeat take** or **Cancel**, and confirm Claude comes back.

If those three pass, the core product works. Everything else is edge coverage.

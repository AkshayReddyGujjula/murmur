# Tray and automatic paste repair, 3 November 2026

## Proven cause and runtime repair

The workspace and its compiled executables carry an inherited Low Mandatory Level
label. Shell registration from that location failed for WinForms NotifyIcon, a native
standard Windows icon, and a native Murmur icon. Native NIM_ADD returned false with
Win32 error 5. Repeating the same diagnostic from a freshly written executable in
`%LOCALAPPDATA%\Murmur\diagnostics` succeeded for all three icons, including their
Shell_NotifyIconGetRect queries. This establishes a process-integrity problem rather
than an icon drawing problem or an overflow-panel assumption.

`scripts/install.mjs` writes a fresh executable into `%LOCALAPPDATA%\Murmur` and
creates the per-user Start menu shortcut. It refuses to update a running copy. It
keeps the existing data at `build\data` through `data-location.txt`, retaining the
owned profile's existing workspace setup. It does not edit trust or security settings.
Direct Low-integrity app launches now explain the installation command instead of
silently running without a usable tray or paste capability.

The normal launch also exposed inherited redirected standard handles reaching the
ConPTY child. Claude exited with the non-interactive --print input error. Explicit
STARTF_USESTDHANDLES with null handles lets ConPTY initialize them. The native smoke
test fails without that line and passes with it; only its own test child is launched.

References: [Mandatory Integrity Control](https://learn.microsoft.com/en-us/windows/win32/secauthz/mandatory-integrity-control),
[Microsoft terminal redirected-handle discussion](https://github.com/microsoft/terminal/discussions/15814),
[SendInput integrity limits](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).

## Automatic paste behavior

- Main tray menu: **Auto paste**, checked when enabled, off by default.
- Options > Paste destination: **Window focused when transcription finishes**
  (default), or **Window where dictation started**. Both preferences persist.
- Paste is attempted immediately after copying. Only a held modifier causes a
  retry, at 25 ms timer intervals for at most one second. Other guard failures
  terminate delivery. A delivery cannot send twice.
- Focus/clipboard changes, a new take, shutdown, disabling Auto paste, or changing
  destination cancel pending delivery. Failure leaves the transcript copied and
  shows **Copied - press Ctrl+V**. Successful input injection shows **Pasted**.
- This follows the existing Ctrl+Space stop/finalization flow. It does not add
  silence detection or press Enter. Windows can block input into elevated apps.

## Verification

- `node scripts/build.mjs test`: passed all groups; engine 415 assertions, UI 239,
  startup 61. Existing handoff reparse-point check remains skipped because link
  creation is unavailable; its chain check is by inspection.
- `node scripts/test-conpty.mjs`: passed. Before-fix variant failed with both child
  standard handles reporting non-console; the repaired variant reports both true.
- `node scripts/build.mjs build`: passed; installed executable hash equals build.
- Installed runtime check: integrity 8192 (Medium), Engine Ready. Actual process
  tray query returned S_OK and nonzero bounds. Core and Wiring checks were rerun
  after adding the late-UI-timer deadline regression, followed by rebuild/install.
- Start menu shortcut target verified as `%LOCALAPPDATA%\Murmur\Murmur.exe`.
- Desktop helper unavailable (native pipe missing). No visual menu acceptance or
  real speech-to-paste delivery was tested. No microphone or manual input injection
  into user applications was performed. The native ConPTY test uses only its child.

This is the historical tray-repair checkpoint. The user subsequently confirmed the
live desktop flow worked and approved publication. Current UI and package details
are in README.md; development data and temporary diagnostics are excluded from releases.

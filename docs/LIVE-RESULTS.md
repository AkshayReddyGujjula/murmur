# Live feasibility evidence

User approved the isolated login/probe and microphone checks on 2026-10-31.
The existing Claude session and clipboard remain off limits.

## Initial launch

- `build\Murmur.Probe.exe --login` exited before starting Claude because creation
  of `%LOCALAPPDATA%\Murmur\feasibility-v1` was denied.
- The owned root did not exist afterwards. No browser or recording was started.
- Changed the default data directory to `data\feasibility-v1` beside the probe
  executable, under the ignored build directory. No permissions or system settings
  were changed. Added an offline assertion for the portable default root.
- All seven offline test groups and the build passed after the change; the existing
  reparse-point fixture remains skipped.

## Portable login

- The next `--login` launch created its separate portable profile and started
  Claude's normal browser sign-in flow.
- The CLI's automatic browser launch did not show a page to the user. Cancelled
  only the owned login flow and opened its fresh authorization link in a new
  T3 Browser tab. Verified the Claude sign-in page loaded. The user must complete
  browser authorization.
- No credentials are copied from the user's existing Claude profile, read into
  chat or committed. Authorization links/codes are not written to repository docs.
- Browser authorization completed. The displayed one-time code was transferred
  directly to the matching owned login process, without the clipboard. The CLI
  returned `Login successful.` and exit 0. The sign-in tab was closed afterwards.

## Native startup and readiness

- `--probe` successfully created its dedicated ConPTY child in 23 ms. This measures
  process creation only, not readiness to record.
- Diagnostic snapshots showed Claude Code v2.1.316. Completed theme/security/trust
  setup only in the newly created workspace. Final screen showed Claude Max,
  safe mode, an empty input and `hold space to speak`.
- No prompt was submitted. The existing Claude session and clipboard were not
  accessed. Waiting for the user to be ready before arming the first microphone take.
- A targeted process query identified only this executable's `--probe` process and
  its direct Claude child. One ready-before-recording snapshot:
  - Probe: 19.9 MiB working set, 13.3 MiB private bytes, 0.078125 cumulative CPU seconds.
  - Owned Claude CLI: 200.3 MiB working set, 382.2 MiB private bytes, 2.984375 cumulative CPU seconds.
  These are individual process snapshots, not idle CPU rates or final tray-app
  measurements. Private bytes are not a physical resident-memory measurement.
- Speech recognition, final export, cancellation, seven-minute duration and return
  to idle remain unverified. A terminal UI does not make the CLI itself negligible
  in memory; provider lifetime needs to account for this measured cost.

## Short take 1

- Confirmed the owned CLI's `listening…` status after synthetic hold began.
- On the user's stop message, released hold and inspected the finalized unsent
  composer. Ctrl+G exported 174 characters; the probe exited with code 0.
- The result contained unrelated leading speech and rendered the intended name
  `Murmur` as `Mama`. The rest of the test sentence included OAuth and JSON.
- The user initially reported only saying the phrase, then clarified there was
  faint background sound. Background pickup is plausible, not a proven root cause.
- Verified the exact previously identified probe PID and direct Claude child were
  absent after exit. The owned `handoff.txt` was removed. A CLI-created temp folder
  and native `.node` file remained; they were not recursively deleted.

## Audio input and quiet short take 2

- Read-only Core Audio endpoint metadata reported the same default input for
  console, multimedia and communications: Microphone Array (Intel Smart Sound
  Technology for Digital Microphones). No audio was captured and no settings were
  changed by this metadata inspection. The user confirmed this is the intended mic.
- Started a new probe/Claude child with a fresh empty composer. The second startup
  did not require repeating theme/security/trust setup. Child creation was 25 ms;
  readiness latency still is not measured.
- After the user muted background audio and confirmed readiness, verified the new
  child displayed `listening…`. Stopped on request and inspected finalized text.
- The editor export returned 93 characters and exit 0. It contained only the test
  phrase, with `Murmur` again rendered as `Mama`. No unrelated leading speech was
  present. The user reported a small stutter. No raw audio is available to classify
  recognition errors more precisely.
- No prompt submission or assistant response was observed; the clipboard was not
  written. This establishes two successful short hold/stop/export runs, not perfect
  transcription accuracy. Proper-name accuracy remains an explicit limitation.
- Awaiting user availability for seven-minute duration and cancellation checks.

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

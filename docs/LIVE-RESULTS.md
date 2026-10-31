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
  browser authorization; authentication has not succeeded yet.
- No credentials are copied from the user's existing Claude profile, read into
  chat or committed. Authorization links/codes are not written to repository docs.
- No microphone or speech recognition result exists yet. Native ConPTY startup,
  transcription, export, long recording, cancellation and CPU/RAM checks remain.

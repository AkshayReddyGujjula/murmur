# Feasibility harness verification, 2026-10-31

This is a fresh rebuild on `rebuild/minimal-dictation`. The prior implementation
was removed at empty-tree commit `98da563`; the archive branch retains it for
recovery. No old source was copied into the rebuild. All work is local.

## Automated evidence

`node scripts/build.mjs test` passed the seven offline groups:

- Control gate: one take, simulated seven-minute repeat schedule, stop, no Enter
  after arming, cancel and stale export handling. This is not a real audio test.
- Strict command-line options and inert default/help parsing.
- Event mailbox: tick coalescing, reliable export/error signals, cancellation
  priority, preserved key ordering, explicit overflow and disposal.
- Transport: 521 assertions in this environment for quoting, environment blocks,
  pre-native argument rejection, native structure layouts and never-started disposal.
- Handoff: 20 passed; 1 skipped reparse/junction fixture. Exact bytes, BOM, Unicode,
  mixed line endings, whitespace, bounds, invalid paths, UTF-8 and duplicate publish.
- Diagnostic terminal screen: 18 passed, including split escapes, cursor/erase,
  query responses, bounded malformed sequences and concurrent feed/snapshot.
- Real entry-point inertness: help/error paths never resolve the profile factory.
  A positive control constructs the profile under a test-owned path, proving the
  redirection seam works. Diagnostic bare CR, CRLF and format controls are checked.

`node scripts/build.mjs build` passed using the installed .NET Framework C# compiler,
x64, warnings as errors, no third-party packages. This only compiles the probe.

The first integrated test run exposed a Windows compiler relative-path issue and
denied system-temp fixture creation. Build inputs now use absolute native paths;
test-only TEMP/TMP overrides point inside `.test-output/`. No permissions, execution
policy or system settings were changed to make the checks pass.

## Independent review and fixes

Muse reviewed the fresh terminal transport and DeepSeek reviewed the harness and
profile boundaries. The coordinator verified findings and made bounded fixes:

- Managed completion predicates replace constructor-created kernel events.
- Kernel32 lookup no longer adds a LoadLibrary reference per session.
- Child process handle inheritance is disabled; private job containment remains.
- The reaper owns closing the process handle after its wait completes.
- Attribute-list cleanup only calls Delete after successful initialization.
- Transport and diagnostic model share the same 120 x 40 geometry.
- Startup terminal queries can be answered before Start returns; failed replies
  and setup input writes now produce explicit failures.
- Idle writer/dispatcher workers wait for notifications rather than timed polling.
- The inertness test uses the actual profile-root seam instead of changing an
  environment variable that .NET's special-folder lookup ignores.
- Critical events have reserved mailbox slots; timeout checks for an already
  published handoff before reporting a failure.
- Diagnostic display cannot be overwritten by bare CR or bidi formatting controls.
- Existing path ancestors and ownership-marker contents are checked before using
  the profile. Only exact session-owned files are cleaned up after child disposal;
  authentication data and unknown CLI files are never recursively pruned.
- Rejected editor exports emit a content-free status code in the owned terminal.

## Open gates

No real app/provider, login, microphone, clipboard or user window has been accessed.
Native ConPTY creation, containment and teardown are not runtime-tested. The
terminal model is deliberately partial: no alternate screen, scroll regions or
wide/combining cell support. Its snapshots are diagnostics, never transcript data.

Claude's own state isolation outside CLAUDE_CONFIG_DIR, flag compatibility, quoted
editor invocation, repeat-key dictation, finalization, seven-minute behavior and
actual CPU/RAM/startup costs require approved live evidence. Cleanup of known
session files is best effort: unknown CLI temp files can remain, and an older
Windows ConPTY close can outlive the bounded Dispose wait. The profile is kept
for future sign-ins; no promise of completely ephemeral CLI storage is made.

The tray app, global Ctrl+Space, real waveform, clipboard and focus acceptance
follow only after this feasibility gate succeeds. See [LIVE-CHECK.md](LIVE-CHECK.md).

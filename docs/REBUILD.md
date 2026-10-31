# Murmur rebuild: agreed product contract

Updated 2026-10-31. This records the user's instructions for the replacement app.

## Approval boundary

The user subsequently required plan approval before any major implementation or
replacement. Small isolated prototypes and offline tests are allowed. The full
rebuild, app integration, and live testing have not been approved. The original
`app/` and `native/` source remain unchanged.

## Proposed stages, awaiting approval

1. Prove Claude Code hold-mode dictation in a Murmur-owned hidden terminal and
   separate configuration profile. Export the finalized unsent prompt through the
   external-editor command. Never drive the user's existing session. A separate
   profile may need one-time Claude.ai sign-in by the user.
2. Obtain explicit live-test approval, then establish short-take and seven-minute
   behavior, cancellation, export integrity, and actual CLI memory/startup costs.
   If this fails, report the evidence and revise the plan before a full rebuild.
3. Build the native tray host and tiny, non-activating waveform overlay. Use
   Ctrl+Space for start/stop, real audio levels while recording, and automatic
   clipboard copy after finalization. Keep idle event-driven and close the owned
   provider when practical. No transcript dashboard or automatic prompt submission.
4. Have DeepSeek and Muse independently review the integrated code; run automated
   failure/regression checks, then seek live acceptance approval and measure idle,
   recording, finalization and teardown resource usage. Retain the old app in Git
   until the replacement passes.

Only prototype/offline evidence exists so far: 15 session behavior tests, 13
prompt-export tests and 832 transport assertions pass; the opt-in probe compiles.
None of this establishes successful native terminal startup or speech recognition.

## Required workflow

1. Press Ctrl+Space from another app or website.
2. Speak in English. Ordinary recordings can last five to seven minutes.
3. While recording, see only a small voice indicator with a waveform that responds
   to the microphone. No dashboard or transcript card.
4. Press Ctrl+Space again to stop.
5. Receive the final transcript on the clipboard, ready to paste in the original
   app. Murmur must not steal keyboard focus.

## Footprint requirement

The app must stay lightweight enough to leave running continuously, without
sacrificing transcription quality. Idle work should be an OS message loop/global
hotkey, with no waveform timer, rendering loop, microphone sampling or provider
process kept alive unnecessarily. Open a dedicated CLI child only when needed
and close only that owned child when finished. Benchmark cold-start friction
before deciding whether a bounded warm period is worthwhile. A terminal UI alone
does not establish that its backend uses little memory.

Measure resident/private memory and CPU separately for idle controller, startup,
recording, finalization and return to idle. Report measured numbers and hardware;
do not claim zero resource use or infer RAM from executable size.

## Transcription requirement

Reuse the dictation included in the user's Claude or Codex application or CLI.
There must be no separate speech API billing, API key, or local speech model.
The choice of application/CLI is open to investigation; the included dictation
requirement is not.

Use application controls rather than duplicating private service authentication.
Do not send the dictated prompt to a coding agent. Capture the unsent input.

## Current permissions

Source changes and automated code checks are authorized. Keep all work local.
Any live/manual test, microphone or clipboard interaction, app launch, installation,
or system-setting change requires explicit user approval first.

The user's existing Claude session is off limits. Do not attach to it, inspect it,
send it keys, alter its settings, hide its window, or restart it. A proposed backend
must own a separate child process and target only handles it created. Sharing user
configuration can affect running sessions, so session-only configuration and its
limits must be checked before launching any backend.

## Investigation findings, not runtime acceptance

- The old implementation uses calibrated clicks and manipulates Claude Desktop's
  foreground/window state. It is not the basis for the replacement.
- Claude Code documents hold-to-record dictation that leaves text unsent, and an
  external-editor command for editing the current prompt. A dedicated pseudoterminal
  plus prompt-export helper is a candidate, not yet validated.
- Claude Code's tap mode automatically submits sufficiently long prompts and stops
  after two minutes or fifteen seconds of silence. It is unsuitable as-is.
- Installed Codex package metadata reports 0.166.0. Bundled voice libraries alone
  do not establish that reusable, transcription-only dictation is exposed.
- Seven-minute recording, finalization detection, and real audio-level telemetry
  remain explicit feasibility gates. Never show fabricated audio activity.

## Acceptance gates

Automated checks must cover shortcut state transitions, stop during startup,
cancellation during finalization, stale child events, exact Unicode/multiline
transcript transfer, clipboard retry without losing text, provider failure, owned
child cleanup, and long-recording limits. Fixtures must not launch real providers.

Live acceptance, only after approval, must establish a short take, a seven-minute
take, cancel, repeated takes, microphone failure, clipboard contention, and focus
preservation while an unrelated Claude session continues untouched.

Sources checked on 2026-10-31:

- https://code.claude.com/docs/en/voice-dictation
- https://code.claude.com/docs/en/interactive-mode
- https://code.claude.com/docs/en/cli-reference
- https://code.claude.com/docs/en/settings

The previous implementation remains in Git for reference and rollback. No live
recording, real clipboard check, or window interaction has been performed.

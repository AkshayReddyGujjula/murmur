# Approved rebuild plan

Historical planning record from 31 October 2026. Current setup and publication
instructions are in README.md and RESUME.md. The user subsequently authorized
committing, merging to main, and publishing the project publicly on GitHub.

The user approved this plan and required deletion of the entire old implementation
on 2026-10-31. Local commits/branches are allowed; pushing is prohibited.

## Product contract

- Ctrl+Space toggles recording from other Windows apps and websites.
- English dictation, including ordinary recordings of five to seven minutes.
- Only a tiny waveform while recording, driven by real microphone levels.
- Stop, finalize, copy the exact transcript automatically, then paste manually.
- Preserve focus in the user's original app. Never submit text to a coding agent.
- Reuse included Claude/Codex dictation, without a separate speech API or local ASR.
- Minimize idle memory and CPU: OS events, no idle render loop, no idle mic sampling,
  no unnecessary resident provider. Measure CLI startup cost versus a warm process.

## Hard execution boundary

Source changes, local Git operations, compilation and isolated offline automated
tests are allowed. Launching real apps/providers, login, microphones, clipboard,
manual checks, installations or system-setting changes require explicit approval.
The user's running Claude session is entirely off limits.

## Stages

1. **Feasibility harness.** A dedicated Windows pseudoterminal hosts an owned
   Claude Code CLI child in documented hold mode (`autoSubmit: false`). Use an
   isolated profile and workspace. Export the finalized unsent prompt through the
   documented external-editor command. No foreground input simulation.
2. **Live gate, after separate approval.** Verify initial setup, short recording,
   seven-minute recording, cancellation, exact final export, no prompt submission,
   child cleanup, startup latency, CPU and private/resident memory. If it fails,
   stop and revise the plan before implementing the tray app.
3. **Native tray host.** Implement the shortcut, a small non-activating waveform,
   real recording telemetry, explicit processing state, clipboard retries and
   recoverable errors. No dashboard. Provider lifetime follows measured trade-offs.
4. **Review and acceptance.** Independent DeepSeek/Muse code review, automated
   regression checks, then approved live checks and resource measurements. Keep
   each local checkpoint reviewable; do not claim runtime acceptance from unit tests.

## Evidence and limits

Claude Code documents hold mode leaving text unsent, tap mode auto-submitting
longer prompts and stopping at two minutes, and Ctrl+G opening the current prompt
in an external editor. Hold mode's seven-minute behavior is not established.
ConPTY key-repeat behavior, finalization detection and external-editor integration
also remain live feasibility gates. No inference of unlimited usage or low CLI RAM.

Sources checked during the initial investigation:

- https://code.claude.com/docs/en/voice-dictation
- https://code.claude.com/docs/en/interactive-mode
- https://code.claude.com/docs/en/cli-reference
- https://code.claude.com/docs/en/settings


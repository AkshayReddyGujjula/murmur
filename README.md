# Murmur

A lightweight Windows dictation companion, rebuilt from an empty working tree.

The intended workflow is Ctrl+Space, speak, Ctrl+Space, paste. During recording,
only a small waveform should appear. Transcription must use the dictation already
included in Claude or Codex; no separately billed speech API or local ASR model.

## Current stage

Building an opt-in feasibility harness for a **dedicated Claude Code CLI child**.
This is not yet the finished tray app. No app, provider, microphone or clipboard
test has been authorized or run. Code changes, compilation, and isolated automated
tests are authorized. Live tests require separate user approval.

The harness must use its own Claude configuration profile, not attach to the
user's existing Claude process, window, terminal, session, or configuration.
It must never submit dictated text as an agent prompt.

## Development

Windows x64, the Windows .NET Framework C# compiler, and Node.js for build/test
orchestration. No package installation or runtime download is required. Node is
not part of the eventual running app.

See [PLAN.md](PLAN.md) for the approved stages and acceptance gates.

## Local recovery

The legacy implementation and earlier experiments are preserved only in Git on
`archive/pre-rebuild-20261031`, commit `47ffa84`. The empty-tree rebuild checkpoint
is `98da563`. No source from that implementation is being copied into this rebuild.


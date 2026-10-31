# Murmur

A lightweight Windows dictation companion, rebuilt from an empty working tree.

The intended workflow is Ctrl+Space, speak, Ctrl+Space, paste. During recording,
only a small waveform should appear. Transcription must use the dictation already
included in Claude or Codex; no separately billed speech API or local ASR model.

## Current stage

Building an opt-in feasibility harness for a **dedicated Claude Code CLI child**.
This is not yet the finished tray app. No app, provider, microphone or clipboard
test had been run at the initial code checkpoint. The user separately approved
isolated login/probe and microphone checks on 2026-10-31. The existing Claude
session and clipboard remain off limits.

The harness must use its own Claude configuration profile, not attach to the
user's existing Claude process, window, terminal, session, or configuration.
It must never submit dictated text as an agent prompt.

## Development

Windows x64, the Windows .NET Framework C# compiler, and Node.js for build/test
orchestration. No package installation or runtime download is required. Node is
not part of the eventual running app.

See [PLAN.md](PLAN.md) for the approved stages and acceptance gates.

```powershell
node scripts/build.mjs test
node scripts/build.mjs build
```

The build produces `build\Murmur.Probe.exe` and does not launch it. The seven
offline test groups pass, with one explicitly skipped reparse-point case. These
checks do not establish native terminal startup or successful speech recognition.
The test runner keeps temporary fixtures under the ignored `.test-output/` folder.

The isolated live-test procedure is in [docs/LIVE-CHECK.md](docs/LIVE-CHECK.md).
That isolated login/probe scope is now approved. Its portable profile lives in
the ignored `build\data\feasibility-v1` folder; it does not reuse the user's
existing Claude profile.

## Local recovery

The legacy implementation and earlier experiments are preserved only in Git on
`archive/pre-rebuild-20261031`, commit `47ffa84`. The empty-tree rebuild checkpoint
is `98da563`. No source from that implementation is being copied into this rebuild.


# Murmur maintainer handoff

Current version: see `VERSION`. Murmur has visual-only feedback; old sound settings are ignored. Publication target: `AkshayReddyGujjula/murmur`, `main`.

The app is a Windows x64 tray companion around an isolated Claude Code dictation
session. The current UI has 27 bars, a 132 × 41.58 logical-pixel pill, optional
paste, and compact success/failure results. Completion polling uses 50 ms while
finalizing/exporting a transcript; the 700 ms settling guard remains intact.

Use README.md for setup, configuration, privacy and known limits. Component
contracts are in docs/APP-CONTRACTS.md. Earlier repair notes are historical evidence,
not current instructions to block approved releases or change user permissions.

## Working locally

- Preserve user data and all unrelated worktrees. The development folder may still
  be named STT App; the product and GitHub repository are named Murmur.
- An existing installation can point at the development `build/data` directory via
  `%LOCALAPPDATA%/Murmur/data-location.txt`. Renaming that directory requires planning
  for the active profile path and Claude's completed workspace setup.
- Never inspect or operate an unrelated Claude session. Native tests launch only
  their own test child. Real microphone, account and clipboard acceptance is separate
  from offline validation.
- Release packages must contain only Murmur.exe, Install.ps1, README.txt and
  SHA256SUMS.txt. Never package data, profiles, history, logs or credentials.

## Verification

The release preparation reruns the complete offline suite, the native ConPTY smoke
regression, build and ZIP manifest/checksum checks. The suite includes 436 engine
assertions, 389 UI checks and 61 startup checks. One existing reparse fixture may
be skipped when link creation is unavailable.

The user has exercised the live desktop flow. Offline checks do not establish
recognition accuracy, long continuous recording, real frame pacing, or a guaranteed
end-to-end latency improvement. Keep those limits visible in the public README.

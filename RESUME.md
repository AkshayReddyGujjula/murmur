# Resume notes (2026-10-31, 22:20 stop)

## State
- Branch `rebuild/minimal-dictation`. The tray app builds as `build\Murmur.exe` and all
  offline tests pass. It has not been launched live yet; run the user checklist in `docs/APP-CHECKS.md`.
- Built by OpenCode workers (DeepSeek v4.1 Flash max, Muse Spark 1.3 xhigh) from
  `docs/APP-CONTRACTS.md`, with the parent writing `src/App/Program.cs`.
- Review rounds: round 1 (4 DeepSeek max reviewers, ~30 findings) fixed in commits
  `8554376..9de7fbe`. Round 2 (engine + wiring) findings are listed below.

## Round-2 work (take ids + suspend)
DONE and committed: engine take ids, suspend mode and all round-2 engine fixes (386 engine assertions), Program.cs on the new API, per-user Global mutexes in app and probe. Not yet re-reviewed by a third DeepSeek round.

Round-2 findings being addressed (DeepSeek max):
1. Stale Stopped/Discarded events could end a newer take. Fix: engine-issued take ids
   (`int BeginTake()`, `TakeRecording/TakeTranscript/TakeFailed/TakeDiscarded(id)`).
2. BeginTake dropped while Finalizing. Fix: queue it.
3. Hold spaces could go into a sign-in prompt mid-take. Fix: guard every hold tick.
4. Cancel quiet window shorter than finalization. Fix: >= NoSpeechMs, and verify an empty
   composer before any hold.
5. Discard-before-take could hold on top of the old draft. Fix: verify cleared, else recycle.
6. Composer text such as "Please run" misread as sign-in. Fix: exclude the composer from all scans.
7. PollClear faulted on transient frames. Fix: wait to the deadline, then recycle.
8. Late export after timeout. Fix: recycle the child on export timeout.
9. Sign-in could overlap a queued Warm. Fix: `Suspend()/Resume()/Suspended`.
10. Program: per-user Global mutexes, OnDemand unload when Starting, junction-safe pruning,
    no-op when re-selecting the same shortcut.



## Next
1. User live checks (`docs/APP-CHECKS.md`), including cancel and a long take.
2. A final DeepSeek review round after the round-2 work.
3. Measure idle CPU/RAM of Murmur.exe and the claude.exe child.

## Tools
- `node scripts/build.mjs test | build | test-group <Engine|Core|Audio|Ui>`
- OpenCode runs: `~/.opencode-agents/runs/` (worktrees retained, nothing pushed).
  Use `OCA_TIMEOUT_MS=2400000` for max-reasoning reviews (the 10 min default times out).
  Running several `run-task` at once can hit an EPERM manifest rename; stagger starts by 30 s.

# Resume, 3 November 2026

User requests autonomous fixes and repeated DeepSeek v4.1 Flash reviews through T3 Code OpenCode.
Keep everything local; do not operate or inspect the existing Claude session. No mic, clipboard,
settings changes, installations, or manual live checks. Current tests are offline/fake/hidden.

## Delivered

- Root branch rebuild/minimal-dictation is at 447c7d6; build/Murmur.exe rebuilt at d828a40
  (the later commit is documentation only). Root's untracked .gitignore (1) is unrelated; preserve.
- Integration branch repair/integration-20261103, checkout:
  C:\Users\aksha\.t3\worktrees\STT App\murmur-repair-integration
- Both fatal TrayIcon RenderMode=Custom assignments removed; hidden actual-menu constructor
  regression passes and fails if either invalid setter is restored.
- Launch confirmation pill, early duplicate named-event reveal, startup/UI error reporting,
  persistent shortcut-conflict status and tray start/stop/cancel actions.
- Cancelled/superseded transcript callbacks cannot reach clipboard/history.
- Optional auto-paste guards original HWND, post-write clipboard sequence and modifier state;
  pending timer cancelled on new take/shutdown. Off by default.
- Engine: empty export reports failure; missing composer is not evidence of no speech;
  locked discard handoff logs retained path and recycles owned session.
- Full offline suite passed: 403 Engine, 171 Ui, 56 Startup plus Wiring/Core/Audio/harness;
  1 reparse fixture skipped. Startup/Wiring rerun after last review fixes. Build/diff checks pass.
- Real shortcut/clipboard/visual acceptance, speech accuracy, seven-minute capture, final
  idle CPU/RAM remain unverified. See docs/REPAIR-2026-11-03.md for triage/evidence.

## Pending delegates

T3 delegate_task: provider opencode, model opencode-go/deepseek-v4.1-flash, variant max.
Three original wait calls are in functions exec cell 46 in the originating runtime.
Await original results; do not poll T3 tasks. Completion can wake the thread.

1. Make microphone meter lifecycle nonblocking and safe: separate prepared worktree from
   d828a40; owns src/App/Audio and tests/App/AudioTests.cs. Native lifecycle off UI,
   no overlapping device sessions, injected fake-device cancellation/restart tests.
2. Reduce overlay render allocation and report presentation failure: separate worktree;
   owns OverlayWindow.cs/optional Ui helper and UiTests.cs. Reuse rendering resources,
   Fault event, hidden tests. Parent must wire Fault to log/tray without recursion.
3. Recheck integrated engine and paste guards: read-only root review at d828a40.

All initial reviews and startup/paste implementation delegates completed. Integrated startup
re-review completed; valid small findings fixed (early reveal, duplicate notifications, wording).

## Next

Inspect real worker diffs/test evidence; integrate owned patches in integration checkout first.
Preserve startup tests and current changes. Review audio generations/disposal and overlay GDI
ownership; wire Fault. Fix verified review findings, then independent lifecycle review.
Run changed groups, full suite, build; commit explicit owned files, fast-forward root only after
checking baseline/unchanged tracked state, rebuild root binary. Update this file/report.
Never push, delete retained worktrees, kill unrelated processes or launch microphone.

Rejected findings: EndTake cannot overtake BeginTake in serial FIFO; do not add a 1.5s
transcription watchdog. Keep cancel quiet window (late words). .NET NotifyIcon handles
TaskbarCreated already. waveInUnprepareHeader on unprepared buffer returns zero per Microsoft,
not WAVERR_UNPREPARED. Autostart registration cannot identify manual vs login launch; explicit
--background only. Do not migrate registry entries automatically.

# Feasibility harness component contracts

All C# source targets C# 5 / .NET Framework 4.8, x64, with no third-party packages.
Do not load native APIs, inspect user state or launch providers at type initialization.

## Owned terminal (`src/Transport/OwnedTerminal.cs`)

Namespace `Murmur.Transport`, sealed `OwnedTerminal : IDisposable`:

- `event Action<string> Output`: incremental UTF-8-decoded terminal output. The
  receiver must return promptly and must not block the drain thread.
- `event Action<string> Closed`: one termination/failure notice, without raw
  transcript/environment data. Receiver must marshal UI work to its own thread.
- `void Start(string executable, string[] arguments, string workingDirectory,
  IDictionary<string,string> environmentOverrides)`: exact existing absolute EXE
  and working directory; child-only overrides, null values remove inherited keys.
- `bool TryWrite(string text)`: bounded queue; never blocks caller waiting on child.
- `void Dispose()`: owns only the new process/job/pipes/pseudoconsole. No process
  enumeration, AttachConsole to another session, SendInput, window lookup or global
  process-name kill. Never let event callbacks deadlock native shutdown.
- `static string QuoteArgument(string value)` and `static string EnvironmentBlock(
  IDictionary<string,string> overrides)` are pure helpers suitable for offline tests.

Start suspended, assign to private kill-on-close job before resuming, and fail closed
on containment failure. Separate pipe read/write workers. Native close must keep
output draining; no lifecycle mutex may be held over a blocking close/wait.
Document actual boundedness and native gaps rather than pretending an untested
ClosePseudoConsole can never block. No native fixture launch is authorized yet.

## Prompt handoff (`src/Handoff/PromptHandoff.cs`)

Namespace `Murmur.Handoff`, static `PromptHandoff`:

- `static int Export(string promptPath, string sessionDirectory, string destination)`
  returns 0 on success or a nonzero status. No transcript content in diagnostics.
- Input and destination must be absolute paths strictly inside an existing owned
  session directory, with path separator boundaries. Reject reparse links within
  the accepted path chain, traversal outside, source=destination, existing output,
  invalid UTF-8 and input larger than 1 MiB. Read with a hard allocation cap.
- Preserve original bytes, BOM, Unicode, CRLF and trailing whitespace. Do not edit
  or delete the source prompt. Write a unique sibling using CreateNew, then atomic
  non-overwriting Move. Remove only the exact staging file that this invocation
  created, including failures during write/flush. Never recursively delete fixtures.

## Parent-owned harness

The coordinator owns `src/Probe/`, `scripts/`, root docs and build orchestration.
It must be inert by default. Only explicit `--probe` / `--login` modes can launch
Claude; neither will be run without separate user approval. Default/help and tests
must not create profiles, native handles, providers, mic streams or clipboard writes.
Use a separate Murmur-owned Claude profile and session/workspace. Never read/copy
credentials out of the user's existing profile. Do not send Enter after recording
has been armed; export uses Ctrl+G. This is an experimental harness, not proof of STT.

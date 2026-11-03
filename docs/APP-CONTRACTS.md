# Murmur tray app: module contracts

The live gate proved the backend: an owned Claude Code CLI child in a ConPTY, hold
mode driven by repeated spaces every 50 ms, `listening…` on screen while recording,
finalized text left unsent in the composer, and Ctrl+G (`\x07`) handing the composer
to `VISUAL` = `Murmur.exe --export-prompt <file>`, which copies it to `handoff.txt`.

The tray app (`build\Murmur.exe`, WinForms `winexe`) wires four modules. Each module is
owned by one worker and lives in its own folder. **No module may reference another
App module's types** (Program.cs, written by the orchestrator, wires them together).
They may use the existing backend files listed per module.

All code: C# 5 (no `?.`, no `$""`, no `nameof`, no expression-bodied members, no
`out var`), .NET Framework 4.8, x64, no packages, compiled with `/warnaserror+`.
References available: System.Windows.Forms, System.Drawing, System.Security.
Tests are console programs `namespace Murmur.Tests.App`, `static class <Group>Tests`,
`public static int Main()` returning 0 on pass and 1 on fail, printing `PASS:`/`FAIL:`
like `tests/ControlTests.cs`. Run one group with `node scripts/build.mjs test-group <Group>`.
Tests must be offline: no Claude process, no microphone, no clipboard, no registry
writes, no global hotkey registration, no visible windows. Tests run with cwd = repo
root, and TEMP points to `.test-output/tmp`.

Never log or persist transcript text except in `TranscriptHistory` (encrypted).

## Engine (`src/App/Engine/`, namespace `Murmur.App.Engine`)

May use: `Murmur.Transport.OwnedTerminal`, `Murmur.Probe.TerminalScreen`,
`Murmur.Probe.IsolatedProfile`, `Murmur.Handoff.PromptHandoff` (all already exist; do
not modify them). Test file: `tests/App/EngineTests.cs`. Fixtures in `tests/App/fixtures/`
(`ready.txt`, `ready-plain.txt`, `listening.txt`, `finalized.txt`, real snapshots; blank
lines may appear anywhere, and lines may have trailing spaces).

```csharp
public enum ScreenKind { Unknown, Ready, Listening, NeedsSignIn, NeedsSetup }
public sealed class ScreenReading {
    public ScreenKind Kind;      // Listening if any line contains "listening…" (U+2026) or "listening...";
                                 // NeedsSignIn if text mentions "/login", "Not logged in", "Invalid API key" or "Please run";
                                 // NeedsSetup if a trust/theme/security choice prompt is shown (e.g. "Do you trust",
                                 //   "Yes, proceed", "Choose the text style", "Press Enter to continue");
                                 // Ready if a composer (line starting with U+276F "❯") sits between two rule lines
                                 //   made of U+2500 "─"; otherwise Unknown.
    public bool HasComposer;
    public string ComposerText;  // composer content: text after "❯" on the prompt line plus following
                                 // continuation lines up to the next rule line, each trimmed, joined by
                                 // single spaces, then trimmed. The recording cursor glyph "▁" (U+2581)
                                 // alone counts as empty. Never null ("" when empty).
}
public static class ScreenParser { public static ScreenReading Read(string snapshot); }

public interface ITerminalHost : IDisposable {   // adapter over OwnedTerminal; fakes in tests
    event Action<string> Output; event Action<string> Closed;
    void Start(string executable, string[] arguments, string workingDirectory, IDictionary<string,string> environment);
    bool TryWrite(string text);
}
public sealed class OwnedTerminalHost : ITerminalHost { /* wraps OwnedTerminal */ }

public interface IEngineSession : IDisposable {  // one owned profile session; fake in tests
    string Workspace { get; }  string ExportPath { get; }  string[] Arguments { get; }
    IDictionary<string,string> Environment { get; }  // must include MURMUR_CLEAR_PROMPT=1
}
public sealed class ProfileSession : IEngineSession { /* wraps new IsolatedProfile(); Dispose -> CleanupKnownFiles */ }

public enum EngineState { Stopped, Starting, Ready, Recording, Finalizing, Faulted }
public sealed class EngineOptions {
    public string ClaudePath;                         // absolute claude.exe
    public int ReadyTimeoutMs = 45000, ListenTimeoutMs = 8000, FinalizeTimeoutMs = 90000,
               ExportTimeoutMs = 15000, StableMs = 700, NoSpeechMs = 5000,
               PollMs = 100, CompletionPollMs = 50, HoldRepeatMs = 50;
}
public sealed class DictationEngine : IDisposable {
    public DictationEngine(EngineOptions options, Func<ITerminalHost> terminals, Func<IEngineSession> sessions);
    public EngineState State { get; }
    public event Action<EngineState> StateChanged;    // raised on the engine thread; UI marshals
    public event Action<string> TranscriptReady;      // final exported text, Trim()'d, non-empty
    public event Action<string> Failed;               // short user-facing message, never transcript text
    public event Action<string> Diagnostic;           // log lines, never transcript text
    public void Warm();         // start the child in the background if Stopped/Faulted
    public void BeginTake();    // start child if needed; once Ready begin hold -> Recording when "listening" seen
    public void EndTake();      // release hold, finalize, export, raise TranscriptReady, return to Ready
    public void CancelTake();   // stop hold, discard any finalized text (export + ignore), return to Ready
    public void Unload();       // dispose child/session, State = Stopped
    public void Dispose();
}
```

Behaviour (single engine thread + message inbox; public methods only post messages and
never block; all timers post into the inbox):

1. **Start:** create session and terminal, feed `Output` into a `TerminalScreen(OwnedTerminal.TerminalColumns, OwnedTerminal.TerminalRows)`
   (reply callback writes back to the terminal), poll `ScreenParser.Read(screen.Snapshot())`
   every `PollMs`. Ready when Kind==Ready. NeedsSignIn -> Failed("Sign in needed — open the
   tray menu and choose Sign in"), Faulted. NeedsSetup -> Failed("Claude needs one-time setup —
   choose Sign in from the tray menu"), Faulted. Timeout -> Failed("Claude did not start"), Faulted.
   Never send Enter or any other key during startup.
2. **BeginTake** while Ready: if ComposerText is non-empty, first discard it (export, ignore).
   Then write `" "` every `HoldRepeatMs` and wait for Kind==Listening -> Recording. If not seen
   in `ListenTimeoutMs`: stop spaces, Failed("Microphone did not start"), discard, back to Ready.
   BeginTake during Starting: remember the request and begin when Ready (raise Starting state).
3. **EndTake** while Recording: stop spaces -> Finalizing. Poll at `min(PollMs, CompletionPollMs)`: finalized when not Listening
   and ComposerText non-empty and unchanged for `StableMs`. A positively identified empty
   composer and not Listening for `NoSpeechMs` -> Failed("No speech detected"), Ready.
   Missing-composer redraws preserve text stability; continuous absence is bounded by
   `ScreenUnavailableTimeoutMs` (10 s), with a distinct failure and owned-session recycle.
   Overall `FinalizeTimeoutMs` remains the bound while the provider is still Listening.
   EndTake while Starting (take requested but not yet recording) == CancelTake.
4. **Export:** delete a stale `ExportPath`, write `"\x07"`, wait for `ExportPath` (poll every
   `min(PollMs, CompletionPollMs)` for transcripts, `PollMs` for discards, bounded by
   `ExportTimeoutMs`), read it strictly as UTF-8 (strip BOM, max 1 MiB, retry briefly
   if the file is still being written), delete it. Non-empty after Trim -> TranscriptReady.
   Then the composer should read empty (the export command clears the prompt file); if it is
   still non-empty after 3 s, recycle the child (dispose, restart via Warm) so the next take is clean.
5. **Closed** from the terminal at any time: dispose, State Faulted (or Stopped when Unload
   requested); an active take raises Failed("Claude closed unexpectedly"). Next Warm/BeginTake restarts.
6. Dispose/Unload: stop timers, dispose terminal then session. Never kill processes other than
   via disposing the owned terminal.

Tests must cover ScreenParser on all fixtures plus synthetic variants (sign-in, setup,
multiline composer, cursor glyph, blank lines), and the full state machine with fake
terminal/session using small timeouts (ready, listen confirm, finalize, export via a fake
that writes the export file when it receives `\x07`, no-speech, listen timeout, cancel, closed
mid-take, begin during Starting, recycle when composer not cleared).

## Core (`src/App/Core/`, namespace `Murmur.App.Core`)

Test file: `tests/App/CoreTests.cs`. Everything takes explicit directories/paths so
tests use `.test-output/tmp`.

```csharp
public enum HotkeyChoice { CtrlSpace, CtrlShiftSpace, CtrlAltSpace, AltShiftSpace }
public enum EngineMode { AlwaysReady, UnloadWhenIdle, OnDemand }
public enum OverlayPlacement { BottomCenter, TopCenter }
public sealed class AppSettings {
    public HotkeyChoice Hotkey = HotkeyChoice.CtrlSpace;
    public EngineMode Engine = EngineMode.AlwaysReady;
    public int IdleUnloadMinutes = 15;          // clamp 1..240
    public OverlayPlacement Placement = OverlayPlacement.BottomCenter;
    public bool KeepHistory = true;
    public int HistoryLimit = 25;               // clamp 1..200
    public bool AutoPaste = false;
    public bool Sounds = true;
    public bool EscapeCancels = true;
    public static AppSettings Load(string path);   // missing/corrupt/unknown keys -> defaults, never throws
    public void Save(string path);                 // key=value lines, atomic (temp + replace)
}
public sealed class HistoryEntry { public DateTime TimeUtc; public string Text; }
public sealed class TranscriptHistory {
    public TranscriptHistory(string path, int limit);
    public IList<HistoryEntry> Items { get; }     // newest first, read-only copy
    public int Limit { get; set; }                // trims
    public void Add(string text);                 // ignores empty; persists
    public void Clear();                          // deletes file
    // File is DPAPI (ProtectedData, CurrentUser, fixed entropy) encrypted. Corrupt -> empty, no throw.
}
public sealed class AppLog {
    public AppLog(string path);                   // roll to <name>.old.log beyond 512 KiB
    public void Info(string message); public void Error(string message, Exception error);
    // thread-safe, never throws, timestamp + level per line, single-line messages
}
public static class ClipboardWriter { public static bool TrySetText(string text, out string error); } // STA caller, retries
public sealed class GlobalHotkeys : IDisposable {  // message-only NativeWindow
    public event Action Pressed; public event Action CancelPressed;
    public bool Register(HotkeyChoice choice);     // RegisterHotKey + MOD_NOREPEAT; false if taken
    public bool SetCancelActive(bool active);      // registers/unregisters plain Escape; false on failure
    public static void Describe(HotkeyChoice choice, out uint modifiers, out uint key); // pure, testable
    public static string Label(HotkeyChoice choice);   // "Ctrl+Space"
}
public static class Autostart {                    // HKCU\Software\Microsoft\Windows\CurrentVersion\Run, value "Murmur"
    public static bool IsEnabled(); public static void Set(bool enabled, string exePath);
    public static string CommandFor(string exePath); // pure: quoted path plus --background
}
public static class AutoPaste { public static void SendCtrlV(); }  // SendInput, only when user enabled
public enum Chime { Start, Stop, Error }
public static class Chimes {
    public static void Play(Chime chime);          // async, never throws; cached tiny sine WAVs via SoundPlayer
    public static byte[] BuildWav(int frequencyHz, int milliseconds, double volume); // pure, testable
}
```

## Audio (`src/App/Audio/`, namespace `Murmur.App.Audio`)

Test file: `tests/App/AudioTests.cs`.

```csharp
public static class LevelMath {
    public static float Rms16(byte[] pcm, int byteCount);      // little-endian 16-bit mono, 0..1
    public static float ToMeter(float rms);                    // dBFS -60..0 mapped to 0..1, clamped
}
public sealed class MicLevelMeter : IDisposable {
    public event Action<float> Level;     // 0..1 meter value ~30/s, raised on a background thread
    public event Action<string> Error;    // device open failure etc.; never throws from Start
    public bool IsRunning { get; }
    public void Start();                  // waveIn (winmm) WAVE_MAPPER 16 kHz mono 16-bit, ~32 ms buffers,
    public void Stop();                   // CALLBACK_EVENT + one worker thread; idempotent; frees buffers
    public void Dispose();
}
```
Audio is only for the on-screen waveform. Nothing is stored or sent anywhere. The meter must
not run when not recording.

## UI (`src/App/Ui/`, namespace `Murmur.App.Ui`)

Test file: `tests/App/UiTests.cs`. Look: pure black (#000) surfaces, white foreground,
subtle #1F1F1F/#2A2A2A lines, sleek and minimal, no other colours except a muted red
(#FF453A) for errors and the small recording dot.

```csharp
public static class Logo {
    public static void Draw(Graphics g, RectangleF bounds, bool recording); // black rounded square, 5 white
                                                                            // waveform bars (symmetric, tallest centre)
    public static Icon CreateIcon(int size, bool recording);  // recording adds a small red dot
    public static void SaveIco(string path);                  // multi-size 16/24/32/48/64/256 PNG-in-ICO
}
public enum OverlayMode { Hidden, Starting, Recording, Working, Done, Error }
public static class OverlayLayout {
    public static Rectangle Place(Rectangle workingArea, Size size, bool top, int margin); // centred, pure
}
public sealed class WaveHistory {                // pure, testable
    public WaveHistory(int bars);
    public void Push(float level);               // smoothing: rise fast, fall slowly
    public float[] Snapshot();                   // oldest..newest, 0..1
}
public sealed class OverlayWindow : Form {
    // Layered (UpdateLayeredWindow, per-pixel alpha, anti-aliased pill), WS_EX_NOACTIVATE |
    // WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_TRANSPARENT, ShowWithoutActivation = true.
    // Never takes focus or appears in Alt+Tab/taskbar. Animation timer (~30 fps) runs only while visible.
    public bool PlaceAtTop { get; set; }
    public void PushLevel(float level);           // thread-safe
    public void ShowMode(OverlayMode mode, string text); // UI thread. Recording: red dot + ~24 live bars,
                                                  // ~132x36. Starting: three pulsing dots. Working: shimmer
                                                  // dots. Done: check + text (e.g. "Copied"), hides after 1.1 s.
                                                  // Error: red text, width fits text (max 420), hides after 3.5 s.
}
public sealed class TrayItem {                    // menu model, built by Program.cs
    public string Text; public bool Checked; public bool Enabled = true; public bool IsSeparator; public bool IsHeader;
    public Action OnClick; public List<TrayItem> Children = new List<TrayItem>();
}
public sealed class TrayIcon : IDisposable {
    public TrayIcon(Func<List<TrayItem>> buildMenu);  // menu rebuilt on every open
    public void SetState(bool recording, string tooltip);
    public void Notify(string title, string text);    // balloon
    // Left and right click both open the menu (dark renderer: black bg, #1A1A1A hover, white text,
    // #2A2A2A separators, 13px Segoe UI). Headers render dimmed and non-clickable.
}
```

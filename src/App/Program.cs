using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Murmur.App.Audio;
using Murmur.App.Core;
using Murmur.App.Engine;
using Murmur.App.Ui;
using Murmur.Handoff;

namespace Murmur.App
{
    internal static class Program
    {
        [DllImport("shcore.dll")] private static extern int SetProcessDpiAwareness(int value);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();

        [STAThread]
        public static int Main(string[] args)
        {
            // Invoked by the owned Claude child as VISUAL/EDITOR on Ctrl+G. Copies the unsent
            // prompt to the session handoff file, then empties the prompt so the composer is
            // clean for the next take. Must run before any single-instance or UI logic.
            if (args.Length == 2 && args[0] == "--export-prompt") return ExportPrompt(args[1]);

            // Global + per-user names: one Murmur per Windows user across RDP/fast-user-switching
            // sessions, because all of them would share the same data folder and Claude profile.
            string user = UserKey();
            bool owned;
            using (Mutex app = new Mutex(true, "Global\\Murmur.App." + user, out owned))
            {
                if (!owned) return 0; // already running in the tray
                using (Mutex probe = new Mutex(false, "Global\\Murmur.Feasibility.v1." + user))
                {
                    bool probeFree;
                    try { probeFree = probe.WaitOne(0); } catch (AbandonedMutexException) { probeFree = true; }
                    if (!probeFree)
                    {
                        MessageBox.Show("Close the Murmur probe first; it shares the same Claude profile.", "Murmur");
                        return 1;
                    }
                    try
                    {
                        EnableDpiAwareness();
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        using (TrayApp tray = new TrayApp()) { tray.Start(); Application.Run(); }
                        return 0;
                    }
                    finally { probe.ReleaseMutex(); }
                }
            }
        }

        internal static string UserKey()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    if (identity.User != null) return identity.User.Value;
            }
            catch (System.Security.SecurityException) { }
            return Environment.UserName;
        }

        // System DPI awareness keeps the overlay crisp at 125%/150% scaling while WinForms
        // menus still scale their point-sized fonts.
        private static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwareness(1) == 0) return; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
        }

        private static int ExportPrompt(string promptPath)
        {
            int code = PromptHandoff.Export(promptPath,
                Environment.GetEnvironmentVariable("MURMUR_SESSION_DIRECTORY"),
                Environment.GetEnvironmentVariable("MURMUR_HANDOFF_PATH"));
            if (code == 0 && Environment.GetEnvironmentVariable("MURMUR_CLEAR_PROMPT") == "1")
            {
                try { File.WriteAllText(promptPath, "", new UTF8Encoding(false)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return code;
        }
    }

    internal sealed class TrayApp : IDisposable
    {
        private readonly string _dataDir, _settingsPath, _historyPath, _claudePath;
        private readonly AppLog _log;
        private readonly AppSettings _settings;
        private readonly TranscriptHistory _history;
        private readonly Control _ui;
        private readonly OverlayWindow _overlay;
        private readonly MicLevelMeter _mic;
        private readonly GlobalHotkeys _hotkeys;
        private readonly System.Windows.Forms.Timer _idleTimer;
        private TrayIcon _tray;
        private DictationEngine _engine;
        // _takeId: engine id of the take in progress (0 = none), from the start press until it is
        // copied, failed, cancelled or discarded. _takeRecording: Claude confirmed listening.
        private int _takeId;
        private bool _takeRecording, _stopping, _unloadWhenReady, _disposed;
        private string _lastTakeFailure;
        private bool _accountPending, _accountLogin;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private Process _accountProcess;
        private string _lastTranscript; // in-memory fallback when copying fails and history is off

        public TrayApp()
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _dataDir = Path.Combine(exeDir, "data", "app");
            Directory.CreateDirectory(_dataDir);
            _settingsPath = Path.Combine(_dataDir, "settings.ini");
            _historyPath = Path.Combine(_dataDir, "history.dat");
            _log = new AppLog(Path.Combine(_dataDir, "murmur.log"));
            _settings = AppSettings.Load(_settingsPath);
            if (!_settings.KeepHistory) TranscriptHistory.DeleteFile(_historyPath);
            _history = new TranscriptHistory(_historyPath, _settings.HistoryLimit);
            _claudePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            PruneStaleHandoffs(Path.Combine(exeDir, "data", "feasibility-v1", "sessions"));
            _ui = new Control();
            _ui.CreateControl();
            IntPtr force = _ui.Handle; // ensure the marshalling handle exists on the UI thread
            GC.KeepAlive(force);
            _overlay = new OverlayWindow();
            _overlay.PlaceAtTop = _settings.Placement == OverlayPlacement.TopCenter;
            _mic = new MicLevelMeter();
            _mic.Level += delegate(float level) { _overlay.PushLevel(level); };
            _mic.Error += delegate(string message) { _log.Info("Mic level meter unavailable: " + message); };
            _hotkeys = new GlobalHotkeys();
            _hotkeys.Pressed += OnHotkey;
            _hotkeys.CancelPressed += OnCancel;
            _idleTimer = new System.Windows.Forms.Timer();
            _idleTimer.Interval = 60000;
            _idleTimer.Tick += delegate { CheckIdle(); };
        }

        public void Start()
        {
            _log.Info("Murmur started.");
            CreateEngine(); // before the tray: the tray builds its menu from engine state
            _tray = new TrayIcon(BuildMenu);
            _tray.SetState(false, "Murmur");
            if (!_hotkeys.Register(_settings.Hotkey))
                _tray.Notify("Shortcut unavailable", GlobalHotkeys.Label(_settings.Hotkey) + " is used by another app. Pick another shortcut from the Murmur menu.");
            if (!File.Exists(_claudePath))
            {
                _tray.Notify("Claude Code not found", "Expected " + _claudePath);
                _log.Info("claude.exe not found at the default path.");
            }
            if (_settings.Engine != EngineMode.OnDemand) WarmEngine();
            _idleTimer.Start();
        }

        private void CreateEngine()
        {
            EngineOptions options = new EngineOptions();
            options.ClaudePath = _claudePath;
            _engine = new DictationEngine(options,
                delegate { return (ITerminalHost)new OwnedTerminalHost(); },
                delegate { return (IEngineSession)new ProfileSession(); });
            _engine.StateChanged += delegate(EngineState state) { Post(delegate { OnEngineState(state); }); };
            _engine.TakeRecording += delegate(int id) { Post(delegate { OnTakeRecording(id); }); };
            _engine.TakeTranscript += delegate(int id, string text) { Post(delegate { OnTakeTranscript(id, text); }); };
            _engine.TakeFailed += delegate(int id, string message) { Post(delegate { OnTakeFailed(id, message); }); };
            _engine.TakeDiscarded += delegate(int id) { Post(delegate { OnTakeDiscarded(id); }); };
            _engine.Failed += delegate(string message) { Post(delegate { OnEngineFailed(message); }); };
            _engine.Suspended += delegate { Post(delegate { OnSuspended(); }); };
            _engine.Diagnostic += delegate(string line) { _log.Info(line); };
        }

        private void Post(MethodInvoker action)
        {
            if (_disposed) return;
            try { _ui.BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        private void WarmEngine()
        {
            if (AccountBusy()) return;
            _lastActivityUtc = DateTime.UtcNow;
            _engine.Warm();
        }

        private bool AccountBusy()
        {
            return _accountPending || (_accountProcess != null && !_accountProcess.HasExited);
        }

        // ---- Take flow: press = start, press again = stop; Esc cancels at any point. ----
        // Every take has an engine-issued id; events for any other id are stale and ignored,
        // so a late event from a cancelled or older take can never end the current one.

        private void OnHotkey()
        {
            _lastActivityUtc = DateTime.UtcNow;
            if (_takeId == 0)
            {
                if (AccountBusy()) { _overlay.ShowMode(OverlayMode.Error, "Finish the Claude sign-in window first"); return; }
                if (!File.Exists(_claudePath)) { _overlay.ShowMode(OverlayMode.Error, "Claude Code not found"); return; }
                _takeRecording = false;
                _stopping = false;
                _overlay.ShowMode(OverlayMode.Starting, "");
                SetCancelKey(true);
                _takeId = _engine.BeginTake();
                _log.Info("Take " + _takeId + " requested (engine " + _engine.State + ").");
                return;
            }
            if (_stopping) return; // already finishing; Esc still cancels
            EngineState state = _engine.State;
            if (!_takeRecording && (state == EngineState.Starting || state == EngineState.Stopped || state == EngineState.Faulted))
            {
                OnCancel(); // Claude never started listening; nothing was said
                return;
            }
            // Recording, or hold already started but "listening" not yet seen: finalize normally.
            _stopping = true;
            _mic.Stop();
            _overlay.ShowMode(OverlayMode.Working, "");
            if (_settings.Sounds) Chimes.Play(Chime.Stop);
            _engine.EndTake(_takeId);
            _log.Info("Take " + _takeId + " stopped by shortcut.");
        }

        private void OnCancel()
        {
            if (_takeId == 0) return;
            int id = _takeId;
            _engine.CancelTake(id);
            EndTakeUi(true);
            _log.Info("Take " + id + " cancelled.");
            UnloadIfOnDemand();
        }

        private void EndTakeUi(bool hideOverlay)
        {
            _takeId = 0;
            _takeRecording = false;
            _stopping = false;
            _mic.Stop();
            SetCancelKey(false);
            if (hideOverlay) _overlay.ShowMode(OverlayMode.Hidden, "");
        }

        private void SetCancelKey(bool active) { _hotkeys.SetCancelActive(active && _settings.EscapeCancels); }

        private void UnloadIfOnDemand()
        {
            if (_settings.Engine == EngineMode.OnDemand && _takeId == 0) _engine.Unload();
        }

        private void OnEngineState(EngineState state)
        {
            _log.Info("Engine " + state + ".");
            if (_tray != null) _tray.SetState(state == EngineState.Recording, "Murmur - " + StatusText(state));
            if (state != EngineState.Recording) _mic.Stop();
            if (state == EngineState.Ready && _takeId == 0 && _settings.Engine == EngineMode.OnDemand && _unloadWhenReady)
            {
                _unloadWhenReady = false;
                _engine.Unload();
            }
        }

        private void OnTakeRecording(int id)
        {
            if (id != _takeId || _stopping) return;
            _takeRecording = true;
            _overlay.ShowMode(OverlayMode.Recording, "");
            _mic.Start(); // the meter only runs while Claude is actually listening
            if (_settings.Sounds) Chimes.Play(Chime.Start);
        }

        private void OnTakeTranscript(int id, string text)
        {
            _lastActivityUtc = DateTime.UtcNow;
            bool current = id == _takeId;
            if (current) EndTakeUi(false);
            if (_settings.KeepHistory) _history.Add(text);
            else _lastTranscript = text;
            string error;
            if (ClipboardWriter.TrySetText(text, out error))
            {
                _log.Info("Copied transcript of take " + id + " (" + text.Length + " chars).");
                if (current || !_overlay.IsShowing) _overlay.ShowMode(OverlayMode.Done, "Copied");
                if (_settings.AutoPaste && current)
                {
                    System.Windows.Forms.Timer paste = new System.Windows.Forms.Timer();
                    paste.Interval = 120;
                    paste.Tick += delegate {
                        paste.Stop(); paste.Dispose();
                        string pasteError;
                        if (!AutoPaste.TrySendCtrlV(out pasteError)) _log.Info("Auto-paste failed: " + pasteError);
                    };
                    paste.Start();
                }
            }
            else
            {
                _log.Info("Clipboard write failed: " + error);
                if (current || !_overlay.IsShowing) _overlay.ShowMode(OverlayMode.Error, "Couldn't copy - open Recent in the tray menu");
                if (_settings.Sounds) Chimes.Play(Chime.Error);
            }
            UnloadIfOnDemand();
        }

        private void OnTakeFailed(int id, string message)
        {
            _lastTakeFailure = message;
            _log.Info("Take " + id + " failed: " + message);
            if (id != _takeId) return;
            EndTakeUi(false);
            _overlay.ShowMode(OverlayMode.Error, message);
            if (_settings.Sounds) Chimes.Play(Chime.Error);
            UnloadIfOnDemand();
        }

        private void OnTakeDiscarded(int id)
        {
            _log.Info("Take " + id + " discarded.");
            if (id != _takeId) return;
            EndTakeUi(true);
            UnloadIfOnDemand();
        }

        // Engine failures not tied to a take (e.g. sign-in needed during warm-up).
        private void OnEngineFailed(string message)
        {
            if (_takeId != 0) return; // reported through TakeFailed
            if (_lastTakeFailure == message) { _lastTakeFailure = null; return; }
            _log.Info("Engine failed: " + message);
            if (_tray != null) _tray.Notify("Murmur", message);
        }

        private void CheckIdle()
        {
            if (_takeId != 0 || _settings.Engine != EngineMode.UnloadWhenIdle || _engine.State != EngineState.Ready) return;
            if ((DateTime.UtcNow - _lastActivityUtc).TotalMinutes >= _settings.IdleUnloadMinutes)
            {
                _log.Info("Unloading Claude after idle period.");
                _engine.Unload();
            }
        }

        private static string StatusText(EngineState state)
        {
            switch (state)
            {
                case EngineState.Ready: return "Ready";
                case EngineState.Starting: return "Starting Claude...";
                case EngineState.Recording: return "Listening";
                case EngineState.Finalizing: return "Transcribing...";
                case EngineState.Faulted: return "Needs attention";
                default: return "Sleeping (starts on shortcut)";
            }
        }

        // ---- Tray menu ----

        private List<TrayItem> BuildMenu()
        {
            List<TrayItem> menu = new List<TrayItem>();
            menu.Add(Header("Murmur  ·  " + StatusText(_engine.State)));
            menu.Add(Header(GlobalHotkeys.Label(_settings.Hotkey) + " to dictate"));
            menu.Add(Separator());

            TrayItem recent = Item("Recent", null);
            if (_settings.KeepHistory)
            {
                IList<HistoryEntry> items = _history.Items;
                if (items.Count == 0) recent.Children.Add(Header("Nothing yet"));
                for (int i = 0; i < items.Count && i < 12; i++)
                    recent.Children.Add(CopyItem(items[i].Text, Ago(items[i].TimeUtc)));
                if (items.Count > 0)
                {
                    recent.Children.Add(Separator());
                    recent.Children.Add(Item("Clear history", delegate { _history.Clear(); _log.Info("History cleared."); }));
                }
            }
            else
            {
                recent.Children.Add(Header("History is off"));
                if (_lastTranscript != null) recent.Children.Add(CopyItem(_lastTranscript, "last"));
            }
            menu.Add(recent);
            menu.Add(Separator());

            TrayItem shortcut = Item("Shortcut", null);
            foreach (HotkeyChoice choice in new HotkeyChoice[] { HotkeyChoice.CtrlSpace, HotkeyChoice.CtrlShiftSpace, HotkeyChoice.CtrlAltSpace, HotkeyChoice.AltShiftSpace })
            {
                HotkeyChoice pick = choice;
                shortcut.Children.Add(Choice(GlobalHotkeys.Label(choice), _settings.Hotkey == choice, delegate { ChangeHotkey(pick); }));
            }
            menu.Add(shortcut);

            TrayItem engine = Item("Claude engine", null);
            engine.Children.Add(Choice("Always ready (instant start)", _settings.Engine == EngineMode.AlwaysReady, delegate { ChangeEngine(EngineMode.AlwaysReady); }));
            engine.Children.Add(Choice("Sleep after " + _settings.IdleUnloadMinutes + " min idle", _settings.Engine == EngineMode.UnloadWhenIdle, delegate { ChangeEngine(EngineMode.UnloadWhenIdle); }));
            engine.Children.Add(Choice("Start on demand (least memory)", _settings.Engine == EngineMode.OnDemand, delegate { ChangeEngine(EngineMode.OnDemand); }));
            engine.Children.Add(Separator());
            engine.Children.Add(Item("Restart engine", delegate { RestartEngine(); }));
            menu.Add(engine);

            TrayItem options = Item("Options", null);
            options.Children.Add(Choice("Overlay at bottom", _settings.Placement == OverlayPlacement.BottomCenter, delegate { ChangePlacement(OverlayPlacement.BottomCenter); }));
            options.Children.Add(Choice("Overlay at top", _settings.Placement == OverlayPlacement.TopCenter, delegate { ChangePlacement(OverlayPlacement.TopCenter); }));
            options.Children.Add(Separator());
            options.Children.Add(Choice("Sounds", _settings.Sounds, delegate { _settings.Sounds = !_settings.Sounds; Save(); }));
            options.Children.Add(Choice("Esc cancels recording", _settings.EscapeCancels, delegate {
                _settings.EscapeCancels = !_settings.EscapeCancels; Save();
                SetCancelKey(_takeId != 0);
            }));
            options.Children.Add(Choice("Paste automatically after copy", _settings.AutoPaste, delegate { _settings.AutoPaste = !_settings.AutoPaste; Save(); }));
            options.Children.Add(Choice("Keep recent history", _settings.KeepHistory, delegate {
                _settings.KeepHistory = !_settings.KeepHistory;
                if (!_settings.KeepHistory) { _history.Clear(); TranscriptHistory.DeleteFile(_historyPath); }
                Save();
            }));
            bool autostart = SafeAutostart();
            options.Children.Add(Choice("Start with Windows", autostart, delegate {
                try { Autostart.Set(!autostart, Assembly.GetExecutingAssembly().Location); }
                catch (Exception error) { _log.Error("Autostart change failed.", error); }
            }));
            menu.Add(options);

            TrayItem account = Item("Account", null);
            account.Children.Add(Item("Sign in to Claude...", delegate { RequestAccount(true); }));
            account.Children.Add(Item("Run Claude first-time setup...", delegate { RequestAccount(false); }));
            menu.Add(account);

            menu.Add(Item("Open logs folder", delegate {
                try { Process.Start("explorer.exe", "\"" + _dataDir + "\""); } catch (Exception error) { _log.Error("Open folder failed.", error); }
            }));
            menu.Add(Separator());
            menu.Add(Item("Quit Murmur", delegate { Application.ExitThread(); }));
            return menu;
        }

        private TrayItem CopyItem(string text, string age)
        {
            return Item(Preview(text) + "   " + age, delegate {
                string error;
                if (ClipboardWriter.TrySetText(text, out error)) _overlay.ShowMode(OverlayMode.Done, "Copied");
                else _overlay.ShowMode(OverlayMode.Error, "Couldn't copy");
            });
        }

        private bool SafeAutostart() { try { return Autostart.IsEnabled(); } catch (Exception) { return false; } }

        private void ChangeHotkey(HotkeyChoice choice)
        {
            if (choice == _settings.Hotkey) return;
            // Register keeps the previous shortcut active if the new one is taken.
            if (_hotkeys.Register(choice)) { _settings.Hotkey = choice; Save(); }
            else _tray.Notify("Shortcut unavailable", GlobalHotkeys.Label(choice) + " is used by another app. Keeping " + GlobalHotkeys.Label(_settings.Hotkey) + ".");
        }

        private void ChangeEngine(EngineMode mode)
        {
            _settings.Engine = mode; Save();
            _lastActivityUtc = DateTime.UtcNow;
            _unloadWhenReady = false;
            if (mode == EngineMode.AlwaysReady) WarmEngine();
            else if (mode == EngineMode.OnDemand && _takeId == 0)
            {
                EngineState state = _engine.State;
                if (state == EngineState.Starting) _unloadWhenReady = true; // unload once it settles
                else if (state != EngineState.Stopped) _engine.Unload();
            }
        }

        private void ChangePlacement(OverlayPlacement placement)
        {
            _settings.Placement = placement; Save();
            _overlay.PlaceAtTop = placement == OverlayPlacement.TopCenter;
        }

        private void RestartEngine()
        {
            if (AccountBusy()) return;
            if (_takeId != 0) _engine.CancelTake(_takeId);
            EndTakeUi(true);
            _engine.Unload();
            if (_settings.Engine != EngineMode.OnDemand) WarmEngine();
            _log.Info("Engine restarted from menu.");
        }

        // Sign-in/setup runs Claude's own console UI against Murmur's separate profile. The engine
        // is suspended first (child closed, starts refused) so the two never share the profile.
        private void RequestAccount(bool login)
        {
            if (AccountBusy()) return;
            if (!File.Exists(_claudePath)) { _tray.Notify("Claude Code not found", _claudePath); return; }
            if (_takeId != 0) _engine.CancelTake(_takeId);
            EndTakeUi(true);
            _accountPending = true;
            _accountLogin = login;
            _engine.Suspend(); // continues in OnSuspended
        }

        private void OnSuspended()
        {
            if (_accountPending) LaunchAccountWindow();
        }

        private void LaunchAccountWindow()
        {
            _accountPending = false;
            ProfileSession session = null;
            try
            {
                session = new ProfileSession();
                StringBuilder arguments = new StringBuilder();
                if (_accountLogin) arguments.Append("auth login");
                else foreach (string argument in session.Arguments) { if (arguments.Length > 0) arguments.Append(' '); arguments.Append(Murmur.Transport.OwnedTerminal.QuoteArgument(argument)); }
                ProcessStartInfo info = new ProcessStartInfo(_claudePath, arguments.ToString());
                info.UseShellExecute = false;
                info.CreateNoWindow = false;
                info.WorkingDirectory = session.Workspace;
                foreach (KeyValuePair<string, string> entry in session.Environment)
                    if (entry.Value == null) info.EnvironmentVariables.Remove(entry.Key);
                    else info.EnvironmentVariables[entry.Key] = entry.Value;
                ProfileSession owned = session;
                _accountProcess = Process.Start(info);
                session = null;
                _accountProcess.EnableRaisingEvents = true;
                _accountProcess.Exited += delegate {
                    owned.Dispose();
                    Post(delegate {
                        _log.Info("Claude account window closed.");
                        _engine.Resume();
                        if (_settings.Engine != EngineMode.OnDemand) WarmEngine();
                    });
                };
                _log.Info(_accountLogin ? "Opened Claude sign-in." : "Opened Claude first-time setup.");
            }
            catch (Exception error)
            {
                if (session != null) session.Dispose();
                _accountProcess = null;
                _log.Error("Could not open Claude account window.", error);
                _tray.Notify("Murmur", "Could not open Claude: " + error.Message);
                _engine.Resume();
                if (_settings.Engine != EngineMode.OnDemand) WarmEngine();
            }
        }

        // A crash between Claude's export and Murmur's read could leave a plaintext handoff file
        // in an old session folder. Remove only those exact Murmur-created files, and never
        // follow a link out of Murmur's own data folder.
        private void PruneStaleHandoffs(string sessionsRoot)
        {
            try
            {
                if (!Directory.Exists(sessionsRoot)) return;
                for (string current = Path.GetFullPath(sessionsRoot); current != null; current = Path.GetDirectoryName(current))
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return;
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sessionsRoot)), ".murmur-owned-root"))) return;
                foreach (string directory in Directory.GetDirectories(sessionsRoot))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    foreach (string file in Directory.GetFiles(directory, "handoff.txt*"))
                    {
                        string name = Path.GetFileName(file);
                        if (name == "handoff.txt" || name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase))
                        {
                            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                        }
                    }
                }
            }
            catch (Exception error) { _log.Error("Stale handoff cleanup failed.", error); }
        }

        private void Save()
        {
            try { _settings.Save(_settingsPath); }
            catch (Exception error) { _log.Error("Settings save failed.", error); }
        }

        private static TrayItem Item(string text, Action click) { TrayItem item = new TrayItem(); item.Text = text; item.OnClick = click; return item; }
        private static TrayItem Choice(string text, bool on, Action click) { TrayItem item = Item(text, click); item.Checked = on; return item; }
        private static TrayItem Header(string text) { TrayItem item = new TrayItem(); item.Text = text; item.IsHeader = true; item.Enabled = false; return item; }
        private static TrayItem Separator() { TrayItem item = new TrayItem(); item.IsSeparator = true; return item; }

        private static string Preview(string text)
        {
            string flat = text.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            return flat.Length <= 46 ? flat : flat.Substring(0, 45).TrimEnd() + "…";
        }

        private static string Ago(DateTime utc)
        {
            TimeSpan age = DateTime.UtcNow - utc;
            if (age.TotalMinutes < 1) return "now";
            if (age.TotalHours < 1) return (int)age.TotalMinutes + "m";
            if (age.TotalDays < 1) return (int)age.TotalHours + "h";
            return (int)age.TotalDays + "d";
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _overlay.ShowMode(OverlayMode.Hidden, "");
            _idleTimer.Stop(); _idleTimer.Dispose();
            _hotkeys.Dispose();
            _mic.Dispose();
            if (_tray != null) _tray.Dispose(); // icon disappears immediately
            if (_engine != null) _engine.Dispose(); // closes the owned Claude child (bounded wait)
            if (_accountProcess != null)
            {
                try { if (!_accountProcess.HasExited) _accountProcess.Kill(); } catch (Exception) { }
                _accountProcess.Dispose();
            }
            _overlay.Dispose();
            _ui.Dispose();
            _log.Info("Murmur exited.");
        }
    }
}

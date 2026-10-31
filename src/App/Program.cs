using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
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
        [STAThread]
        public static int Main(string[] args)
        {
            // Invoked by the owned Claude child as VISUAL/EDITOR on Ctrl+G. Copies the unsent
            // prompt to the session handoff file, then empties the prompt so the composer is
            // clean for the next take. Must run before any single-instance or UI logic.
            if (args.Length == 2 && args[0] == "--export-prompt") return ExportPrompt(args[1]);

            bool owned;
            using (Mutex app = new Mutex(true, "Local\\Murmur.App", out owned))
            {
                if (!owned) return 0; // already running in the tray
                using (Mutex probe = new Mutex(false, "Local\\Murmur.Feasibility.v1"))
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
                        Application.EnableVisualStyles();
                        Application.SetCompatibleTextRenderingDefault(false);
                        using (TrayApp tray = new TrayApp()) { tray.Start(); Application.Run(); }
                        return 0;
                    }
                    finally { probe.ReleaseMutex(); }
                }
            }
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
        private readonly string _dataDir, _settingsPath, _claudePath;
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
        private bool _takeRequested, _disposed;
        private DateTime _lastActivityUtc = DateTime.UtcNow;
        private Process _accountProcess;

        public TrayApp()
        {
            string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _dataDir = Path.Combine(exeDir, "data", "app");
            Directory.CreateDirectory(_dataDir);
            _settingsPath = Path.Combine(_dataDir, "settings.ini");
            _log = new AppLog(Path.Combine(_dataDir, "murmur.log"));
            _settings = AppSettings.Load(_settingsPath);
            _history = new TranscriptHistory(Path.Combine(_dataDir, "history.dat"), _settings.HistoryLimit);
            _claudePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
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
            _tray = new TrayIcon(BuildMenu);
            _tray.SetState(false, "Murmur");
            if (!_hotkeys.Register(_settings.Hotkey))
                _tray.Notify("Shortcut unavailable", GlobalHotkeys.Label(_settings.Hotkey) + " is used by another app. Pick another shortcut from the Murmur menu.");
            if (!File.Exists(_claudePath))
            {
                _tray.Notify("Claude Code not found", "Expected " + _claudePath);
                _log.Info("claude.exe not found at the default path.");
            }
            CreateEngine();
            if (_settings.Engine != EngineMode.OnDemand) _engine.Warm();
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
            _engine.TranscriptReady += delegate(string text) { Post(delegate { OnTranscript(text); }); };
            _engine.Failed += delegate(string message) { Post(delegate { OnFailed(message); }); };
            _engine.Diagnostic += delegate(string line) { _log.Info(line); };
        }

        private void Post(MethodInvoker action)
        {
            if (_disposed) return;
            try { _ui.BeginInvoke(action); } catch (InvalidOperationException) { }
        }

        // ---- Hotkey flow: press = start, press again = stop; Esc cancels. ----

        private void OnHotkey()
        {
            _lastActivityUtc = DateTime.UtcNow;
            EngineState state = _engine.State;
            if (state == EngineState.Recording)
            {
                _takeRequested = false;
                _mic.Stop();
                SetCancelKey(false);
                _overlay.ShowMode(OverlayMode.Working, "");
                if (_settings.Sounds) Chimes.Play(Chime.Stop);
                _engine.EndTake();
                _log.Info("Take stopped by shortcut.");
            }
            else if (_takeRequested)
            {
                OnCancel(); // pressed again before Claude started listening
            }
            else if (state == EngineState.Finalizing)
            {
                // Still finishing the previous take; ignore.
            }
            else
            {
                if (!File.Exists(_claudePath)) { _overlay.ShowMode(OverlayMode.Error, "Claude Code not found"); return; }
                _takeRequested = true;
                SetCancelKey(true);
                _overlay.ShowMode(OverlayMode.Starting, "");
                _mic.Start();
                _engine.BeginTake();
                _log.Info("Take requested (engine " + state + ").");
            }
        }

        private void OnCancel()
        {
            if (!_takeRequested && _engine.State != EngineState.Recording) return;
            _takeRequested = false;
            _mic.Stop();
            SetCancelKey(false);
            _engine.CancelTake();
            _overlay.ShowMode(OverlayMode.Hidden, "");
            _log.Info("Take cancelled.");
        }

        private void SetCancelKey(bool active) { _hotkeys.SetCancelActive(active && _settings.EscapeCancels); }

        private void OnEngineState(EngineState state)
        {
            _log.Info("Engine " + state + ".");
            bool recording = state == EngineState.Recording;
            if (_tray != null) _tray.SetState(recording, "Murmur — " + StatusText(state));
            if (recording && _takeRequested)
            {
                _takeRequested = false;
                _overlay.ShowMode(OverlayMode.Recording, "");
                if (_settings.Sounds) Chimes.Play(Chime.Start);
            }
        }

        private void OnTranscript(string text)
        {
            _lastActivityUtc = DateTime.UtcNow;
            string error;
            if (!ClipboardWriter.TrySetText(text, out error))
            {
                _log.Info("Clipboard write failed: " + error);
                _overlay.ShowMode(OverlayMode.Error, "Couldn't copy — text saved in Recent");
                if (_settings.KeepHistory) _history.Add(text);
                return;
            }
            if (_settings.KeepHistory) _history.Add(text);
            _log.Info("Copied transcript (" + text.Length + " chars).");
            _overlay.ShowMode(OverlayMode.Done, "Copied");
            if (_settings.AutoPaste)
            {
                System.Windows.Forms.Timer paste = new System.Windows.Forms.Timer();
                paste.Interval = 120;
                paste.Tick += delegate { paste.Stop(); paste.Dispose(); AutoPaste.SendCtrlV(); };
                paste.Start();
            }
            if (_settings.Engine == EngineMode.OnDemand) _engine.Unload();
        }

        private void OnFailed(string message)
        {
            _log.Info("Failed: " + message);
            bool inTake = _takeRequested || _overlay.Visible;
            _takeRequested = false;
            _mic.Stop();
            SetCancelKey(false);
            if (inTake)
            {
                _overlay.ShowMode(OverlayMode.Error, message);
                if (_settings.Sounds) Chimes.Play(Chime.Error);
            }
            else if (_tray != null) _tray.Notify("Murmur", message);
            if (_settings.Engine == EngineMode.OnDemand) _engine.Unload();
        }

        private void CheckIdle()
        {
            if (_settings.Engine != EngineMode.UnloadWhenIdle || _engine.State != EngineState.Ready) return;
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
                case EngineState.Starting: return "Starting Claude…";
                case EngineState.Recording: return "Listening";
                case EngineState.Finalizing: return "Transcribing…";
                case EngineState.Faulted: return "Needs attention";
                default: return "Sleeping (starts on shortcut)";
            }
        }

        // ---- Tray menu ----

        private List<TrayItem> BuildMenu()
        {
            List<TrayItem> menu = new List<TrayItem>();
            menu.Add(Header("Murmur · " + StatusText(_engine.State)));
            menu.Add(Header(GlobalHotkeys.Label(_settings.Hotkey) + " to dictate"));
            menu.Add(Separator());

            TrayItem recent = Item("Recent", null);
            IList<HistoryEntry> items = _history.Items;
            if (!_settings.KeepHistory) recent.Children.Add(Header("History is off"));
            else if (items.Count == 0) recent.Children.Add(Header("Nothing yet"));
            for (int i = 0; i < items.Count && i < 12; i++)
            {
                string text = items[i].Text;
                recent.Children.Add(Item(Preview(text) + "   " + Ago(items[i].TimeUtc), delegate {
                    string error;
                    if (ClipboardWriter.TrySetText(text, out error)) _overlay.ShowMode(OverlayMode.Done, "Copied");
                    else _overlay.ShowMode(OverlayMode.Error, "Couldn't copy");
                }));
            }
            if (items.Count > 0)
            {
                recent.Children.Add(Separator());
                recent.Children.Add(Item("Clear history", delegate { _history.Clear(); _log.Info("History cleared."); }));
            }
            menu.Add(recent);
            menu.Add(Separator());

            TrayItem shortcut = Item("Shortcut", null);
            foreach (HotkeyChoice choice in new HotkeyChoice[] { HotkeyChoice.CtrlSpace, HotkeyChoice.CtrlShiftSpace, HotkeyChoice.CtrlAltSpace, HotkeyChoice.AltShiftSpace })
            {
                HotkeyChoice pick = choice;
                TrayItem option = Item(GlobalHotkeys.Label(choice), delegate { ChangeHotkey(pick); });
                option.Checked = _settings.Hotkey == choice;
                shortcut.Children.Add(option);
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
            options.Children.Add(Choice("Esc cancels recording", _settings.EscapeCancels, delegate { _settings.EscapeCancels = !_settings.EscapeCancels; Save(); }));
            options.Children.Add(Choice("Paste automatically after copy", _settings.AutoPaste, delegate { _settings.AutoPaste = !_settings.AutoPaste; Save(); }));
            options.Children.Add(Choice("Keep recent history", _settings.KeepHistory, delegate {
                _settings.KeepHistory = !_settings.KeepHistory;
                if (!_settings.KeepHistory) _history.Clear();
                Save();
            }));
            bool autostart = SafeAutostart();
            options.Children.Add(Choice("Start with Windows", autostart, delegate {
                try { Autostart.Set(!autostart, Assembly.GetExecutingAssembly().Location); }
                catch (Exception error) { _log.Error("Autostart change failed.", error); }
            }));
            menu.Add(options);

            TrayItem account = Item("Account", null);
            account.Children.Add(Item("Sign in to Claude…", delegate { RunAccount(true); }));
            account.Children.Add(Item("Run Claude first-time setup…", delegate { RunAccount(false); }));
            menu.Add(account);

            menu.Add(Item("Open logs folder", delegate {
                try { Process.Start("explorer.exe", "\"" + _dataDir + "\""); } catch (Exception error) { _log.Error("Open folder failed.", error); }
            }));
            menu.Add(Separator());
            menu.Add(Item("Quit Murmur", delegate { Application.ExitThread(); }));
            return menu;
        }

        private bool SafeAutostart() { try { return Autostart.IsEnabled(); } catch (Exception) { return false; } }

        private void ChangeHotkey(HotkeyChoice choice)
        {
            HotkeyChoice previous = _settings.Hotkey;
            if (_hotkeys.Register(choice)) { _settings.Hotkey = choice; Save(); }
            else
            {
                _hotkeys.Register(previous);
                _tray.Notify("Shortcut unavailable", GlobalHotkeys.Label(choice) + " is used by another app.");
            }
        }

        private void ChangeEngine(EngineMode mode)
        {
            _settings.Engine = mode; Save();
            _lastActivityUtc = DateTime.UtcNow;
            if (mode == EngineMode.AlwaysReady) _engine.Warm();
            else if (mode == EngineMode.OnDemand && _engine.State == EngineState.Ready) _engine.Unload();
        }

        private void ChangePlacement(OverlayPlacement placement)
        {
            _settings.Placement = placement; Save();
            _overlay.PlaceAtTop = placement == OverlayPlacement.TopCenter;
        }

        private void RestartEngine()
        {
            _takeRequested = false;
            _mic.Stop();
            SetCancelKey(false);
            _overlay.ShowMode(OverlayMode.Hidden, "");
            _engine.Unload();
            if (_settings.Engine != EngineMode.OnDemand) _engine.Warm();
            _log.Info("Engine restarted from menu.");
        }

        // Opens a visible console that runs Claude's own sign-in (or interactive first-run
        // setup) against Murmur's separate profile. Murmur never reads the credentials.
        private void RunAccount(bool login)
        {
            if (_accountProcess != null && !_accountProcess.HasExited) return;
            if (!File.Exists(_claudePath)) { _tray.Notify("Claude Code not found", _claudePath); return; }
            _engine.Unload();
            ProfileSession session = new ProfileSession();
            try
            {
                StringBuilder arguments = new StringBuilder();
                if (login) arguments.Append("auth login");
                else foreach (string argument in session.Arguments) { if (arguments.Length > 0) arguments.Append(' '); arguments.Append(Murmur.Transport.OwnedTerminal.QuoteArgument(argument)); }
                ProcessStartInfo info = new ProcessStartInfo(_claudePath, arguments.ToString());
                info.UseShellExecute = false;
                info.CreateNoWindow = false;
                info.WorkingDirectory = session.Workspace;
                foreach (KeyValuePair<string, string> entry in session.Environment)
                    if (entry.Value == null) info.EnvironmentVariables.Remove(entry.Key);
                    else info.EnvironmentVariables[entry.Key] = entry.Value;
                _accountProcess = Process.Start(info);
                _accountProcess.EnableRaisingEvents = true;
                _accountProcess.Exited += delegate {
                    session.Dispose();
                    Post(delegate { if (_settings.Engine != EngineMode.OnDemand) _engine.Warm(); });
                };
                _log.Info(login ? "Opened Claude sign-in." : "Opened Claude first-time setup.");
            }
            catch (Exception error)
            {
                session.Dispose();
                _log.Error("Could not open Claude account window.", error);
                _tray.Notify("Murmur", "Could not open Claude: " + error.Message);
            }
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
            string flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
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
            _idleTimer.Stop(); _idleTimer.Dispose();
            _hotkeys.Dispose();
            _mic.Dispose();
            if (_engine != null) _engine.Dispose();
            if (_tray != null) _tray.Dispose();
            _overlay.Dispose();
            _ui.Dispose();
            _log.Info("Murmur exited.");
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Murmur.Handoff;
using Murmur.Transport;
using Message = Murmur.Probe.ProbeMessage;

namespace Murmur.Probe
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                ProbeOptions options = ProbeOptions.Parse(args);
                if (options.Mode == ProbeMode.Help) { Help(); return 0; }
                if (options.Mode == ProbeMode.Export)
                {
                    int code = PromptHandoff.Export(options.PromptPath,
                        Environment.GetEnvironmentVariable("MURMUR_SESSION_DIRECTORY"),
                        Environment.GetEnvironmentVariable("MURMUR_HANDOFF_PATH"));
                    if (code != 0) Console.Error.WriteLine("Murmur handoff rejected (code " + code + ").");
                    return code;
                }
                string executable = options.ClaudePath ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
                if (executable.Length < 3 || !Char.IsLetter(executable[0]) || executable[1] != ':' ||
                    (executable[2] != '\\' && executable[2] != '/') || !File.Exists(executable))
                    throw new ArgumentException("An existing absolute local path to claude.exe is required.");
                using (Mutex instance = new Mutex(false, "Local\\Murmur.Feasibility.v1"))
                {
                    bool acquired;
                    try { acquired = instance.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new InvalidOperationException("Another Murmur feasibility login/probe is running.");
                    try
                    {
                        IsolatedProfile profile = new IsolatedProfile();
                        try { return options.Mode == ProbeMode.Login ? Login(executable, profile) : Probe(executable, profile); }
                        finally { profile.CleanupKnownFiles(); }
                    }
                    finally { instance.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Murmur probe: " + error.Message);
                return 1;
            }
        }

        private static void Help()
        {
            Console.WriteLine("Murmur feasibility harness. Default/help are inert; no profile or provider is opened.");
            Console.WriteLine("After user approval: --login (separate Claude profile), then --probe (one voice take).");
            Console.WriteLine("Optional: --claude <absolute local path to claude.exe>.");
            Console.WriteLine("Live modes may access microphone/login/browser and have not yet been authorized.");
            Console.WriteLine("This is not the finished tray app, and does not access the clipboard.");
        }

        private static int Login(string executable, IsolatedProfile profile)
        {
            Console.WriteLine("Sign in to the separate Murmur profile: " + profile.ProfileDirectory);
            Console.WriteLine("This can open a browser. Murmur supplies a separate config directory; CLI-wide state isolation still needs verification.");
            ProcessStartInfo info = new ProcessStartInfo(executable, "auth login");
            info.UseShellExecute = false;
            info.WorkingDirectory = profile.Workspace;
            foreach (var entry in profile.EnvironmentOverrides)
                if (entry.Value == null) info.EnvironmentVariables.Remove(entry.Key);
                else info.EnvironmentVariables[entry.Key] = entry.Value;
            using (Process child = Process.Start(info)) { child.WaitForExit(); return child.ExitCode; }
        }

        private static int Probe(string executable, IsolatedProfile profile)
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.WriteLine("ONE-TAKE LIVE PROBE. Only a newly created, owned Claude CLI child is controlled.");
            Console.WriteLine("V: inspect its terminal screen. Before R only: Enter/Up/Down answer its setup prompts.");
            Console.WriteLine("When ready, R: record; S: release to finalize; V: inspect; E: export/display the final unsent buffer.");
            Console.WriteLine("C: cancel; Q: quit. No global shortcut or clipboard is used by this harness.");
            Console.WriteLine("Verify actual listening and finalized text in the diagnostic screen; a keypress is not proof.");
            using (ProbeInbox inbox = new ProbeInbox())
            using (OwnedTerminal terminal = new OwnedTerminal())
            using (FileSystemWatcher watcher = new FileSystemWatcher(profile.SessionDirectory))
            using (Timer hold = new Timer(delegate { Post(inbox, new Message("tick")); }, null, Timeout.Infinite, Timeout.Infinite))
            using (Timer timeout = new Timer(delegate { Post(inbox, new Message("timeout")); }, null, Timeout.Infinite, Timeout.Infinite))
            {
                TerminalScreen screen = new TerminalScreen(OwnedTerminal.TerminalColumns, OwnedTerminal.TerminalRows);
                ProbeControl control = new ProbeControl();
                terminal.Output += delegate(string chunk) { screen.Feed(chunk, delegate(string reply) {
                    if (!terminal.TryWrite(reply)) Post(inbox, new Message("reply-dropped"));
                }); };
                terminal.Closed += delegate(string reason) { Post(inbox, new Message("closed") { Detail = reason }); };
                watcher.Filter = Path.GetFileName(profile.ExportPath);
                watcher.Created += delegate { Post(inbox, new Message("handoff")); };
                watcher.Renamed += delegate { Post(inbox, new Message("handoff")); };
                watcher.Error += delegate { Post(inbox, new Message("watch-error")); };
                watcher.EnableRaisingEvents = true;
                Stopwatch startup = Stopwatch.StartNew();
                terminal.Start(executable, profile.Arguments, profile.Workspace, profile.EnvironmentOverrides);
                Console.WriteLine("Owned child created in " + startup.ElapsedMilliseconds + " ms; Claude readiness latency is still unmeasured.");
                new Thread(delegate() {
                    try { while (true) Post(inbox, new Message("key") { Key = Console.ReadKey(true).Key }); }
                    catch { Post(inbox, new Message("input-ended")); }
                }) { IsBackground = true, Name = "Murmur probe keys" }.Start();
                ConsoleCancelEventHandler cancel = delegate(object sender, ConsoleCancelEventArgs eventArgs) {
                    eventArgs.Cancel = true; Post(inbox, new Message("key") { Key = ConsoleKey.C });
                };
                Console.CancelKeyPress += cancel;
                try
                {
                    while (true)
                    {
                        Message message = inbox.Take();
                        if (message.Kind == "tick")
                        {
                            string repeat = control.RepeatInput();
                            if (repeat != null && !terminal.TryWrite(repeat)) throw new IOException("Owned terminal input is unavailable.");
                        }
                        else if (message.Kind == "key")
                        {
                            ProbeAction action = control.Key(message.Key);
                            if (action == ProbeAction.Inspect) Console.WriteLine("\n--- Owned child screen ---\n" + SafeDisplay(screen.Snapshot()) + "\n--- End screen ---");
                            else if (action == ProbeAction.SetupInput)
                            {
                                if (!terminal.TryWrite(message.Key == ConsoleKey.Enter ? "\r" : message.Key == ConsoleKey.UpArrow ? "\x1b[A" : "\x1b[B"))
                                    throw new IOException("Owned terminal input is unavailable during setup.");
                            }
                            else if (action == ProbeAction.StartHold) hold.Change(0, 50);
                            else if (action == ProbeAction.StopHold) { hold.Change(Timeout.Infinite, Timeout.Infinite); Console.WriteLine("\nReleased hold. Inspect finalization with V before E."); }
                            else if (action == ProbeAction.Export)
                            {
                                if (!terminal.TryWrite("\x07")) throw new IOException("Cannot request the external editor.");
                                timeout.Change(15000, Timeout.Infinite);
                                if (File.Exists(profile.ExportPath)) Post(inbox, new Message("handoff"));
                            }
                            else if (action == ProbeAction.Cancel || action == ProbeAction.Quit)
                            {
                                hold.Change(Timeout.Infinite, Timeout.Infinite);
                                if (action == ProbeAction.Cancel) terminal.TryWrite("\x1b");
                                Console.WriteLine("\nProbe cancelled; only its owned child is being closed.");
                                return 2;
                            }
                        }
                        else if ((message.Kind == "handoff" || message.Kind == "timeout") && control.Phase == ProbePhase.Exporting && File.Exists(profile.ExportPath))
                        {
                            string text = ReadHandoff(profile.ExportPath);
                            if (String.IsNullOrWhiteSpace(text)) throw new IOException("Editor buffer was empty; this does not establish successful dictation.");
                            if (!control.AcceptExport()) continue;
                            Console.WriteLine("\nReceived " + text.Length + " characters from the unsent editor buffer. Clipboard untouched.");
                            Console.WriteLine("Compare this diagnostic text with what you spoke:\n" + SafeDisplay(text));
                            File.Delete(profile.ExportPath);
                            return 0;
                        }
                        else if (message.Kind == "timeout" && control.Phase == ProbePhase.Exporting)
                            throw new TimeoutException("No prompt export arrived within 15 seconds. No transcript was accepted.");
                        else if (message.Kind == "closed" || message.Kind == "watch-error" || message.Kind == "input-ended" || message.Kind == "reply-dropped" || message.Kind == "overflow")
                            throw new IOException("Probe stopped: " + (message.Detail ?? message.Kind));
                    }
                }
                finally
                {
                    Console.CancelKeyPress -= cancel;
                    hold.Change(Timeout.Infinite, Timeout.Infinite);
                    timeout.Change(Timeout.Infinite, Timeout.Infinite);
                }
            }
        }

        private static string ReadHandoff(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > 1048576) throw new IOException("Prompt handoff exceeded the size limit.");
                byte[] bytes = new byte[(int)stream.Length];
                int position = 0;
                while (position < bytes.Length)
                {
                    int count = stream.Read(bytes, position, bytes.Length - position);
                    if (count == 0) throw new IOException("Incomplete prompt handoff.");
                    position += count;
                }
                if (stream.ReadByte() != -1) throw new IOException("Prompt handoff changed while reading.");
                string value = new UTF8Encoding(false, true).GetString(bytes);
                return value.Length > 0 && value[0] == '\ufeff' ? value.Substring(1) : value;
            }
        }

        internal static string SafeDisplay(string text)
        {
            StringBuilder display = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') continue; display.Append('\u240d'); }
                else if (Char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) display.Append('\u2423');
                else display.Append(Char.IsControl(c) && c != '\n' && c != '\t' ? '\u2423' : c);
            }
            return display.ToString();
        }

        private static void Post(ProbeInbox inbox, Message message) { inbox.Post(message); }
    }
}

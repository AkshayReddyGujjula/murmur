using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Murmur.Next.Windows;

namespace Murmur.Next.Probe
{
    // Explicitly opt-in feasibility tool, NOT the shipping tray app.
    // Default invocation does not create a profile, start a provider, or open audio.
    internal static class Program
    {
        private static readonly object repeatLock = new object();
        private static bool holding;

        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--export-prompt")
                return PromptExport.Run(args[1], Environment.GetEnvironmentVariable("MURMUR_SESSION_TEMP"),
                    Environment.GetEnvironmentVariable("MURMUR_EXPORT_FILE"));
            if (args.Length == 0 || (args[0] != "--live-probe" && args[0] != "--login"))
            {
                Console.WriteLine("Murmur feasibility probe. No provider has been started.");
                Console.WriteLine("After explicit approval only: --login, then --live-probe.");
                Console.WriteLine("Both use a separate Murmur-owned Claude profile, not your existing session.");
                return args.Length == 0 || args[0] == "--help" ? 0 : 2;
            }
            try { return RunLive(args); }
            catch (Exception error)
            {
                Console.Error.WriteLine("Probe failed (" + error.GetType().Name + "): " + error.Message);
                return 1;
            }
        }

        private static int RunLive(string[] args)
        {
            string claude = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
            if (args.Length == 3 && args[1] == "--claude-path") claude = args[2];
            else if (args.Length != 1) throw new ArgumentException("Use --claude-path followed by an absolute executable path.");
            if (!Path.IsPathRooted(claude) || !File.Exists(claude)) throw new FileNotFoundException("Claude CLI executable was not found.");

            string profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MurmurNext", "ClaudeProfile");
            EnsureOwnedProfile(profile);
            string work = Path.Combine(profile, "Workspace");
            Directory.CreateDirectory(work);
            string temp = Path.Combine(profile, "Sessions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            string export = Path.Combine(temp, "prompt-export.txt");
            string settings = Path.Combine(temp, "voice-settings.json");
            string mcp = Path.Combine(temp, "mcp.json");
            File.WriteAllText(settings, "{\"voice\":{\"enabled\":true,\"mode\":\"hold\",\"autoSubmit\":false},\"language\":\"en\",\"editorMode\":\"normal\"}", new UTF8Encoding(false));
            File.WriteAllText(mcp, "{\"mcpServers\":{}}", new UTF8Encoding(false));
            Dictionary<string, string> environment = ChildEnvironment(profile, temp, export);
            if (args[0] == "--login")
            {
                Console.WriteLine("Signing in ONLY to the separate Murmur profile: " + profile);
                ProcessStartInfo login = new ProcessStartInfo(claude, "auth login");
                login.UseShellExecute = false;
                login.WorkingDirectory = work;
                foreach (KeyValuePair<string, string> item in environment)
                {
                    if (item.Value == null) login.EnvironmentVariables.Remove(item.Key);
                    else login.EnvironmentVariables[item.Key] = item.Value;
                }
                using (Process child = Process.Start(login)) { child.WaitForExit(); return child.ExitCode; }
            }

            Console.WriteLine("LIVE PROBE: a separate hidden Claude CLI will start using the Murmur profile.");
            Console.WriteLine("Its terminal output is displayed here in memory; no transcript log is written.");
            Console.WriteLine("R = hold Space to record; S = release to finalize; E = export finalized prompt; C = cancel; Q = quit.");
            Console.WriteLine("Before the FIRST R only: Enter and Up/Down can answer the owned child's setup prompts.");
            Console.WriteLine("Wait for Claude's ready prompt before R, and final unsent text before E.");
            Console.WriteLine("This probe does NOT register a global shortcut or access the clipboard.");
            using (PseudoTerminal terminal = new PseudoTerminal())
            {
                bool everRecorded = false;
                terminal.Output += delegate(string text) { Console.Write(text); };
                terminal.Faulted += delegate(Exception error) { Console.Error.WriteLine("\nOwned terminal ended: " + error.GetType().Name); };
                terminal.Start(claude, new string[] { "--safe-mode", "--setting-sources", "", "--settings", settings,
                    "--tools", "", "--strict-mcp-config", "--mcp-config", mcp }, work, environment);
                using (Timer repeat = new Timer(delegate(object ignored) {
                    lock (repeatLock) { if (holding && !terminal.Send(" ")) holding = false; }
                }, null, 0, 50))
                {
                    while (true)
                    {
                        ConsoleKey key = Console.ReadKey(true).Key;
                        if (key == ConsoleKey.R) { everRecorded = true; lock (repeatLock) { holding = true; } }
                        else if (!everRecorded && key == ConsoleKey.Enter) terminal.Send("\r");
                        else if (!everRecorded && key == ConsoleKey.UpArrow) terminal.Send("\x1b[A");
                        else if (!everRecorded && key == ConsoleKey.DownArrow) terminal.Send("\x1b[B");
                        else if (key == ConsoleKey.S || key == ConsoleKey.C || key == ConsoleKey.Q)
                        {
                            lock (repeatLock) { holding = false; }
                            if (key == ConsoleKey.C) terminal.Send("\x1b");
                            if (key == ConsoleKey.Q) break;
                        }
                        else if (key == ConsoleKey.E)
                        {
                            lock (repeatLock) { if (holding) { Console.WriteLine("\nStop recording before exporting."); continue; } }
                            // External editor, never Enter. No dictated prompt is submitted.
                            if (!terminal.Send("\x07")) throw new IOException("Owned terminal input is unavailable.");
                            if (!WaitForExport(export, TimeSpan.FromSeconds(12)))
                                Console.WriteLine("\nNo export was received. Check that the owned child is ready and finalized.");
                            else
                            {
                                byte[] bytes = File.ReadAllBytes(export);
                                string hash;
                                using (SHA256 sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                                Console.WriteLine("\nEXPORTED " + bytes.Length + " bytes; SHA256 " + hash + ". Clipboard untouched.");
                                File.Delete(export);
                            }
                        }
                    }
                }
            }
            return 0;
        }

        private static bool WaitForExport(string path, TimeSpan timeout)
        {
            Stopwatch watch = Stopwatch.StartNew();
            while (watch.Elapsed < timeout) { if (File.Exists(path)) return true; Thread.Sleep(50); }
            return false;
        }

        private static void EnsureOwnedProfile(string profile)
        {
            string marker = Path.Combine(profile, ".murmur-owned-profile");
            if (Directory.Exists(profile) && !File.Exists(marker))
                throw new IOException("Refusing an existing profile without a Murmur ownership marker.");
            Directory.CreateDirectory(profile);
            if (!File.Exists(marker)) File.WriteAllText(marker, "Murmur isolated dictation profile v1\n");
        }

        private static Dictionary<string, string> ChildEnvironment(string profile, string temp, string export)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            {
                string key = (string)item.Key;
                if (key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("ANTHROPIC", StringComparison.OrdinalIgnoreCase) ||
                    key.StartsWith("CODEX", StringComparison.OrdinalIgnoreCase)) result[key] = null;
            }
            result["CLAUDE_CONFIG_DIR"] = profile;
            result["CLAUDE_CODE_TMPDIR"] = temp;
            result["TEMP"] = temp; result["TMP"] = temp;
            result["DISABLE_AUTOUPDATER"] = "1";
            result["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            result["MURMUR_SESSION_TEMP"] = temp;
            result["MURMUR_EXPORT_FILE"] = export;
            result["EDITOR"] = PseudoTerminal.QuoteArgument(Assembly.GetExecutingAssembly().Location) + " --export-prompt";
            result["VISUAL"] = result["EDITOR"];
            result["TERM"] = "xterm-256color";
            return result;
        }
    }
}

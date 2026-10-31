using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Murmur.Transport;

namespace Murmur.Probe
{
    // Construct ONLY after explicit --login or --probe. No user's existing profile
    // is inspected, copied, linked, reconfigured or used for authentication.
    internal sealed class IsolatedProfile
    {
        private const string OwnerMarker = "Murmur feasibility profile v1";
        internal static Func<string> RootOverride { get; set; }
        public readonly string ProfileDirectory, SessionDirectory, Workspace, ExportPath;
        public readonly Dictionary<string, string> EnvironmentOverrides;
        public readonly string[] Arguments;

        public IsolatedProfile()
        {
            string root = RootOverride == null ? DefaultRoot() : RootOverride();
            root = Path.GetFullPath(root);
            RejectLinks(root);
            string marker = Path.Combine(root, ".murmur-owned-root");
            if (Directory.Exists(root) && !File.Exists(marker)) throw new IOException("Existing data directory has no Murmur ownership marker; refusing to use it.");
            if (File.Exists(marker))
            {
                RejectLinks(marker);
                if (new FileInfo(marker).Length > 128) throw new IOException("Invalid Murmur ownership marker.");
                using (StreamReader reader = new StreamReader(marker))
                    if (reader.ReadLine() != OwnerMarker) throw new IOException("Invalid Murmur ownership marker.");
            }
            Directory.CreateDirectory(root);
            if (!File.Exists(marker))
                using (FileStream file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(OwnerMarker + "\n"); file.Write(bytes, 0, bytes.Length);
                }
            ProfileDirectory = Path.Combine(root, "profile");
            SessionDirectory = Path.Combine(root, "sessions", Guid.NewGuid().ToString("N"));
            Workspace = Path.Combine(SessionDirectory, "workspace");
            ExportPath = Path.Combine(SessionDirectory, "handoff.txt");
            RejectLinks(ProfileDirectory); RejectLinks(Workspace);
            Directory.CreateDirectory(ProfileDirectory); Directory.CreateDirectory(Workspace);
            string settings = Path.Combine(SessionDirectory, "settings.json");
            string mcp = Path.Combine(SessionDirectory, "mcp.json");
            File.WriteAllText(settings, "{\"voice\":{\"enabled\":true,\"mode\":\"hold\",\"autoSubmit\":false},\"language\":\"en\",\"editorMode\":\"normal\"}", new UTF8Encoding(false));
            File.WriteAllText(mcp, "{\"mcpServers\":{}}", new UTF8Encoding(false));
            Arguments = new string[] { "--safe-mode", "--setting-sources", "", "--settings", settings,
                "--strict-mcp-config", "--mcp-config", mcp, "--tools", "", "--session-id", Guid.NewGuid().ToString() };
            EnvironmentOverrides = EnvironmentForChild(ProfileDirectory, SessionDirectory, ExportPath);
        }

        internal static string DefaultRoot()
        {
            // Portable data stays beside this executable. AppData is not writable
            // by the live probe in the current host; do not broaden permissions.
            return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "data", "feasibility-v1");
        }

        private static void RejectLinks(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("A Murmur data path contains a link; refusing to use it.");
        }

        public void CleanupKnownFiles()
        {
            // Called after disposing the owned terminal/login child. Preserve auth;
            // delete only exact files owned by this session, never prune whole trees.
            try
            {
                RejectLinks(SessionDirectory);
                foreach (string name in new string[] { "handoff.txt", "settings.json", "mcp.json" })
                {
                    string path = Path.Combine(SessionDirectory, name); RejectLinks(path);
                    if (File.Exists(path)) File.Delete(path);
                }
                if (Directory.Exists(Workspace)) Directory.Delete(Workspace, false);
                if (Directory.Exists(SessionDirectory)) Directory.Delete(SessionDirectory, false);
            }
            catch (IOException) { Console.Error.WriteLine("Session temp files remain at " + SessionDirectory); }
            catch (UnauthorizedAccessException) { Console.Error.WriteLine("Session temp cleanup was denied at " + SessionDirectory); }
        }

        private static Dictionary<string, string> EnvironmentForChild(string profile, string session, string export)
        {
            Dictionary<string, string> overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            {
                string name = (string)item.Key;
                foreach (string prefix in new string[] { "CLAUDE", "ANTHROPIC", "CODEX", "MURMUR" })
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) overrides[name] = null;
            }
            overrides["CLAUDE_CONFIG_DIR"] = profile;
            overrides["CLAUDE_CODE_TMPDIR"] = session;
            overrides["TEMP"] = session; overrides["TMP"] = session;
            overrides["DISABLE_AUTOUPDATER"] = "1";
            overrides["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            overrides["MURMUR_SESSION_DIRECTORY"] = session;
            overrides["MURMUR_HANDOFF_PATH"] = export;
            overrides["VISUAL"] = OwnedTerminal.QuoteArgument(Assembly.GetExecutingAssembly().Location) + " --export-prompt";
            overrides["EDITOR"] = overrides["VISUAL"];
            overrides["TERM"] = "xterm-256color";
            return overrides;
        }
    }
}

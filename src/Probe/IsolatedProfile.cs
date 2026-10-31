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
        public readonly string ProfileDirectory, SessionDirectory, Workspace, ExportPath;
        public readonly Dictionary<string, string> EnvironmentOverrides;
        public readonly string[] Arguments;

        public IsolatedProfile()
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Murmur", "feasibility-v1");
            string marker = Path.Combine(root, ".murmur-owned-root");
            if (Directory.Exists(root) && !File.Exists(marker)) throw new IOException("Existing data directory has no Murmur ownership marker; refusing to use it.");
            if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Murmur data directory must not be a link.");
            Directory.CreateDirectory(root);
            if (!File.Exists(marker)) File.WriteAllText(marker, "Murmur feasibility profile v1\n", new UTF8Encoding(false));
            ProfileDirectory = Path.Combine(root, "profile");
            SessionDirectory = Path.Combine(root, "sessions", Guid.NewGuid().ToString("N"));
            Workspace = Path.Combine(SessionDirectory, "workspace");
            ExportPath = Path.Combine(SessionDirectory, "handoff.txt");
            Directory.CreateDirectory(ProfileDirectory);
            Directory.CreateDirectory(Workspace);
            if ((File.GetAttributes(ProfileDirectory) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The isolated profile must not be a link.");
            string settings = Path.Combine(SessionDirectory, "settings.json");
            string mcp = Path.Combine(SessionDirectory, "mcp.json");
            File.WriteAllText(settings, "{\"voice\":{\"enabled\":true,\"mode\":\"hold\",\"autoSubmit\":false},\"language\":\"en\",\"editorMode\":\"normal\"}", new UTF8Encoding(false));
            File.WriteAllText(mcp, "{\"mcpServers\":{}}", new UTF8Encoding(false));
            Arguments = new string[] { "--safe-mode", "--setting-sources", "", "--settings", settings,
                "--strict-mcp-config", "--mcp-config", mcp, "--tools", "", "--session-id", Guid.NewGuid().ToString() };
            EnvironmentOverrides = EnvironmentForChild(ProfileDirectory, SessionDirectory, ExportPath);
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

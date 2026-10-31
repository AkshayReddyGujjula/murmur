using System;

namespace Murmur.Probe
{
    public enum ProbeMode { Help, Login, Probe, Export }
    public sealed class ProbeOptions
    {
        public ProbeMode Mode { get; private set; }
        public string ClaudePath { get; private set; }
        public string PromptPath { get; private set; }

        public static ProbeOptions Parse(string[] args)
        {
            ProbeOptions options = new ProbeOptions();
            if (args == null || args.Length == 0 || (args.Length == 1 && args[0] == "--help")) return options;
            if (args.Length == 2 && args[0] == "--export-prompt")
            { options.Mode = ProbeMode.Export; options.PromptPath = args[1]; return options; }
            if (args[0] == "--login") options.Mode = ProbeMode.Login;
            else if (args[0] == "--probe") options.Mode = ProbeMode.Probe;
            else throw new ArgumentException("Choose --help, --login or --probe.");
            if (args.Length == 3 && args[1] == "--claude" && !String.IsNullOrWhiteSpace(args[2])) options.ClaudePath = args[2];
            else if (args.Length != 1) throw new ArgumentException("The only additional option is --claude <absolute-path-to-claude.exe>.");
            return options;
        }
    }
}

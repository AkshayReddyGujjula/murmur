using System;
using Murmur.Probe;

internal static class OptionsTests
{
    public static int Main()
    {
        try
        {
            if (ProbeOptions.Parse(new string[0]).Mode != ProbeMode.Help) throw new Exception("Default mode must be inert.");
            if (ProbeOptions.Parse(new string[] { "--help" }).Mode != ProbeMode.Help) throw new Exception("Help must be inert.");
            if (ProbeOptions.Parse(new string[] { "--probe" }).Mode != ProbeMode.Probe) throw new Exception("Probe must be explicit.");
            if (ProbeOptions.Parse(new string[] { "--login" }).Mode != ProbeMode.Login) throw new Exception("Login must be explicit.");
            if (ProbeOptions.Parse(new string[] { "--probe", "--claude", "C:\\a b\\claude.exe" }).ClaudePath != "C:\\a b\\claude.exe") throw new Exception("Path changed.");
            foreach (string[] args in new string[][] {
                new string[] { "--help", "--probe" }, new string[] { "--login", "--probe" },
                new string[] { "--probe", "--claude" }, new string[] { "--probe", "--resume", "existing" },
                new string[] { "--export-prompt" }, new string[] { "--probe", "--claude", "" } })
            {
                bool rejected = false;
                try { ProbeOptions.Parse(args); } catch (ArgumentException) { rejected = true; }
                if (!rejected) throw new Exception("Ambiguous/unsafe option accepted.");
            }
            Console.WriteLine("PASS: inert default/help, explicit modes and strict argument parsing.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}

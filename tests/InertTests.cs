using System;
using System.IO;
using System.Text;
using Murmur.Probe;

namespace Murmur.Tests
{
    internal static class InertTests
    {
        public static int Main()
        {
            string root = Path.Combine(Path.GetTempPath(), "murmur-inert-" + Guid.NewGuid().ToString("N"));
            int resolvedRoots = 0;
            TextWriter output = Console.Out, errors = Console.Error;
            try
            {
                // These entry-point paths must return before any profile, native
                // transport, mutex, microphone or provider is initialized.
                IsolatedProfile.RootOverride = delegate { resolvedRoots++; return root; };
                using (StringWriter sink = new StringWriter())
                {
                    Console.SetOut(sink); Console.SetError(sink);
                    if (Program.Main(new string[0]) != 0) throw new Exception("Default help failed.");
                    if (Program.Main(new string[] { "--help" }) != 0) throw new Exception("Explicit help failed.");
                    if (Program.Main(new string[] { "--unknown" }) == 0) throw new Exception("Invalid mode accepted.");
                }
                if (resolvedRoots != 0 || Directory.Exists(root)) throw new Exception("An inert command attempted profile initialization.");
                string executableDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (IsolatedProfile.DefaultRoot() != Path.Combine(executableDirectory, "data", "feasibility-v1"))
                    throw new Exception("Default data root escaped the portable executable directory.");
                // Positive control: prove the seam redirects the real constructor,
                // rather than checking a path the production code never uses.
                IsolatedProfile fixture = new IsolatedProfile();
                if (resolvedRoots != 1 || !fixture.ProfileDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Profile test seam did not redirect the constructor.");
                fixture.CleanupKnownFiles();
                Directory.Delete(fixture.ProfileDirectory, false);
                Directory.Delete(Path.GetDirectoryName(fixture.SessionDirectory), false);
                File.Delete(Path.Combine(root, ".murmur-owned-root")); Directory.Delete(root, false);
                if (Program.SafeDisplay("good morning\rjunk") != "good morning\u240djunk") throw new Exception("Bare CR can rewrite diagnostic text.");
                if (Program.SafeDisplay("a\r\nb") != "a\nb") throw new Exception("CRLF display changed incorrectly.");
                if (Program.SafeDisplay("x\u202ey\x1b") != "x\u2423y\u2423") throw new Exception("Diagnostic format controls escaped filtering.");
                Console.SetOut(output); Console.SetError(errors);
                Console.WriteLine("PASS: real entry-point default/help/error paths create no profile or provider.");
                return 0;
            }
            catch (Exception error) { Console.SetError(errors); Console.Error.WriteLine(error.Message); return 1; }
            finally
            {
                IsolatedProfile.RootOverride = null;
                Console.SetOut(output); Console.SetError(errors);
            }
        }
    }
}

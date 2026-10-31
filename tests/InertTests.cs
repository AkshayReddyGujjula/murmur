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
            string appData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string root = Path.Combine(Path.GetTempPath(), "murmur-inert-" + Guid.NewGuid().ToString("N"));
            TextWriter output = Console.Out, errors = Console.Error;
            try
            {
                // These entry-point paths must return before any profile, native
                // transport, mutex, microphone or provider is initialized.
                Environment.SetEnvironmentVariable("LOCALAPPDATA", root);
                using (StringWriter sink = new StringWriter())
                {
                    Console.SetOut(sink); Console.SetError(sink);
                    if (Program.Main(new string[0]) != 0) throw new Exception("Default help failed.");
                    if (Program.Main(new string[] { "--help" }) != 0) throw new Exception("Explicit help failed.");
                    if (Program.Main(new string[] { "--unknown" }) == 0) throw new Exception("Invalid mode accepted.");
                }
                if (Directory.Exists(root)) throw new Exception("An inert command created a data directory.");
                Console.SetOut(output); Console.SetError(errors);
                Console.WriteLine("PASS: real entry-point default/help/error paths create no profile or provider.");
                return 0;
            }
            catch (Exception error) { Console.SetError(errors); Console.Error.WriteLine(error.Message); return 1; }
            finally
            {
                Environment.SetEnvironmentVariable("LOCALAPPDATA", appData);
                Console.SetOut(output); Console.SetError(errors);
            }
        }
    }
}

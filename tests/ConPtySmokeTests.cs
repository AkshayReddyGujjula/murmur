using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Murmur.Transport;

namespace Murmur.Tests
{
    // Explicit native smoke test: only this test's child runs, never Claude/audio.
    internal static class ConPtySmokeTests
    {
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
        [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

        public static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--child")
            {
                uint mode;
                bool input = GetConsoleMode(GetStdHandle(-10), out mode);
                bool output = GetConsoleMode(GetStdHandle(-11), out mode);
                Console.WriteLine("CONPTY stdin=" + input + " stdout=" + output);
                return input && output ? 0 : 1;
            }
            using (ManualResetEvent closed = new ManualResetEvent(false))
            using (OwnedTerminal terminal = new OwnedTerminal())
            {
                StringBuilder text = new StringBuilder();
                object gate = new object();
                terminal.Output += delegate(string value) { lock (gate) text.Append(value); };
                terminal.Closed += delegate { closed.Set(); };
                terminal.Start(Assembly.GetExecutingAssembly().Location, new string[] { "--child" },
                    System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), null);
                if (!closed.WaitOne(6000)) { Console.Error.WriteLine("FAIL: ConPTY child did not finish."); return 1; }
                lock (gate)
                {
                    if (text.ToString().IndexOf("CONPTY stdin=True stdout=True", StringComparison.Ordinal) < 0)
                    { Console.Error.WriteLine("FAIL: child did not receive ConPTY standard handles."); return 1; }
                }
                Console.WriteLine("PASS: redirected launcher cannot replace ConPTY stdin/stdout.");
                return 0;
            }
        }
    }
}

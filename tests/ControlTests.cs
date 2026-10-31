using System;
using Murmur.Probe;

namespace Murmur.Tests
{
    internal static class ControlTests
    {
        private static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
        public static int Main()
        {
            try
            {
                ProbeControl gate = new ProbeControl();
                Check(gate.RepeatInput() == null, "setup must not emit held keys");
                Check(gate.Key(ConsoleKey.Enter) == ProbeAction.SetupInput, "explicit setup confirmation");
                Check(gate.Key(ConsoleKey.E) == ProbeAction.None && !gate.AcceptExport(), "no export before recording");
                Check(gate.Key(ConsoleKey.R) == ProbeAction.StartHold, "first R starts exactly one take");
                Check(gate.Key(ConsoleKey.R) == ProbeAction.None, "repeat R is harmless");
                for (int tick = 0; tick < 7 * 60 * 20; tick++) Check(gate.RepeatInput() == " ", "seven-minute input schedule");
                Check(gate.Key(ConsoleKey.Enter) == ProbeAction.None, "Enter cannot submit speech");
                Check(gate.Key(ConsoleKey.E) == ProbeAction.None, "cannot export partial recording");
                Check(gate.Key(ConsoleKey.S) == ProbeAction.StopHold && gate.RepeatInput() == null, "release on stop");
                Check(gate.Key(ConsoleKey.R) == ProbeAction.None, "cannot overlap takes");
                Check(gate.Key(ConsoleKey.Enter) == ProbeAction.None, "Enter stays blocked after stop");
                Check(gate.Key(ConsoleKey.E) == ProbeAction.Export, "explicit export once");
                Check(gate.Key(ConsoleKey.E) == ProbeAction.None && gate.AcceptExport(), "one export completion");
                Check(!gate.AcceptExport(), "duplicate completion ignored");
                foreach (ConsoleKey key in new ConsoleKey[] { ConsoleKey.Enter, ConsoleKey.R, ConsoleKey.E })
                    Check(gate.Key(key) == ProbeAction.None, "finished take cannot write more input");

                foreach (ConsoleKey cancel in new ConsoleKey[] { ConsoleKey.C, ConsoleKey.Q })
                {
                    gate = new ProbeControl(); gate.Key(ConsoleKey.R); gate.Key(ConsoleKey.S);
                    gate.Key(ConsoleKey.E); gate.Key(cancel);
                    Check(gate.RepeatInput() == null && !gate.AcceptExport(), "cancel invalidates late export");
                }
                Console.WriteLine("PASS: setup, single take, seven-minute simulated hold, stop, no-submit gate, cancel and stale export.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
        }
    }
}

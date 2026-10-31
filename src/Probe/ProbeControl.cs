using System;

namespace Murmur.Probe
{
    public enum ProbePhase { Setup, Holding, Finalizing, Exporting, Finished, Cancelled }
    public enum ProbeAction { None, SetupInput, StartHold, StopHold, Export, Inspect, Cancel, Quit }

    // A single-take control gate. The event loop is the sole caller. In particular,
    // terminal setup confirmations cannot turn into prompt submissions after R.
    public sealed class ProbeControl
    {
        public ProbePhase Phase { get; private set; }
        public ProbeControl() { Phase = ProbePhase.Setup; }

        public ProbeAction Key(ConsoleKey key)
        {
            if (Phase == ProbePhase.Finished || Phase == ProbePhase.Cancelled) return ProbeAction.None;
            if (key == ConsoleKey.Q) { Phase = ProbePhase.Cancelled; return ProbeAction.Quit; }
            if (key == ConsoleKey.C) { Phase = ProbePhase.Cancelled; return ProbeAction.Cancel; }
            if (key == ConsoleKey.V) return ProbeAction.Inspect;
            if (Phase == ProbePhase.Setup)
            {
                if (key == ConsoleKey.Enter || key == ConsoleKey.UpArrow || key == ConsoleKey.DownArrow)
                    return ProbeAction.SetupInput;
                if (key == ConsoleKey.R) { Phase = ProbePhase.Holding; return ProbeAction.StartHold; }
            }
            else if (Phase == ProbePhase.Holding && key == ConsoleKey.S)
            { Phase = ProbePhase.Finalizing; return ProbeAction.StopHold; }
            else if (Phase == ProbePhase.Finalizing && key == ConsoleKey.E)
            { Phase = ProbePhase.Exporting; return ProbeAction.Export; }
            return ProbeAction.None;
        }

        public string RepeatInput() { return Phase == ProbePhase.Holding ? " " : null; }
        public bool AcceptExport()
        {
            if (Phase != ProbePhase.Exporting) return false;
            Phase = ProbePhase.Finished;
            return true;
        }
    }
}

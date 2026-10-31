using System;
using Murmur.Probe;

internal static class InboxTests
{
    private static void Check(bool value, string detail) { if (!value) throw new Exception(detail); }
    public static int Main()
    {
        try
        {
            using (ProbeInbox inbox = new ProbeInbox())
            {
                for (int i = 0; i < 10000; i++) inbox.Post(new ProbeMessage("tick"));
                inbox.Post(new ProbeMessage("handoff")); inbox.Post(new ProbeMessage("timeout"));
                inbox.Post(new ProbeMessage("handoff"));
                Check(inbox.Take().Kind == "handoff", "Handoff lost behind tick backlog.");
                Check(inbox.Take().Kind == "timeout", "Timeout lost behind tick backlog.");
                Check(inbox.Take().Kind == "tick", "Ticks should coalesce to one pending event.");
            }
            using (ProbeInbox inbox = new ProbeInbox())
            {
                for (int i = 0; i < 200; i++) inbox.Post(new ProbeMessage("key") { Key = ConsoleKey.V });
                inbox.Post(new ProbeMessage("handoff"));
                inbox.Post(new ProbeMessage("key") { Key = ConsoleKey.C });
                Check(inbox.Take().Key == ConsoleKey.C, "Cancel must preempt a saturated key queue.");
                Check(inbox.Take().Kind == "overflow", "Saturation must produce an explicit failure.");
                Check(inbox.Take().Kind == "handoff", "Saturation must not discard the handoff signal.");
                for (int i = 0; i < 32; i++) Check(inbox.Take().Key == ConsoleKey.V, "Unexpected retained key order.");
            }
            using (ProbeInbox inbox = new ProbeInbox())
            {
                inbox.Post(new ProbeMessage("tick"));
                foreach (ConsoleKey key in new ConsoleKey[] { ConsoleKey.R, ConsoleKey.S, ConsoleKey.E })
                    inbox.Post(new ProbeMessage("key") { Key = key });
                ProbeControl control = new ProbeControl();
                Check(control.Key(inbox.Take().Key) == ProbeAction.StartHold, "Start reordered.");
                Check(control.Key(inbox.Take().Key) == ProbeAction.StopHold, "Stop reordered.");
                Check(control.Key(inbox.Take().Key) == ProbeAction.Export, "Export reordered.");
                Check(inbox.Take().Kind == "tick" && control.RepeatInput() == null, "A stale tick restarted recording.");
                inbox.Dispose(); inbox.Post(new ProbeMessage("tick"));
                bool rejected = false;
                try { inbox.Take(); } catch (ObjectDisposedException) { rejected = true; }
                Check(rejected, "Disposed inbox must not block forever.");
            }
            Console.WriteLine("PASS: coalesced ticks, reliable control signals, cancellation priority and explicit queue overflow.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 1; }
    }
}

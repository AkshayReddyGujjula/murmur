using System;
using System.Collections.Generic;
using System.Threading;

namespace Murmur.Probe
{
    internal sealed class ProbeMessage
    {
        public ConsoleKey Key;
        public string Kind, Detail;
        public ProbeMessage(string kind) { Kind = kind; }
    }

    // Bounded, event-driven mailbox. Repeated ticks coalesce; critical signals
    // have reserved slots and cancellation cannot be buried behind diagnostic keys.
    internal sealed class ProbeInbox : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<ProbeMessage> keys = new Queue<ProbeMessage>();
        private readonly Dictionary<string, ProbeMessage> signals = new Dictionary<string, ProbeMessage>();
        private readonly Queue<string> signalOrder = new Queue<string>();
        private ProbeMessage cancel;
        private bool tick, disposed;

        public void Post(ProbeMessage message)
        {
            lock (gate)
            {
                if (disposed) return;
                if (message.Kind == "tick") tick = true;
                else if (message.Kind == "key")
                {
                    if (message.Key == ConsoleKey.C || message.Key == ConsoleKey.Q) cancel = message;
                    else if (keys.Count < 32) keys.Enqueue(message);
                    else Signal(new ProbeMessage("overflow") { Detail = "Diagnostic key queue overflowed; take stopped." });
                }
                else if (message.Kind == "handoff" || message.Kind == "closed" || message.Kind == "timeout" ||
                    message.Kind == "watch-error" || message.Kind == "input-ended" || message.Kind == "reply-dropped") Signal(message);
                else throw new ArgumentException("Unknown probe message kind.");
                Monitor.Pulse(gate);
            }
        }

        private void Signal(ProbeMessage message)
        {
            if (!signals.ContainsKey(message.Kind)) signalOrder.Enqueue(message.Kind);
            signals[message.Kind] = message;
        }

        public ProbeMessage Take()
        {
            lock (gate)
            {
                while (true)
                {
                    if (disposed) throw new ObjectDisposedException("ProbeInbox");
                    if (cancel != null) { ProbeMessage value = cancel; cancel = null; return value; }
                    if (signalOrder.Count > 0)
                    {
                        string kind = signalOrder.Dequeue();
                        ProbeMessage value = signals[kind]; signals.Remove(kind); return value;
                    }
                    if (keys.Count > 0) return keys.Dequeue();
                    if (tick) { tick = false; return new ProbeMessage("tick"); }
                    Monitor.Wait(gate);
                }
            }
        }

        public void Dispose() { lock (gate) { disposed = true; Monitor.PulseAll(gate); } }
    }
}

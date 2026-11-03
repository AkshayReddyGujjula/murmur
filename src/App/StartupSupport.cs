using System;
using System.Threading;

namespace Murmur.App
{
    // Command-line boundaries are kept pure so the tests can prove --export-prompt stays an
    // early, inert path and --background is the only quiet-launch switch.
    internal static class StartupArguments
    {
        internal const string ExportPromptSwitch = "--export-prompt";
        internal const string BackgroundSwitch = "--background";

        public static bool IsPromptExport(string[] args)
        {
            return args != null
                && args.Length == 2
                && String.Equals(args[0], ExportPromptSwitch, StringComparison.Ordinal);
        }

        public static bool IsBackground(string[] args)
        {
            return args != null && args.Length == 1 && args[0] == BackgroundSwitch;
        }

        public static bool IsValidLaunch(string[] args)
        {
            return args != null && (args.Length == 0 || IsBackground(args));
        }
    }

    // A second launch signals the running instance through a named event; the running UI waits for
    // it through a registered wait. No idle polling or focus changes are involved.
    internal sealed class InstanceSignal : IDisposable
    {
        internal const string GlobalPrefix = "Global\\Murmur.Reveal.";
        internal const string LocalPrefix = "Local\\Murmur.Reveal.";

        private EventWaitHandle handle;
        private bool disposed;

        private InstanceSignal(EventWaitHandle handle)
        {
            this.handle = handle;
        }

        public static InstanceSignal CreateOwner(string userKey)
        {
            string key = KeyPart(userKey);
            EventWaitHandle created = Create(GlobalPrefix + key);
            if (created == null) created = Create(LocalPrefix + key);
            return created == null ? null : new InstanceSignal(created);
        }

        public static bool TryReveal(string userKey)
        {
            string key = KeyPart(userKey);
            return Signal(GlobalPrefix + key) || Signal(LocalPrefix + key);
        }

        public bool Consume()
        {
            if (disposed || handle == null) return false;
            try
            {
                return handle.WaitOne(0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public RegisteredWaitHandle Register(Action reveal)
        {
            if (reveal == null) throw new ArgumentNullException("reveal");
            if (disposed) throw new ObjectDisposedException("InstanceSignal");
            return ThreadPool.RegisterWaitForSingleObject(handle,
                delegate { if (!disposed) reveal(); }, null, Timeout.Infinite, false);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            EventWaitHandle closing = handle;
            handle = null;
            if (closing == null) return;
            try
            {
                closing.Close();
            }
            catch (Exception)
            {
            }
        }

        private static EventWaitHandle Create(string name)
        {
            try
            {
                return new EventWaitHandle(false, EventResetMode.AutoReset, name);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Signal(string name)
        {
            try
            {
                using (EventWaitHandle existing = EventWaitHandle.OpenExisting(name))
                {
                    existing.Set();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string KeyPart(string value)
        {
            if (String.IsNullOrEmpty(value)) return "unknown";
            return value.Replace('\\', '_').Replace('/', '_');
        }
    }
}

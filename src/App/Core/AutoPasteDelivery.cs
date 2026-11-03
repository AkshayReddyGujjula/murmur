namespace Murmur.App.Core
{
    public enum PasteProgress { WaitingForKeys, Sent, Skipped }

    // A completion may arrive while Ctrl is still held from the stop shortcut.
    // Retry only that condition, for at most one second, and never paste twice.
    public sealed class AutoPasteDelivery
    {
        private bool finished;

        public PasteProgress Attempt(long elapsedMilliseconds, AutoPaste.Injector send, out string error)
        {
            error = null;
            if (finished) return PasteProgress.Skipped;
            if (elapsedMilliseconds >= 1000)
            {
                finished = true;
                error = "Auto-paste skipped: timed out waiting for shortcut keys.";
                return PasteProgress.Skipped;
            }
            if (send(out error))
            {
                finished = true;
                return PasteProgress.Sent;
            }
            if (error == AutoPaste.KeysHeldError)
                return PasteProgress.WaitingForKeys;
            finished = true;
            return PasteProgress.Skipped;
        }
    }
}

// Offline behavioral tests for Next.Core.DictationSession.
// No UI, provider processes, mic, clipboard or system settings.
// Compiled with csc.exe only alongside next/Core/DictationSession.cs.

using System;
using System.Collections.Generic;
using Murmur.Next.Core;

namespace Murmur.Next.Tests
{
    internal static class CoreTests
    {
        private static int passed;
        private static int failed;

        public static int Main()
        {
            Run("HappyPathUnicodePreserved", HappyPathUnicodePreserved);
            Run("QueuedStopDuringStarting", QueuedStopDuringStarting);
            Run("NoOverlapDuringFinalizingCopying", NoOverlapDuringFinalizingCopying);
            Run("CancelInvalidatesLateTranscript", CancelInvalidatesLateTranscript);
            Run("LateStartedAfterCancelIgnored", LateStartedAfterCancelIgnored);
            Run("StaleCallbacksIgnored", StaleCallbacksIgnored);
            Run("RepeatSessionsMonotonic", RepeatSessionsMonotonic);
            Run("ClipboardFailureRetry", ClipboardFailureRetry);
            Run("EmptyTranscriptNeverCopies", EmptyTranscriptNeverCopies);
            Run("StartFailureCleanupTargetsOwnedId", StartFailureCleanupTargetsOwnedId);
            Run("CancelDuringCopyingCannotCopy", CancelDuringCopyingCannotCopy);
            Run("NoTimerAutoTransition", NoTimerAutoTransition);
            Run("DismissErrorWaitsForCleanup", DismissErrorWaitsForCleanup);
            Run("RecordingFailureIsRecoverable", RecordingFailureIsRecoverable);
            Run("QueuedCopyInvalidatedByCancel", QueuedCopyInvalidatedByCancel);

            Console.WriteLine("PASS=" + passed + " FAIL=" + failed);
            return failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("ok " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine("FAIL " + name + ": " + ex.Message);
            }
        }

        private static void DismissErrorWaitsForCleanup()
        {
            DictationSession session = new DictationSession();
            int id = Only(session.OnHotkeyToggle()).SessionId;
            Only(session.OnProviderStartFailed(id, "failed"));
            session.OnDismissError();
            Check(session.State == SessionState.Cancelling, "must wait for owned cleanup");
            Check(Len(session.OnHotkeyToggle()) == 0, "must not overlap failed child");
            session.OnCleanupCompleted(id + 1);
            Check(session.CleanupPending, "wrong cleanup ack ignored");
            session.OnCleanupCompleted(id);
            Check(session.State == SessionState.Idle, "matching cleanup permits retry");
            Check(Only(session.OnHotkeyToggle()).SessionId > id, "new identity");
        }

        private static void RecordingFailureIsRecoverable()
        {
            DictationSession session = new DictationSession();
            int id = Only(session.OnHotkeyToggle()).SessionId;
            session.OnProviderStarted(id);
            Check(Only(session.OnProviderFailed(id, "microphone disconnected")).Kind == EffectKind.Cleanup, "must clean up failed capture");
            Check(session.State == SessionState.Error, "must report capture failure");
            session.OnCleanupCompleted(id);
            session.OnDismissError();
            Check(session.State == SessionState.Idle, "must be able to retry");
        }

        private static void QueuedCopyInvalidatedByCancel()
        {
            DictationSession session = new DictationSession();
            int id = Only(session.OnHotkeyToggle()).SessionId;
            session.OnProviderStarted(id);
            session.OnHotkeyToggle();
            Effect copy = Only(session.OnTranscriptionReady(id, "retained words", null));
            Check(session.CanExecuteCopy(copy), "current copy allowed");
            session.OnCancel();
            Check(!session.CanExecuteCopy(copy), "cancel revokes queued clipboard intent");
            session.OnCleanupCompleted(id);
            Check(!session.CanExecuteCopy(copy), "old copy still invalid after cleanup");
        }

        private static void Check(bool cond, string msg)
        {
            if (!cond)
            {
                throw new Exception(msg);
            }
        }

        private static Effect Only(Effect[] fx)
        {
            Check(fx != null && fx.Length == 1, "expected exactly 1 effect, got " + Len(fx));
            return fx[0];
        }

        private static int Len(Effect[] fx)
        {
            return fx == null ? -1 : fx.Length;
        }

        private static void HappyPathUnicodePreserved()
        {
            DictationSession s = new DictationSession();
            Effect e0 = Only(s.OnHotkeyToggle());
            Check(e0.Kind == EffectKind.StartProvider && e0.SessionId == 1, "start sid=1");
            Check(s.State == SessionState.Starting, "state Starting");

            Check(Len(s.OnProviderStarted(1)) == 0, "started silent");
            Check(s.State == SessionState.Recording, "state Recording");

            Effect stop = Only(s.OnHotkeyToggle());
            Check(stop.Kind == EffectKind.StopForTranscription && stop.SessionId == 1, "stop sid=1");
            Check(s.State == SessionState.Finalizing, "state Finalizing");

            string text = "h\u00E9llo \U0001F30D\r\nline2\t\u2713  ";
            Effect copy = Only(s.OnTranscriptionReady(1, text, null));
            Check(copy.Kind == EffectKind.CopyText && copy.SessionId == 1, "copy sid=1");
            Check(copy.Text == text, "unicode/multiline exact, got [" + copy.Text + "]");
            Check(s.State == SessionState.Copying, "state Copying");

            Check(Len(s.OnCopyCompleted(1, true, null)) == 0, "copy ok silent");
            Check(s.State == SessionState.Idle, "back to Idle");
            Check(s.CurrentSessionId == 0, "sid cleared");
        }

        private static void QueuedStopDuringStarting()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle()); // sid=1 Starting
            Check(Len(s.OnHotkeyToggle()) == 0, "second hotkey queues silently");
            Check(s.StopQueued, "stop queued flag");
            Check(s.State == SessionState.Starting, "still Starting");

            Effect stop = Only(s.OnProviderStarted(1));
            Check(stop.Kind == EffectKind.StopForTranscription && stop.SessionId == 1, "queued stop fires");
            Check(s.State == SessionState.Finalizing, "state Finalizing");
        }

        private static void NoOverlapDuringFinalizingCopying()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle());
            s.OnProviderStarted(1);
            Only(s.OnHotkeyToggle()); // -> Finalizing
            Check(Len(s.OnHotkeyToggle()) == 0, "hotkey in Finalizing ignored");
            Check(s.State == SessionState.Finalizing, "still Finalizing");
            Check(s.CurrentSessionId == 1, "no new session in Finalizing");

            Only(s.OnTranscriptionReady(1, "abc", null)); // -> Copying
            Check(Len(s.OnHotkeyToggle()) == 0, "hotkey in Copying ignored");
            Check(s.State == SessionState.Copying, "still Copying");
            Check(s.CurrentSessionId == 1, "no new session in Copying");
        }

        private static void CancelInvalidatesLateTranscript()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle());
            s.OnProviderStarted(1);
            Only(s.OnHotkeyToggle()); // Finalizing
            Effect cleanup = Only(s.OnCancel());
            Check(cleanup.Kind == EffectKind.Cleanup && cleanup.SessionId == 1, "cancel cleanup sid=1");
            Check(s.State == SessionState.Cancelling, "state Cancelling");

            // Late transcript for the cancelled session must not copy.
            Check(Len(s.OnTranscriptionReady(1, "late text", null)) == 0, "late transcript ignored");
            Check(Len(s.OnCopyCompleted(1, true, null)) == 0, "late copy ignored");
            Check(s.State == SessionState.Cancelling, "still Cancelling");
            Check(s.PendingText == null, "no pending text after cancel");

            Check(Len(s.OnCleanupCompleted(1)) == 0, "cleanup ack silent");
            Check(s.State == SessionState.Idle, "Idle after cleanup");
        }

        private static void LateStartedAfterCancelIgnored()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle()); // Starting sid=1
            Effect cleanup = Only(s.OnCancel());
            Check(cleanup.Kind == EffectKind.Cleanup && cleanup.SessionId == 1, "cleanup sid=1");
            Check(Len(s.OnProviderStarted(1)) == 0, "late Started ignored");
            Check(s.State == SessionState.Cancelling, "still Cancelling");
            Check(Len(s.OnHotkeyToggle()) == 0, "no new session while Cancelling");
            s.OnCleanupCompleted(1);
            Check(s.State == SessionState.Idle, "Idle after ack");
        }

        private static void StaleCallbacksIgnored()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle()); // sid=1
            Check(Len(s.OnProviderStarted(999)) == 0, "stale Started ignored");
            Check(s.State == SessionState.Starting, "still Starting");
            Check(Len(s.OnTranscriptionReady(999, "x", null)) == 0, "stale transcript ignored");
            Check(Len(s.OnCopyCompleted(999, true, null)) == 0, "stale copy ignored");
            Check(Len(s.OnCleanupCompleted(999)) == 0, "stale cleanup ignored");
            Check(s.CurrentSessionId == 1, "owned sid untouched");
        }

        private static void RepeatSessionsMonotonic()
        {
            DictationSession s = new DictationSession();
            // Session 1 full cycle.
            Only(s.OnHotkeyToggle());
            s.OnProviderStarted(1);
            Only(s.OnHotkeyToggle());
            Only(s.OnTranscriptionReady(1, "one", null));
            s.OnCopyCompleted(1, true, null);
            Check(s.State == SessionState.Idle, "idle after session 1");

            // Session 2 gets a fresh monotonic id.
            Effect start2 = Only(s.OnHotkeyToggle());
            Check(start2.SessionId == 2, "second session sid=2, got " + start2.SessionId);

            // Stale completion from session 1 cannot affect session 2.
            Check(Len(s.OnTranscriptionReady(1, "stale", null)) == 0, "stale sid=1 ignored");
            Check(Len(s.OnCopyCompleted(1, true, null)) == 0, "stale copy ignored");
            Check(s.State == SessionState.Starting, "session 2 still Starting");
        }

        private static void ClipboardFailureRetry()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle());
            s.OnProviderStarted(1);
            Only(s.OnHotkeyToggle());
            Effect copy = Only(s.OnTranscriptionReady(1, "keep me", null));
            Check(copy.SessionId == 1 && copy.Text == "keep me", "copy sid/text");

            Check(Len(s.OnCopyCompleted(1, false, "denied")) == 0, "failure silent");
            Check(s.State == SessionState.Error, "state Error");
            Check(s.PendingText == "keep me", "pending retained");
            Check(s.CanRetryCopy, "retry allowed");
            Check(s.LastError == "denied", "error recorded");

            // Stale copy completion for another id cannot resolve the error.
            Check(Len(s.OnCopyCompleted(999, true, null)) == 0, "stale copy ignored in Error");
            Check(s.State == SessionState.Error, "still Error");

            Effect retry = Only(s.OnRetryCopy());
            Check(retry.Kind == EffectKind.CopyText, "retry emits copy");
            Check(retry.SessionId == 1, "retry reuses owned sid=1");
            Check(retry.Text == "keep me", "retry preserves text");
            Check(s.State == SessionState.Copying, "state Copying");

            s.OnCopyCompleted(1, true, null);
            Check(s.State == SessionState.Idle, "Idle after retry success");
            Check(s.PendingText == null, "pending cleared");
        }

        private static void EmptyTranscriptNeverCopies()
        {
            string[] empties = new string[] { null, "", "   ", "\r\n\t " };
            for (int i = 0; i < empties.Length; i++)
            {
                DictationSession s = new DictationSession();
                Only(s.OnHotkeyToggle());
                s.OnProviderStarted(1);
                Only(s.OnHotkeyToggle());
                Effect[] fx = s.OnTranscriptionReady(1, empties[i], null);
                Check(Len(fx) == 0, "empty case " + i + " produced no effects");
                Check(s.State == SessionState.Idle, "empty case " + i + " back to Idle");
                Check(s.PendingText == null, "empty case " + i + " no pending");
            }
        }

        private static void StartFailureCleanupTargetsOwnedId()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle()); // sid=1
            Effect cleanup = Only(s.OnProviderStartFailed(1, "no mic"));
            Check(cleanup.Kind == EffectKind.Cleanup && cleanup.SessionId == 1, "cleanup sid=1");
            Check(s.State == SessionState.Error, "state Error");
            Check(!s.CanRetryCopy, "start failure not retryable");
            Check(s.PendingText == null, "no pending on start failure");
            Check(Len(s.OnRetryCopy()) == 0, "no retry without pending");

            // Failure cleanup targeted owned sid; stale sid cannot dismiss.
            Check(Len(s.OnCleanupCompleted(999)) == 0, "stale cleanup ignored");
            Check(s.State == SessionState.Error, "still Error");
            Check(Len(s.OnDismissError()) == 0, "dismiss silent");
            Check(s.State == SessionState.Cancelling, "dismiss waits for owned cleanup");
            s.OnCleanupCompleted(1);
            Check(s.State == SessionState.Idle, "Idle after dismiss");
        }

        private static void CancelDuringCopyingCannotCopy()
        {
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle());
            s.OnProviderStarted(1);
            Only(s.OnHotkeyToggle());
            Only(s.OnTranscriptionReady(1, "payload", null)); // Copying
            Effect cleanup = Only(s.OnCancel());
            Check(cleanup.Kind == EffectKind.Cleanup && cleanup.SessionId == 1, "cleanup sid=1");
            Check(Len(s.OnCopyCompleted(1, true, null)) == 0, "copy after cancel ignored");
            Check(s.State == SessionState.Cancelling, "still Cancelling");
            Check(Len(s.OnRetryCopy()) == 0, "no retry while Cancelling");
            s.OnCleanupCompleted(1);
            Check(s.State == SessionState.Idle, "Idle after cleanup");
        }

        private static void NoTimerAutoTransition()
        {
            // Core has no clock: Recording persists without host events
            // (host owns any >= 7 minute duration policy).
            DictationSession s = new DictationSession();
            Only(s.OnHotkeyToggle());
            s.OnProviderStarted(1);
            Check(s.State == SessionState.Recording, "Recording holds with no timer");
            Check(s.CurrentSessionId == 1, "sid stable with no timer");
        }
    }
}

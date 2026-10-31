// Pure single-threaded dictation session controller.
// Dependency-free C# 5 / .NET Framework 4.8. No native calls, I/O,
// clocks, sleeps, clipboard access or provider processes. The host owns
// all side effects; this reducer only returns typed effects to execute.
//
// States: Idle -> Starting -> Recording -> Finalizing -> Copying -> Idle,
// plus Cancelling (explicit cancel, waits for host cleanup ack) and Error
// (start failure or recoverable clipboard failure).
//
// Duration/timeout policy: the core has no timers and never auto-times
// out. Any maximum session length (including >= 7 minutes support) is
// entirely the host's concern via OnCancel / UI.
//
// Threading: not thread-safe by design. The host must call from one thread.

using System;
using System.Collections.Generic;

namespace Murmur.Next.Core
{
    public enum SessionState
    {
        Idle,
        Starting,
        Recording,
        Finalizing,
        Copying,
        Cancelling,
        Error
    }

    public enum EffectKind
    {
        StartProvider,
        StopForTranscription,
        CopyText,
        Cleanup
    }

    public sealed class Effect
    {
        private readonly EffectKind kind;
        private readonly int sessionId;
        private readonly string text;
        private readonly string detail;

        public EffectKind Kind
        {
            get { return kind; }
        }

        public int SessionId
        {
            get { return sessionId; }
        }

        public string Text
        {
            get { return text; }
        }

        public string Detail
        {
            get { return detail; }
        }

        public Effect(EffectKind kind, int sessionId, string text, string detail)
        {
            this.kind = kind;
            this.sessionId = sessionId;
            this.text = text;
            this.detail = detail;
        }

        public static Effect Start(int sessionId)
        {
            return new Effect(EffectKind.StartProvider, sessionId, null, null);
        }

        public static Effect Stop(int sessionId)
        {
            return new Effect(EffectKind.StopForTranscription, sessionId, null, null);
        }

        public static Effect Copy(int sessionId, string text)
        {
            return new Effect(EffectKind.CopyText, sessionId, text, null);
        }

        public static Effect Cleanup(int sessionId, string detail)
        {
            return new Effect(EffectKind.Cleanup, sessionId, null, detail);
        }

        public override string ToString()
        {
            return "Effect(" + kind.ToString() + ", sid=" + sessionId + ")";
        }
    }

    public sealed class DictationSession
    {
        private static readonly Effect[] NoEffects = new Effect[0];

        private SessionState state;
        private int nextSessionId;
        private int currentSessionId;
        private bool stopQueued;
        private string pendingText;
        private string lastError;
        private bool canRetryCopy;
        private bool cleanupPending;

        public DictationSession()
        {
            state = SessionState.Idle;
            nextSessionId = 0;
            currentSessionId = 0;
            stopQueued = false;
            pendingText = null;
            lastError = null;
            canRetryCopy = false;
        }

        public SessionState State
        {
            get { return state; }
        }

        public int CurrentSessionId
        {
            get { return currentSessionId; }
        }

        public bool StopQueued
        {
            get { return stopQueued; }
        }

        public string PendingText
        {
            get { return pendingText; }
        }

        public string LastError
        {
            get { return lastError; }
        }

        public bool CanRetryCopy
        {
            get { return state == SessionState.Error && canRetryCopy; }
        }

        public bool CleanupPending { get { return cleanupPending; } }

        // The host checks this immediately before a queued clipboard operation,
        // on the same UI thread as cancellation. An emitted intent is not a lease.
        public bool CanExecuteCopy(Effect effect)
        {
            return effect != null && effect.Kind == EffectKind.CopyText &&
                state == SessionState.Copying && effect.SessionId == currentSessionId &&
                effect.Text == pendingText;
        }

        // Global hotkey toggles start/stop. Never submits dictated prompts.
        public Effect[] OnHotkeyToggle()
        {
            if (state == SessionState.Idle && !cleanupPending)
            {
                nextSessionId++;
                currentSessionId = nextSessionId;
                stopQueued = false;
                pendingText = null;
                lastError = null;
                canRetryCopy = false;
                state = SessionState.Starting;
                return new Effect[] { Effect.Start(currentSessionId) };
            }
            if (state == SessionState.Starting)
            {
                // Provider not up yet: queue Stop for when Started arrives.
                stopQueued = true;
                return NoEffects;
            }
            if (state == SessionState.Recording)
            {
                state = SessionState.Finalizing;
                return new Effect[] { Effect.Stop(currentSessionId) };
            }
            // Finalizing / Copying: ignore so sessions never overlap.
            // Cancelling / Error: host must finish cleanup/retry first.
            return NoEffects;
        }

        public Effect[] OnProviderStarted(int sessionId)
        {
            if (sessionId != currentSessionId)
            {
                return NoEffects; // stale callback
            }
            if (state != SessionState.Starting)
            {
                return NoEffects;
            }
            if (stopQueued)
            {
                stopQueued = false;
                state = SessionState.Finalizing;
                return new Effect[] { Effect.Stop(sessionId) };
            }
            state = SessionState.Recording;
            return NoEffects;
        }

        public Effect[] OnProviderStartFailed(int sessionId, string error)
        {
            if (sessionId != currentSessionId)
            {
                return NoEffects;
            }
            if (state != SessionState.Starting)
            {
                return NoEffects;
            }
            EnterError(sessionId, error, null, false);
            cleanupPending = true;
            // Cleanup targets the owned session only.
            return new Effect[] { Effect.Cleanup(sessionId, "start-failed") };
        }

        public Effect[] OnProviderFailed(int sessionId, string error)
        {
            if (sessionId != currentSessionId ||
                (state != SessionState.Starting && state != SessionState.Recording && state != SessionState.Finalizing))
                return NoEffects;
            EnterError(sessionId, error, null, false);
            cleanupPending = true;
            return new Effect[] { Effect.Cleanup(sessionId, "provider-failed") };
        }

        // Host stopped the provider and transcribed. Exact transcript bytes
        // are preserved; empty/whitespace never produces a copy effect.
        public Effect[] OnTranscriptionReady(int sessionId, string transcript, string error)
        {
            if (sessionId != currentSessionId)
            {
                return NoEffects; // stale or post-cancel session id
            }
            if (state != SessionState.Finalizing)
            {
                return NoEffects; // cancelled or wrong phase: cannot copy
            }
            if (error != null && error.Length > 0)
            {
                EnterError(sessionId, error, null, false);
                cleanupPending = true;
                return new Effect[] { Effect.Cleanup(sessionId, "transcribe-failed") };
            }
            if (transcript == null || transcript.Trim().Length == 0)
            {
                ResetToIdle();
                return NoEffects;
            }
            pendingText = transcript;
            canRetryCopy = false;
            state = SessionState.Copying;
            return new Effect[] { Effect.Copy(sessionId, transcript) };
        }

        public Effect[] OnCopyCompleted(int sessionId, bool success, string error)
        {
            if (sessionId != currentSessionId)
            {
                return NoEffects; // stale callback cannot copy
            }
            if (state != SessionState.Copying)
            {
                return NoEffects; // cancelled or already resolved
            }
            if (success)
            {
                ResetToIdle();
                return NoEffects;
            }
            string message = error != null ? error : "clipboard failed";
            EnterError(sessionId, message, pendingText, true);
            return NoEffects;
        }

        public Effect[] OnCancel()
        {
            if (state == SessionState.Idle)
            {
                return NoEffects;
            }
            if (state == SessionState.Error)
            {
                if (cleanupPending) state = SessionState.Cancelling;
                else ResetToIdle();
                return NoEffects;
            }
            if (state == SessionState.Cancelling)
            {
                return NoEffects;
            }
            int sid = currentSessionId;
            state = SessionState.Cancelling;
            cleanupPending = true;
            stopQueued = false;
            // Late transcript/clipboard intents for sid are now invalid:
            // handlers only act in Finalizing/Copying, never Cancelling.
            return new Effect[] { Effect.Cleanup(sid, "cancel") };
        }

        public Effect[] OnCleanupCompleted(int sessionId)
        {
            if (sessionId != currentSessionId || !cleanupPending)
            {
                return NoEffects;
            }
            cleanupPending = false;
            if (state == SessionState.Cancelling) ResetToIdle();
            return NoEffects;
        }

        public Effect[] OnRetryCopy()
        {
            if (state != SessionState.Error || !canRetryCopy)
            {
                return NoEffects;
            }
            if (pendingText == null || pendingText.Trim().Length == 0)
            {
                return NoEffects;
            }
            state = SessionState.Copying;
            lastError = null;
            // Reuses the owned session id; no new session is allocated.
            return new Effect[] { Effect.Copy(currentSessionId, pendingText) };
        }

        public Effect[] OnDismissError()
        {
            if (state != SessionState.Error)
            {
                return NoEffects;
            }
            if (cleanupPending) state = SessionState.Cancelling;
            else ResetToIdle();
            return NoEffects;
        }

        private void EnterError(int sessionId, string error, string keepText, bool retry)
        {
            state = SessionState.Error;
            currentSessionId = sessionId;
            lastError = error != null ? error : "failed";
            pendingText = keepText;
            canRetryCopy = retry;
            stopQueued = false;
        }

        private void ResetToIdle()
        {
            state = SessionState.Idle;
            currentSessionId = 0;
            stopQueued = false;
            pendingText = null;
            lastError = null;
            canRetryCopy = false;
            cleanupPending = false;
        }
    }
}

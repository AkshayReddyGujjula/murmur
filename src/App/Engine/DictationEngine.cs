using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Murmur.Probe;
using Murmur.Transport;

namespace Murmur.App.Engine
{
    public enum EngineState { Stopped, Starting, Ready, Recording, Finalizing, Faulted }

    public sealed class DictationEngine : IDisposable
    {
        private const int ClearComposerTimeoutMs = 3000;
        private const int ExportFreshnessSlackSeconds = 2;
        private const int MaxExportBytes = 1048576;
        private const int ExportReadAttempts = 5;
        private const int ExportReadRetryMs = 20;
        internal const string SignInMessage = "Sign in needed \u2014 open the tray menu and choose Sign in";
        internal const string SetupMessage = "Claude needs one-time setup \u2014 open the tray menu and choose Account, then Run Claude first-time setup";
        private const string SuspendedMessage = "Finish the Claude sign-in window first";

        private readonly EngineOptions _options;
        private readonly Func<ITerminalHost> _terminals;
        private readonly Func<IEngineSession> _sessions;
        private readonly object _inboxGate = new object();
        private readonly Queue<EngineMessage> _inbox = new Queue<EngineMessage>();
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stopped = new ManualResetEventSlim(false);
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private volatile int _state;
        private int _generation;
        private int _disposed;
        private bool _closedInbox;
        private int _nextTakeId;
        private bool _suspended;

        private ITerminalHost _terminal;
        private IEngineSession _session;
        private TerminalScreen _screen;

        private Timer _pollTimer;
        private Timer _holdTimer;

        private TakePhase _phase;
        private ExportPurpose _exportPurpose;
        private TakeRequest _pipelineTake;
        private TakeRequest _pendingTake;
        private bool _beginAfterDiscard;
        private string _lastComposer = "";
        private long _startDeadline;
        private long _listenDeadline;
        private long _finalDeadline;
        private long _exportDeadline;
        private long _cancelDeadline;
        private long _clearDeadline;
        private long _stableSince;
        private long _emptySince = -1;
        private long _missingComposerSince = -1;
        private DateTime _exportRequestUtc;

        public DictationEngine(EngineOptions options, Func<ITerminalHost> terminals, Func<IEngineSession> sessions)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (terminals == null) throw new ArgumentNullException("terminals");
            if (sessions == null) throw new ArgumentNullException("sessions");

            _options = options;
            _terminals = terminals;
            _sessions = sessions;
            _state = (int)EngineState.Stopped;

            _thread = new Thread(Run);
            _thread.Name = "Murmur engine";
            _thread.IsBackground = true;
            _thread.Start();
        }

        public EngineState State
        {
            get { return (EngineState)_state; }
        }

        public event Action<EngineState> StateChanged;
        public event Action<string> TranscriptReady;
        public event Action<string> Failed;
        public event Action<string> Diagnostic;
        public event Action<string> Warning;
        public event Action Discarded;
        public event Action<int> TakeRecording;
        public event Action<int, string> TakeTranscript;
        public event Action<int, string> TakeFailed;
        public event Action<int> TakeDiscarded;
        public event Action Suspended;

        public void Warm()
        {
            Post(new WarmMessage());
        }

        public int BeginTake()
        {
            int id = Interlocked.Increment(ref _nextTakeId);
            Post(new BeginTakeMessage { Id = id });
            return id;
        }

        public void EndTake()
        {
            Post(new EndTakeMessage { Current = true });
        }

        public void EndTake(int id)
        {
            Post(new EndTakeMessage { Id = id });
        }

        public void CancelTake()
        {
            Post(new CancelTakeMessage { Current = true });
        }

        public void CancelTake(int id)
        {
            Post(new CancelTakeMessage { Id = id });
        }

        public void Suspend()
        {
            Post(new SuspendMessage());
        }

        public void Resume()
        {
            Post(new ResumeMessage());
        }

        public void Unload()
        {
            Post(new UnloadMessage());
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            Post(new DisposeMessage());
            if (Thread.CurrentThread == _thread)
            {
                return;
            }

            _stopped.Wait(15000);
            StopAllTimers();
        }

        private void Run()
        {
            try
            {
                while (true)
                {
                    EngineMessage message = Take();
                    if (message == null)
                    {
                        break;
                    }
                    if (message is DisposeMessage)
                    {
                        StopAllTimers();
                        _generation++;
                        _phase = TakePhase.None;
                        _pendingTake = null;
                        _pipelineTake = null;
                        _beginAfterDiscard = false;
                        CleanupResources();
                        SetState(EngineState.Stopped);
                        break;
                    }

                    try
                    {
                        Handle(message);
                    }
                    catch (Exception error)
                    {
                        HandleEngineError(error);
                    }
                }
            }
            finally
            {
                StopAllTimers();
                CleanupResources();
                lock (_inboxGate)
                {
                    _inbox.Clear();
                    _closedInbox = true;
                    Monitor.PulseAll(_inboxGate);
                }
                _stopped.Set();
            }
        }

        private void Handle(EngineMessage message)
        {
            if (message is WarmMessage) { HandleWarm(); return; }
            if (message is BeginTakeMessage) { HandleBeginTake((BeginTakeMessage)message); return; }
            if (message is EndTakeMessage) { HandleEndTake((EndTakeMessage)message); return; }
            if (message is CancelTakeMessage) { HandleCancelTake((CancelTakeMessage)message); return; }
            if (message is SuspendMessage) { HandleSuspend(); return; }
            if (message is ResumeMessage) { HandleResume(); return; }
            if (message is UnloadMessage) { HandleUnload(); return; }

            OutputMessage output = message as OutputMessage;
            if (output != null)
            {
                HandleOutput(output);
                return;
            }

            ClosedMessage closed = message as ClosedMessage;
            if (closed != null)
            {
                HandleClosed(closed);
                return;
            }

            PollMessage poll = message as PollMessage;
            if (poll != null)
            {
                HandlePoll(poll);
                return;
            }

            HoldMessage hold = message as HoldMessage;
            if (hold != null)
            {
                HandleHold(hold);
            }
        }

        private void HandleWarm()
        {
            if (_disposed != 0 || _suspended)
            {
                return;
            }
            EngineState state = (EngineState)_state;
            if (state != EngineState.Stopped && state != EngineState.Faulted)
            {
                return;
            }
            StartChild();
        }

        private void HandleBeginTake(BeginTakeMessage message)
        {
            if (_disposed != 0)
            {
                return;
            }

            TakeRequest request = new TakeRequest();
            request.Id = message.Id;

            if (_suspended)
            {
                FailRequest(request, SuspendedMessage, true);
                return;
            }

            if (_pendingTake != null && !_pendingTake.Ended)
            {
                TakeRequest previousPending = _pendingTake;
                _pendingTake = null;
                EndTakeDiscarded(previousPending);
            }

            if (_pipelineTake != null && !_pipelineTake.Ended)
            {
                EndPipelineTakeDiscarded();
                if (_phase == TakePhase.Listening || _phase == TakePhase.Recording)
                {
                    CancelActiveTake();
                }
                else if (_phase == TakePhase.Discard || _phase == TakePhase.DiscardClearWait)
                {
                    _beginAfterDiscard = false;
                }
            }

            _pendingTake = request;

            EngineState state = (EngineState)_state;
            if (state == EngineState.Stopped || state == EngineState.Faulted)
            {
                StartChild();
                return;
            }
            if (state == EngineState.Starting)
            {
                return;
            }
            if (state == EngineState.Ready && _phase == TakePhase.None)
            {
                StartPendingTake();
            }
        }

        private void HandleEndTake(EndTakeMessage message)
        {
            if (!message.Current)
            {
                TakeRequest pending = _pendingTake;
                if (pending != null && !pending.Ended && pending.Id == message.Id)
                {
                    _pendingTake = null;
                    EndTakeDiscarded(pending);
                    return;
                }
                TakeRequest active = _pipelineTake;
                if (active == null || active.Ended || active.Id != message.Id)
                {
                    return;
                }
                EndActiveTake();
                return;
            }

            TakeRequest current = _pipelineTake;
            if (current != null && !current.Ended)
            {
                EndActiveTake();
                return;
            }
            TakeRequest queued = _pendingTake;
            if (queued != null && !queued.Ended)
            {
                _pendingTake = null;
                EndTakeDiscarded(queued);
            }
        }

        private void EndActiveTake()
        {
            if (_phase == TakePhase.Discard || _phase == TakePhase.DiscardClearWait)
            {
                _beginAfterDiscard = false;
                EndPipelineTakeDiscarded();
                return;
            }
            if (_phase == TakePhase.Listening || _phase == TakePhase.Recording)
            {
                StartFinalWait();
                return;
            }
            if (_phase == TakePhase.FinalWait || _phase == TakePhase.CancelQuiet ||
                _phase == TakePhase.ExportWait || _phase == TakePhase.ClearWait)
            {
                return;
            }
            if ((EngineState)_state == EngineState.Starting)
            {
                EndPipelineTakeDiscarded();
                return;
            }

            StopHold();
            _phase = TakePhase.None;
            _generation++;
            SetState(EngineState.Ready);
            EndPipelineTakeDiscarded();
            StartPendingTake();
        }

        private void HandleCancelTake(CancelTakeMessage message)
        {
            if (!message.Current)
            {
                TakeRequest pending = _pendingTake;
                if (pending != null && !pending.Ended && pending.Id == message.Id)
                {
                    _pendingTake = null;
                    EndTakeDiscarded(pending);
                    return;
                }
                TakeRequest active = _pipelineTake;
                if (active == null || active.Ended || active.Id != message.Id)
                {
                    return;
                }
                CancelActiveTakeRequest();
                return;
            }

            TakeRequest current = _pipelineTake;
            if (current != null && !current.Ended)
            {
                CancelActiveTakeRequest();
                return;
            }
            TakeRequest queued = _pendingTake;
            if (queued != null && !queued.Ended)
            {
                _pendingTake = null;
                EndTakeDiscarded(queued);
            }
        }

        private void CancelActiveTakeRequest()
        {
            if ((EngineState)_state == EngineState.Starting)
            {
                _beginAfterDiscard = false;
                EndPipelineTakeDiscarded();
                return;
            }
            if (_phase == TakePhase.Listening || _phase == TakePhase.Recording ||
                _phase == TakePhase.FinalWait)
            {
                CancelActiveTake();
                return;
            }
            if (_phase == TakePhase.CancelQuiet)
            {
                return;
            }
            if (_phase == TakePhase.ExportWait)
            {
                StopHold();
                _exportPurpose = ExportPurpose.Discard;
                _beginAfterDiscard = false;
                return;
            }
            if (_phase == TakePhase.ClearWait)
            {
                StopHold();
                _phase = TakePhase.None;
                _generation++;
                SetState(EngineState.Ready);
                EndPipelineTakeDiscarded();
                StartPendingTake();
                return;
            }
            if (_phase == TakePhase.Discard || _phase == TakePhase.DiscardClearWait)
            {
                _beginAfterDiscard = false;
                EndPipelineTakeDiscarded();
                return;
            }
            if ((EngineState)_state == EngineState.Recording || (EngineState)_state == EngineState.Finalizing)
            {
                StopHold();
                _phase = TakePhase.None;
                _generation++;
                SetState(EngineState.Ready);
                EndPipelineTakeDiscarded();
                StartPendingTake();
            }
        }

        private void HandleSuspend()
        {
            StopAllTimers();
            StopHold();
            _phase = TakePhase.None;
            _beginAfterDiscard = false;
            _generation++;

            TakeRequest pending = _pendingTake;
            _pendingTake = null;
            if (pending != null)
            {
                EndTakeDiscarded(pending);
            }
            TakeRequest pipeline = _pipelineTake;
            _pipelineTake = null;
            if (pipeline != null)
            {
                EndTakeDiscarded(pipeline);
            }

            CleanupResources();
            SetState(EngineState.Stopped);
            _suspended = true;
            RaiseSuspended();
        }

        private void HandleResume()
        {
            _suspended = false;
        }

        private void HandleUnload()
        {
            StopAllTimers();
            StopHold();
            _generation++;
            _phase = TakePhase.None;
            _beginAfterDiscard = false;

            TakeRequest pending = _pendingTake;
            _pendingTake = null;
            if (pending != null)
            {
                EndTakeDiscarded(pending);
            }
            TakeRequest pipeline = _pipelineTake;
            _pipelineTake = null;
            if (pipeline != null)
            {
                EndTakeDiscarded(pipeline);
            }

            CleanupResources();
            SetState(EngineState.Stopped);
        }

        private void HandleOutput(OutputMessage message)
        {
            if (message.Host != _terminal || _screen == null)
            {
                return;
            }
            _screen.Feed(message.Text, delegate(string reply)
            {
                ITerminalHost terminal = _terminal;
                if (terminal != null)
                {
                    try
                    {
                        terminal.TryWrite(reply);
                    }
                    catch (Exception)
                    {
                    }
                }
            });
        }

        private void HandleClosed(ClosedMessage message)
        {
            if (message.Host != _terminal)
            {
                return;
            }

            bool active = _phase != TakePhase.None || _pendingTake != null || _beginAfterDiscard ||
                HasLivePipelineTake();

            StopAllTimers();
            _generation++;
            _phase = TakePhase.None;
            _beginAfterDiscard = false;
            if (active)
            {
                RaiseFailed("Claude closed unexpectedly");
            }
            EndAllLiveTakesFailure("Claude closed unexpectedly");
            _pipelineTake = null;
            _pendingTake = null;
            CleanupResources();
            SetState(EngineState.Faulted);
        }

        private void HandlePoll(PollMessage message)
        {
            if (message.Generation != _generation)
            {
                return;
            }

            if ((EngineState)_state == EngineState.Starting)
            {
                PollStartup(message.Generation);
                return;
            }

            if (_phase == TakePhase.Listening || _phase == TakePhase.Recording)
            {
                if (CheckHoldGuard())
                {
                    return;
                }
            }

            if (_phase == TakePhase.Recording)
            {
                ArmPoll(message.Generation);
                return;
            }
            if (_phase == TakePhase.Listening) { PollListen(message.Generation); return; }
            if (_phase == TakePhase.FinalWait) { PollFinalize(message.Generation); return; }
            if (_phase == TakePhase.CancelQuiet) { PollCancelQuiet(message.Generation); return; }
            if (_phase == TakePhase.ExportWait) { PollExport(message.Generation); return; }
            if (_phase == TakePhase.ClearWait) { PollClear(message.Generation); return; }
            if (_phase == TakePhase.DiscardClearWait) { PollDiscardClear(message.Generation); return; }
            if (_phase == TakePhase.Discard) { PollExport(message.Generation); }
        }

        private void HandleHold(HoldMessage message)
        {
            if (message.Generation != _generation)
            {
                return;
            }
            if (_phase != TakePhase.Listening && _phase != TakePhase.Recording)
            {
                return;
            }
            if (CheckHoldGuard())
            {
                return;
            }
            WriteSpace();
            ArmHold(message.Generation);
        }

        private bool CheckHoldGuard()
        {
            if (_phase != TakePhase.Listening && _phase != TakePhase.Recording)
            {
                return false;
            }
            ScreenReading reading = ReadScreen();
            if (reading.Kind == ScreenKind.NeedsSignIn)
            {
                FailHoldGuard(SignInMessage);
                return true;
            }
            if (reading.Kind == ScreenKind.NeedsSetup)
            {
                FailHoldGuard(SetupMessage);
                return true;
            }
            return false;
        }

        private void FailHoldGuard(string message)
        {
            StopAllTimers();
            StopHold();
            _phase = TakePhase.None;
            _generation++;
            if (HasLivePipelineTake() || HasLivePendingTake())
            {
                RaiseFailed(message);
            }
            EndAllLiveTakesFailure(message);
            _pipelineTake = null;
            _pendingTake = null;
            CleanupResources();
            SetState(EngineState.Faulted);
        }

        private void StartChild()
        {
            StopAllTimers();
            CleanupResources();
            _phase = TakePhase.None;
            _generation++;
            int generation = _generation;
            _beginAfterDiscard = false;
            SetState(EngineState.Starting);

            try
            {
                IEngineSession session = _sessions();
                _session = session;
                ITerminalHost terminal = _terminals();
                _terminal = terminal;
                _screen = new TerminalScreen(OwnedTerminal.TerminalColumns, OwnedTerminal.TerminalRows);
                terminal.Output += delegate(string chunk)
                {
                    Post(new OutputMessage { Host = terminal, Text = chunk });
                };
                terminal.Closed += delegate(string reason)
                {
                    Post(new ClosedMessage { Host = terminal, Reason = reason });
                };
                terminal.Start(_options.ClaudePath, session.Arguments, session.Workspace, session.Environment);
                _startDeadline = Now() + _options.ReadyTimeoutMs;
                ArmPoll(generation);
            }
            catch (Exception)
            {
                CleanupResources();
                _phase = TakePhase.None;
                _generation++;
                RaiseFailed("Claude did not start");
                EndAllLiveTakesFailure("Claude did not start");
                _pipelineTake = null;
                _pendingTake = null;
                SetState(EngineState.Faulted);
            }
        }

        private void PollStartup(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (reading.Kind == ScreenKind.Ready)
            {
                LogClaudeVersion();
                SetState(EngineState.Ready);
                StartPendingTake();
                return;
            }
            if (reading.Kind == ScreenKind.NeedsSignIn)
            {
                FailStartup(SignInMessage);
                return;
            }
            if (reading.Kind == ScreenKind.NeedsSetup)
            {
                FailStartup(SetupMessage);
                return;
            }
            if (Now() >= _startDeadline)
            {
                FailStartup("Claude did not start");
                return;
            }
            ArmPoll(generation);
        }

        private void FailStartup(string message)
        {
            StopAllTimers();
            _phase = TakePhase.None;
            _generation++;
            RaiseFailed(message);
            EndAllLiveTakesFailure(message);
            _pipelineTake = null;
            _pendingTake = null;
            CleanupResources();
            SetState(EngineState.Faulted);
        }

        private void BeginFromReady()
        {
            if ((EngineState)_state != EngineState.Ready)
            {
                return;
            }
            ScreenReading reading = ReadScreen();
            if (reading.Kind != ScreenKind.Ready || !reading.HasComposer)
            {
                FailPrompt(PromptFailureMessage(reading.Kind));
                return;
            }
            if (reading.ComposerText.Length > 0)
            {
                StartDiscard(true);
                return;
            }
            BeginHold();
        }

        private static string PromptFailureMessage(ScreenKind kind)
        {
            if (kind == ScreenKind.NeedsSignIn)
            {
                return SignInMessage;
            }
            if (kind == ScreenKind.NeedsSetup)
            {
                return SetupMessage;
            }
            return "Claude did not start";
        }

        private void FailPrompt(string message)
        {
            StopAllTimers();
            StopHold();
            _phase = TakePhase.None;
            _beginAfterDiscard = false;
            _generation++;
            RaiseFailed(message);
            EndAllLiveTakesFailure(message);
            _pipelineTake = null;
            _pendingTake = null;
            CleanupResources();
            SetState(EngineState.Faulted);
        }

        private void BeginHold()
        {
            if ((EngineState)_state != EngineState.Ready)
            {
                return;
            }

            ScreenReading reading = ReadScreen();
            if (reading.Kind != ScreenKind.Ready || !reading.HasComposer)
            {
                FailPrompt(PromptFailureMessage(reading.Kind));
                return;
            }
            if (reading.ComposerText.Length > 0)
            {
                StartDiscard(true);
                return;
            }

            StopHold();
            _phase = TakePhase.Listening;
            _generation++;
            int generation = _generation;
            _listenDeadline = Now() + _options.ListenTimeoutMs;
            _lastComposer = "";
            _stableSince = 0;
            _emptySince = -1;

            if (!WriteSpace())
            {
                FailTake("Microphone did not start", true);
                return;
            }
            ArmHold(generation);
            ArmPoll(generation);
        }

        private void PollListen(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (reading.Kind == ScreenKind.NeedsSignIn)
            {
                FailHoldGuard(SignInMessage);
                return;
            }
            if (reading.Kind == ScreenKind.NeedsSetup)
            {
                FailHoldGuard(SetupMessage);
                return;
            }
            if (reading.Kind == ScreenKind.Listening)
            {
                _phase = TakePhase.Recording;
                SetState(EngineState.Recording);
                RaiseTakeRecording();
                ArmPoll(generation);
                return;
            }
            if (Now() >= _listenDeadline)
            {
                FailTake("Microphone did not start", true);
                return;
            }
            ArmPoll(generation);
        }

        private void StartFinalWait()
        {
            StopHold();
            _phase = TakePhase.FinalWait;
            _generation++;
            int generation = _generation;
            SetState(EngineState.Finalizing);
            _finalDeadline = Now() + _options.FinalizeTimeoutMs;

            ScreenReading reading = ReadScreen();
            _lastComposer = ComposerSignature(reading);
            _stableSince = Now();
            _emptySince = -1;
            _missingComposerSince = -1;
            if (reading.HasComposer && reading.Kind != ScreenKind.Listening && reading.ComposerText.Length == 0)
            {
                _emptySince = Now();
            }
            ArmPoll(generation);
        }

        private void PollFinalize(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (reading.Kind == ScreenKind.NeedsSignIn) { FailPrompt(SignInMessage); return; }
            if (reading.Kind == ScreenKind.NeedsSetup) { FailPrompt(SetupMessage); return; }
            if (CheckMissingComposer(reading, false)) return;
            if (reading.Kind == ScreenKind.Listening)
            {
                _emptySince = -1;
                if (Now() >= _finalDeadline)
                {
                    FailTake("Dictation did not finish", true);
                    return;
                }
                ArmPoll(generation);
                return;
            }

            string text = reading.ComposerText;
            if (text.Length > 0)
            {
                _emptySince = -1;
                string signature = ComposerSignature(reading);
                if (!string.Equals(signature, _lastComposer, StringComparison.Ordinal))
                {
                    _lastComposer = signature;
                    _stableSince = Now();
                }
                else if (Now() - _stableSince >= _options.StableMs)
                {
                    StartTranscriptExport();
                    return;
                }
            }
            else if (reading.HasComposer)
            {
                _lastComposer = "";
                if (_emptySince < 0)
                {
                    _emptySince = Now();
                }
                else if (Now() - _emptySince >= _options.NoSpeechMs)
                {
                    FailTake("No speech detected", false);
                    return;
                }
            }
            else
            {
                // A missing composer during a redraw says nothing about silence.
                _emptySince = -1;
            }

            if (Now() >= _finalDeadline)
            {
                FailTake("Dictation did not finish", true);
                return;
            }
            ArmPoll(generation);
        }

        private void StartTranscriptExport()
        {
            StopHold();
            _phase = TakePhase.ExportWait;
            _exportPurpose = ExportPurpose.Transcript;
            _generation++;
            int generation = _generation;
            bool deleteFailed;
            if (!StartExportRequest(out deleteFailed))
            {
                if (deleteFailed)
                {
                    EndPipelineTakeFailure("Could not export the transcript");
                    RecycleChild("Recycled Claude after the transcript export could not start");
                    return;
                }
                FailTake("Could not export the transcript", false);
                return;
            }
            ArmPoll(generation);
        }

        private bool StartExportRequest(out bool deleteFailed)
        {
            deleteFailed = false;
            IEngineSession session = _session;
            ITerminalHost terminal = _terminal;
            if (session == null || terminal == null || session.ExportPath == null)
            {
                return false;
            }

            string path = session.ExportPath;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                deleteFailed = true;
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                deleteFailed = true;
                return false;
            }

            _exportRequestUtc = DateTime.UtcNow;
            _exportDeadline = Now() + _options.ExportTimeoutMs;
            // Let a throwing host reach the message-handler guard in Run().
            return terminal.TryWrite("\x07");
        }

        private void PollExport(int generation)
        {
            IEngineSession session = _session;
            if (session == null || session.ExportPath == null)
            {
                if (_exportPurpose == ExportPurpose.Transcript)
                {
                    FailTake("Could not export the transcript", false);
                }
                else
                {
                    HandleDiscardExportFailure("Recycled Claude after the discard export could not start", false);
                }
                return;
            }

            string path = session.ExportPath;
            if (File.Exists(path) && IsFreshHandoff(path))
            {
                string text;
                if (TryReadExport(path, out text))
                {
                    bool deleted = DeleteExport(path);
                    if (_exportPurpose == ExportPurpose.Transcript)
                    {
                        string trimmed = text.Trim();
                        if (trimmed.Length > 0)
                        {
                            EndPipelineTakeTranscript(trimmed);
                        }
                        else
                        {
                            EndPipelineTakeFailure("No speech detected");
                        }
                        if (!deleted)
                        {
                            RecycleChild("Recycled Claude after the handoff file could not be deleted");
                            return;
                        }
                        _phase = TakePhase.ClearWait;
                        _clearDeadline = Now() + ClearComposerTimeoutMs;
                        _generation++;
                        ArmPoll(_generation);
                        return;
                    }
                    if (!deleted)
                    {
                        RaiseDiagnostic("Handoff cleanup incomplete: " + path);
                        RaiseWarning("Could not remove temporary dictated text; see Murmur logs.");
                        HandleDiscardExportFailure("Recycled Claude after the discard handoff could not be deleted", false);
                        return;
                    }
                    CompleteDiscard();
                    return;
                }
            }

            if (Now() >= _exportDeadline)
            {
                if (_exportPurpose == ExportPurpose.Transcript)
                {
                    RecycleChild("Recycled Claude after the transcript export timed out");
                    EndPipelineTakeFailure("Could not export the transcript");
                }
                else
                {
                    HandleDiscardExportFailure("Recycled Claude after the discard export timed out", false);
                }
                return;
            }
            ArmPoll(generation);
        }

        private void HandleDiscardExportFailure(string diagnostic, bool raiseFailure)
        {
            if (_beginAfterDiscard)
            {
                RestartPendingTakeOnFreshChild(diagnostic);
                return;
            }
            if (raiseFailure)
            {
                RaiseFailed("Could not export the transcript");
            }
            RecycleChild(diagnostic);
            EndPipelineTakeDiscarded();
        }

        private void RaiseWarning(string message)
        {
            Action<string> handler = Warning;
            if (handler != null) try { handler(message); } catch (Exception) { }
        }

        private bool CheckMissingComposer(ScreenReading reading, bool cancelling)
        {
            if (reading.HasComposer || reading.Kind == ScreenKind.Listening)
            {
                _missingComposerSince = -1;
                return false;
            }
            if (_missingComposerSince < 0) _missingComposerSince = Now();
            if (Now() - _missingComposerSince < _options.ScreenUnavailableTimeoutMs) return false;
            RaiseDiagnostic("Composer not found on screen: " + DescribeScreen());
            if (cancelling) EndPipelineTakeDiscarded();
            else EndPipelineTakeFailure("Claude input became unavailable; try again");
            RecycleChild("Recycled Claude after the composer disappeared");
            return true;
        }

        private void RestartPendingTakeOnFreshChild(string diagnostic)
        {
            _beginAfterDiscard = false;
            TakeRequest take = _pipelineTake;
            _pipelineTake = null;
            if (take != null && !take.Ended)
            {
                if (_pendingTake == null)
                {
                    _pendingTake = take;
                }
                else
                {
                    EndTakeDiscarded(take);
                }
            }
            _phase = TakePhase.None;
            _generation++;
            RecycleChild(diagnostic);
        }

        private void PollClear(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (reading.Kind == ScreenKind.NeedsSignIn)
            {
                FailPrompt(SignInMessage);
                return;
            }
            if (reading.Kind == ScreenKind.NeedsSetup)
            {
                FailPrompt(SetupMessage);
                return;
            }
            if (reading.Kind == ScreenKind.Ready && reading.HasComposer &&
                reading.ComposerText.Length == 0)
            {
                CompleteToReady();
                return;
            }
            if (Now() >= _clearDeadline)
            {
                RecycleChild();
                return;
            }
            ArmPoll(generation);
        }

        private void PollDiscardClear(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (reading.Kind == ScreenKind.NeedsSignIn)
            {
                FailPrompt(SignInMessage);
                return;
            }
            if (reading.Kind == ScreenKind.NeedsSetup)
            {
                FailPrompt(SetupMessage);
                return;
            }
            if (reading.Kind == ScreenKind.Ready && reading.HasComposer &&
                reading.ComposerText.Length == 0)
            {
                bool beginAfter = _beginAfterDiscard;
                _beginAfterDiscard = false;
                _phase = TakePhase.None;
                _generation++;
                SetState(EngineState.Ready);
                if (beginAfter && HasLivePipelineTake())
                {
                    BeginHold();
                }
                else
                {
                    StartPendingTake();
                }
                return;
            }
            if (Now() >= _clearDeadline)
            {
                RestartPendingTakeOnFreshChild(
                    "Recycled Claude after the composer discard did not clear");
                return;
            }
            ArmPoll(generation);
        }

        private void CompleteToReady()
        {
            _phase = TakePhase.None;
            _generation++;
            SetState(EngineState.Ready);
            StartPendingTake();
        }

        private void RecycleChild()
        {
            RecycleChild("Recycled Claude after the export left the composer non-empty");
        }

        private void RecycleChild(string diagnostic)
        {
            RaiseDiagnostic(diagnostic);
            _phase = TakePhase.None;
            _generation++;
            StartChild();
        }

        private void StartDiscard(bool beginAfter)
        {
            StopHold();
            _phase = TakePhase.Discard;
            _beginAfterDiscard = beginAfter;
            _exportPurpose = ExportPurpose.Discard;
            _generation++;
            int generation = _generation;
            SetState(EngineState.Ready);

            bool deleteFailed;
            if (!StartExportRequest(out deleteFailed))
            {
                if (beginAfter)
                {
                    RestartPendingTakeOnFreshChild(
                        "Recycled Claude after the composer discard could not start");
                }
                else
                {
                    HandleDiscardExportFailure(
                        "Recycled Claude after the discard export could not start", deleteFailed);
                }
                return;
            }
            ArmPoll(generation);
        }

        private void CompleteDiscard()
        {
            bool beginAfter = _beginAfterDiscard;
            if (beginAfter && HasLivePipelineTake())
            {
                _phase = TakePhase.DiscardClearWait;
                _clearDeadline = Now() + ClearComposerTimeoutMs;
                _generation++;
                SetState(EngineState.Ready);
                ArmPoll(_generation);
                return;
            }
            _beginAfterDiscard = false;
            FinishDiscard();
        }

        private void FinishDiscard()
        {
            _beginAfterDiscard = false;
            EndPipelineTakeDiscarded();
            _phase = TakePhase.None;
            _generation++;
            SetState(EngineState.Ready);
            StartPendingTake();
        }

        private void CancelActiveTake()
        {
            StopHold();
            _phase = TakePhase.CancelQuiet;
            _generation++;
            int generation = _generation;
            SetState(EngineState.Finalizing);

            ScreenReading reading = ReadScreen();
            _lastComposer = ComposerSignature(reading);
            _stableSince = Now();
            _emptySince = -1;
            _missingComposerSince = -1;
            if (reading.HasComposer && reading.Kind != ScreenKind.Listening && reading.ComposerText.Length == 0)
            {
                _emptySince = Now();
            }
            long settle = _options.FinalizeTimeoutMs;
            if (settle < _options.NoSpeechMs)
            {
                settle = _options.NoSpeechMs;
            }
            _cancelDeadline = Now() + settle;
            ArmPoll(generation);
        }

        private void PollCancelQuiet(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (CheckMissingComposer(reading, true)) return;
            bool listening = reading.Kind == ScreenKind.Listening;
            string text = reading.ComposerText;

            if (reading.HasComposer && !listening && text.Length == 0)
            {
                _lastComposer = "";
                _stableSince = Now();
                if (_emptySince < 0)
                {
                    _emptySince = Now();
                }
                else if (Now() - _emptySince >= _options.NoSpeechMs)
                {
                    FinishCancelQuiet();
                    return;
                }
            }
            else if (reading.HasComposer && !listening)
            {
                _emptySince = -1;
                string signature = ComposerSignature(reading);
                if (!string.Equals(signature, _lastComposer, StringComparison.Ordinal))
                {
                    _lastComposer = signature;
                    _stableSince = Now();
                }
                else if (Now() - _stableSince >= _options.StableMs)
                {
                    StartCancelledExport();
                    return;
                }
            }
            else
            {
                _emptySince = -1;
                if (listening)
                {
                    _lastComposer = "";
                    _stableSince = Now();
                }
            }

            if (Now() >= _cancelDeadline)
            {
                EndPipelineTakeDiscarded();
                RecycleChild("Recycled Claude after a cancelled take did not settle");
                return;
            }
            ArmPoll(generation);
        }

        private void FinishCancelQuiet()
        {
            EndPipelineTakeDiscarded();
            _phase = TakePhase.None;
            _generation++;
            SetState(EngineState.Ready);
            StartPendingTake();
        }

        private void StartCancelledExport()
        {
            StopHold();
            _phase = TakePhase.ExportWait;
            _exportPurpose = ExportPurpose.Discard;
            _generation++;
            int generation = _generation;
            bool deleteFailed;
            if (!StartExportRequest(out deleteFailed))
            {
                HandleDiscardExportFailure(
                    "Recycled Claude after the discard export could not start", deleteFailed);
                return;
            }
            ArmPoll(generation);
        }

        private void FailTake(string message, bool attemptDiscard)
        {
            StopAllTimers();
            StopHold();
            _phase = TakePhase.None;
            _generation++;

            TakeRequest take = _pipelineTake;
            if (take == null)
            {
                RaiseFailed(message);
            }
            else if (!take.Ended)
            {
                take.Ended = true;
                RaiseFailed(message);
                RaiseTakeFailed(take.Id, message);
            }

            SetState(EngineState.Ready);

            if (attemptDiscard)
            {
                ScreenReading reading = ReadScreen();
                if (reading.ComposerText.Length > 0)
                {
                    StartDiscard(false);
                    return;
                }
            }
            StartPendingTake();
        }

        private void StartPendingTake()
        {
            if (_pendingTake == null)
            {
                return;
            }
            if (_suspended)
            {
                TakeRequest suspended = _pendingTake;
                _pendingTake = null;
                FailRequest(suspended, SuspendedMessage, true);
                return;
            }
            if ((EngineState)_state != EngineState.Ready || _phase != TakePhase.None)
            {
                return;
            }

            TakeRequest take = _pendingTake;
            _pendingTake = null;
            if (HasLivePipelineTake())
            {
                EndPipelineTakeDiscarded();
            }
            _pipelineTake = take;
            BeginFromReady();
        }

        private void HandleEngineError(Exception error)
        {
            StopAllTimers();
            bool active = _phase != TakePhase.None || _pendingTake != null || _beginAfterDiscard ||
                HasLivePipelineTake();
            _phase = TakePhase.None;
            _beginAfterDiscard = false;
            _generation++;
            RaiseDiagnostic(error.GetType().Name);
            if (active)
            {
                RaiseFailed("Murmur engine error");
            }
            EndAllLiveTakesFailure("Murmur engine error");
            _pipelineTake = null;
            _pendingTake = null;
            CleanupResources();
            SetState(EngineState.Faulted);
        }

        private ScreenReading ReadScreen()
        {
            TerminalScreen screen = _screen;
            if (screen == null)
            {
                ScreenReading missing = new ScreenReading();
                missing.Kind = ScreenKind.Unknown;
                missing.HasComposer = false;
                missing.ComposerText = "";
                return missing;
            }
            return ScreenParser.Read(screen.Snapshot());
        }

        // Visible composer text plus Claude's scroll labels: when a long prompt
        // is scrolled, a change above the visible window still resets settling.
        private static string ComposerSignature(ScreenReading reading)
        {
            string marker = reading.ComposerScrollMarker ?? "";
            string text = reading.ComposerText ?? "";
            return marker.Length == 0 ? text : marker + "\n" + text;
        }

        // Structure only (row numbers of borders and prompts), never screen text.
        private string DescribeScreen()
        {
            TerminalScreen screen = _screen;
            return screen == null ? "no screen" : ScreenParser.Describe(screen.Snapshot());
        }

        private void LogClaudeVersion()
        {
            TerminalScreen screen = _screen;
            string version = screen == null ? null : ScreenParser.ClaudeVersion(screen.Snapshot());
            if (version != null)
            {
                RaiseDiagnostic("Claude Code " + version + " ready.");
            }
        }

        private bool WriteSpace()
        {
            ITerminalHost terminal = _terminal;
            if (terminal == null)
            {
                return false;
            }
            // A throwing host is an engine failure, not a quiet false result:
            // let it reach the message-handler guard in Run().
            return terminal.TryWrite(" ");
        }

        private bool TryReadExport(string path, out string text)
        {
            text = null;
            for (int attempt = 0; attempt < ExportReadAttempts; attempt++)
            {
                try
                {
                    using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (stream.Length > MaxExportBytes)
                        {
                            return false;
                        }
                        byte[] bytes = new byte[(int)stream.Length];
                        int offset = 0;
                        while (offset < bytes.Length)
                        {
                            int read = stream.Read(bytes, offset, bytes.Length - offset);
                            if (read <= 0)
                            {
                                break;
                            }
                            offset += read;
                        }
                        if (offset < bytes.Length)
                        {
                            Thread.Sleep(ExportReadRetryMs);
                            continue;
                        }
                        if (stream.ReadByte() != -1)
                        {
                            Thread.Sleep(ExportReadRetryMs);
                            continue;
                        }

                        string value;
                        try
                        {
                            value = new UTF8Encoding(false, true).GetString(bytes);
                        }
                        catch (DecoderFallbackException)
                        {
                            return false;
                        }
                        if (value.Length > 0 && value[0] == '\ufeff')
                        {
                            value = value.Substring(1);
                        }
                        text = value;
                        return true;
                    }
                }
                catch (IOException)
                {
                    Thread.Sleep(ExportReadRetryMs);
                }
                catch (UnauthorizedAccessException)
                {
                    return false;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }
            return false;
        }

        private bool IsFreshHandoff(string path)
        {
            if (_exportRequestUtc == DateTime.MinValue)
            {
                return false;
            }
            try
            {
                DateTime written = File.GetLastWriteTimeUtc(path);
                return written >= _exportRequestUtc.AddSeconds(-ExportFreshnessSlackSeconds);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static bool DeleteExport(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private void CleanupResources()
        {
            ITerminalHost terminal = _terminal;
            IEngineSession session = _session;
            _terminal = null;
            _session = null;
            _screen = null;

            if (terminal != null)
            {
                try
                {
                    terminal.Dispose();
                }
                catch (Exception)
                {
                }
            }
            if (session != null)
            {
                try
                {
                    session.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        private void ArmPoll(int generation)
        {
            int delay = PollDelayMilliseconds();
            Timer timer = new Timer(PollCallback, generation, delay, Timeout.Infinite);
            Timer previous = Interlocked.Exchange(ref _pollTimer, timer);
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private int PollDelayMilliseconds()
        {
            int normal = Math.Max(1, _options.PollMs);
            bool completing = _phase == TakePhase.FinalWait
                || (_phase == TakePhase.ExportWait && _exportPurpose == ExportPurpose.Transcript);
            return completing ? Math.Min(normal, Math.Max(1, _options.CompletionPollMs)) : normal;
        }

        private void ArmHold(int generation)
        {
            int delay = _options.HoldRepeatMs < 1 ? 1 : _options.HoldRepeatMs;
            Timer timer = new Timer(HoldCallback, generation, delay, Timeout.Infinite);
            Timer previous = Interlocked.Exchange(ref _holdTimer, timer);
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private void PollCallback(object state)
        {
            // The engine loop filters stale generations before acting.
            Post(new PollMessage { Generation = (int)state });
        }

        private void HoldCallback(object state)
        {
            Post(new HoldMessage { Generation = (int)state });
        }

        private void StopHold()
        {
            Timer hold = Interlocked.Exchange(ref _holdTimer, null);
            if (hold != null)
            {
                hold.Dispose();
            }
        }

        private void StopAllTimers()
        {
            Timer poll = Interlocked.Exchange(ref _pollTimer, null);
            if (poll != null)
            {
                poll.Dispose();
            }
            StopHold();
        }

        private void Post(EngineMessage message)
        {
            lock (_inboxGate)
            {
                if (_closedInbox)
                {
                    return;
                }
                _inbox.Enqueue(message);
                Monitor.PulseAll(_inboxGate);
            }
        }

        private EngineMessage Take()
        {
            lock (_inboxGate)
            {
                while (_inbox.Count == 0 && !_closedInbox)
                {
                    Monitor.Wait(_inboxGate);
                }
                if (_inbox.Count == 0)
                {
                    return null;
                }
                return _inbox.Dequeue();
            }
        }

        private long Now()
        {
            return _clock.ElapsedMilliseconds;
        }

        private bool HasLivePipelineTake()
        {
            return _pipelineTake != null && !_pipelineTake.Ended;
        }

        private bool HasLivePendingTake()
        {
            return _pendingTake != null && !_pendingTake.Ended;
        }

        private void SetState(EngineState next)
        {
            if ((EngineState)_state == next)
            {
                return;
            }
            _state = (int)next;
            Action<EngineState> handler = StateChanged;
            if (handler != null)
            {
                try
                {
                    handler(next);
                }
                catch (Exception)
                {
                }
            }
        }

        private void RaiseTakeRecording()
        {
            TakeRequest take = _pipelineTake;
            if (take == null || take.Ended)
            {
                return;
            }
            Action<int> handler = TakeRecording;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(take.Id);
            }
            catch (Exception)
            {
            }
        }

        private void EndPipelineTakeTranscript(string text)
        {
            TakeRequest take = _pipelineTake;
            if (take == null || take.Ended)
            {
                return;
            }
            take.Ended = true;
            Action<string> legacy = TranscriptReady;
            if (legacy != null)
            {
                try
                {
                    legacy(text);
                }
                catch (Exception)
                {
                }
            }
            RaiseTakeTranscript(take.Id, text);
        }

        private void EndPipelineTakeFailure(string message)
        {
            TakeRequest take = _pipelineTake;
            if (take == null || take.Ended)
            {
                return;
            }
            take.Ended = true;
            RaiseFailed(message);
            RaiseTakeFailed(take.Id, message);
        }

        private void EndPipelineTakeDiscarded()
        {
            TakeRequest take = _pipelineTake;
            if (take == null || take.Ended)
            {
                return;
            }
            EndTakeDiscarded(take);
        }

        private void EndTakeDiscarded(TakeRequest take)
        {
            if (take == null || take.Ended)
            {
                return;
            }
            take.Ended = true;
            Action legacy = Discarded;
            if (legacy != null)
            {
                try
                {
                    legacy();
                }
                catch (Exception)
                {
                }
            }
            RaiseTakeDiscarded(take.Id);
        }

        private void EndAllLiveTakesFailure(string message)
        {
            TakeRequest pipeline = _pipelineTake;
            if (pipeline != null && !pipeline.Ended)
            {
                pipeline.Ended = true;
                RaiseTakeFailed(pipeline.Id, message);
            }
            TakeRequest pending = _pendingTake;
            if (pending != null && !pending.Ended)
            {
                pending.Ended = true;
                RaiseTakeFailed(pending.Id, message);
            }
        }

        private void FailRequest(TakeRequest take, string message, bool raiseLegacy)
        {
            if (take == null || take.Ended)
            {
                return;
            }
            take.Ended = true;
            if (raiseLegacy)
            {
                RaiseFailed(message);
            }
            RaiseTakeFailed(take.Id, message);
        }

        private void RaiseTakeTranscript(int id, string text)
        {
            Action<int, string> handler = TakeTranscript;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(id, text);
            }
            catch (Exception)
            {
            }
        }

        private void RaiseTakeFailed(int id, string message)
        {
            Action<int, string> handler = TakeFailed;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(id, message);
            }
            catch (Exception)
            {
            }
        }

        private void RaiseTakeDiscarded(int id)
        {
            Action<int> handler = TakeDiscarded;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(id);
            }
            catch (Exception)
            {
            }
        }

        private void RaiseFailed(string message)
        {
            Action<string> handler = Failed;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(message);
            }
            catch (Exception)
            {
            }
        }

        private void RaiseDiagnostic(string message)
        {
            Action<string> handler = Diagnostic;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(message);
            }
            catch (Exception)
            {
            }
        }

        private void RaiseSuspended()
        {
            Action handler = Suspended;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler();
            }
            catch (Exception)
            {
            }
        }

        private enum TakePhase
        {
            None,
            Listening,
            Recording,
            FinalWait,
            CancelQuiet,
            ExportWait,
            ClearWait,
            Discard,
            DiscardClearWait
        }

        private enum ExportPurpose
        {
            Transcript,
            Discard
        }

        private sealed class TakeRequest
        {
            public int Id;
            public bool Ended;
        }

        private abstract class EngineMessage
        {
        }

        private sealed class WarmMessage : EngineMessage
        {
        }

        private sealed class BeginTakeMessage : EngineMessage
        {
            public int Id;
        }

        private sealed class EndTakeMessage : EngineMessage
        {
            public int Id;
            public bool Current;
        }

        private sealed class CancelTakeMessage : EngineMessage
        {
            public int Id;
            public bool Current;
        }

        private sealed class SuspendMessage : EngineMessage
        {
        }

        private sealed class ResumeMessage : EngineMessage
        {
        }

        private sealed class UnloadMessage : EngineMessage
        {
        }

        private sealed class DisposeMessage : EngineMessage
        {
        }

        private sealed class OutputMessage : EngineMessage
        {
            public ITerminalHost Host;
            public string Text;
        }

        private sealed class ClosedMessage : EngineMessage
        {
            public ITerminalHost Host;
            public string Reason;
        }

        private sealed class PollMessage : EngineMessage
        {
            public int Generation;
        }

        private sealed class HoldMessage : EngineMessage
        {
            public int Generation;
        }
    }
}

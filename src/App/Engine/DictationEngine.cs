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
        private const int MaxExportBytes = 1048576;
        private const int ExportReadAttempts = 5;
        private const int ExportReadRetryMs = 20;

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
        private bool _exitRequested;

        private ITerminalHost _terminal;
        private IEngineSession _session;
        private TerminalScreen _screen;

        private Timer _pollTimer;
        private Timer _holdTimer;

        private TakePhase _phase;
        private ExportPurpose _exportPurpose;
        private bool _pendingTake;
        private bool _beginAfterDiscard;
        private string _lastComposer = "";
        private long _startDeadline;
        private long _listenDeadline;
        private long _finalDeadline;
        private long _exportDeadline;
        private long _clearDeadline;
        private long _stableSince;
        private long _emptySince = -1;

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

        public void Warm()
        {
            Post(new WarmMessage());
        }

        public void BeginTake()
        {
            Post(new BeginTakeMessage());
        }

        public void EndTake()
        {
            Post(new EndTakeMessage());
        }

        public void CancelTake()
        {
            Post(new CancelTakeMessage());
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
                        _pendingTake = false;
                        _beginAfterDiscard = false;
                        CleanupResources();
                        SetState(EngineState.Stopped);
                        break;
                    }

                    try
                    {
                        Handle(message);
                    }
                    catch (Exception)
                    {
                        HandleEngineError();
                    }

                    if (_exitRequested)
                    {
                        break;
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
            if (message is BeginTakeMessage) { HandleBeginTake(); return; }
            if (message is EndTakeMessage) { HandleEndTake(); return; }
            if (message is CancelTakeMessage) { HandleCancelTake(); return; }
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
            if (_disposed != 0)
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

        private void HandleBeginTake()
        {
            if (_disposed != 0)
            {
                return;
            }

            EngineState state = (EngineState)_state;
            if (state == EngineState.Stopped || state == EngineState.Faulted)
            {
                _pendingTake = true;
                StartChild();
                return;
            }
            if (state == EngineState.Starting)
            {
                _pendingTake = true;
                return;
            }
            if (state == EngineState.Ready)
            {
                if (_terminal == null || _screen == null)
                {
                    _pendingTake = true;
                    StartChild();
                    return;
                }
                if (_phase == TakePhase.None)
                {
                    BeginFromReady();
                }
                else if (_phase == TakePhase.Discard)
                {
                    if (!_beginAfterDiscard)
                    {
                        _pendingTake = true;
                    }
                }
                else
                {
                    _pendingTake = true;
                }
            }
        }

        private void HandleEndTake()
        {
            EngineState state = (EngineState)_state;
            if (state == EngineState.Starting)
            {
                _pendingTake = false;
                return;
            }
            if (_phase == TakePhase.Discard)
            {
                _beginAfterDiscard = false;
                _pendingTake = false;
                return;
            }
            if (_phase == TakePhase.Listening)
            {
                CancelActiveTake();
                return;
            }
            if (_phase == TakePhase.Recording)
            {
                StartFinalWait();
            }
        }

        private void HandleCancelTake()
        {
            EngineState state = (EngineState)_state;
            if (state == EngineState.Starting)
            {
                _pendingTake = false;
                return;
            }
            if (_phase == TakePhase.Listening || _phase == TakePhase.Recording ||
                _phase == TakePhase.FinalWait)
            {
                CancelActiveTake();
                return;
            }
            if (_phase == TakePhase.ExportWait)
            {
                StopHold();
                _exportPurpose = ExportPurpose.Discard;
                _beginAfterDiscard = false;
                SetState(EngineState.Ready);
                return;
            }
            if (_phase == TakePhase.ClearWait)
            {
                StopHold();
                _phase = TakePhase.None;
                _generation++;
                SetState(EngineState.Ready);
                return;
            }
            if (_phase == TakePhase.Discard)
            {
                _beginAfterDiscard = false;
                _pendingTake = false;
                return;
            }
            if (state == EngineState.Recording || state == EngineState.Finalizing)
            {
                StopHold();
                _phase = TakePhase.None;
                _generation++;
                SetState(EngineState.Ready);
            }
        }

        private void HandleUnload()
        {
            StopAllTimers();
            _generation++;
            _phase = TakePhase.None;
            _pendingTake = false;
            _beginAfterDiscard = false;
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

            EngineState state = (EngineState)_state;
            bool active = state == EngineState.Starting || state == EngineState.Recording ||
                          state == EngineState.Finalizing;

            StopAllTimers();
            _generation++;
            _phase = TakePhase.None;
            _pendingTake = false;
            _beginAfterDiscard = false;
            CleanupResources();
            if (active)
            {
                RaiseFailed("Claude closed unexpectedly");
            }
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

            if (_phase == TakePhase.Listening) { PollListen(message.Generation); return; }
            if (_phase == TakePhase.FinalWait) { PollFinalize(message.Generation); return; }
            if (_phase == TakePhase.ExportWait) { PollExport(message.Generation); return; }
            if (_phase == TakePhase.ClearWait) { PollClear(message.Generation); return; }
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
            WriteSpace();
            ArmHold(message.Generation);
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
                _pendingTake = false;
                RaiseFailed("Claude did not start");
                SetState(EngineState.Faulted);
            }
        }

        private void PollStartup(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (reading.Kind == ScreenKind.Ready)
            {
                SetState(EngineState.Ready);
                if (_pendingTake)
                {
                    _pendingTake = false;
                    BeginFromReady();
                }
                return;
            }
            if (reading.Kind == ScreenKind.NeedsSignIn)
            {
                FailStartup("Sign in needed \u2014 open the tray menu and choose Sign in");
                return;
            }
            if (reading.Kind == ScreenKind.NeedsSetup)
            {
                FailStartup("Claude needs one-time setup \u2014 choose Sign in from the tray menu");
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
            _pendingTake = false;
            _generation++;
            CleanupResources();
            RaiseFailed(message);
            SetState(EngineState.Faulted);
        }

        private void BeginFromReady()
        {
            if ((EngineState)_state != EngineState.Ready)
            {
                return;
            }
            ScreenReading reading = ReadScreen();
            if (reading.ComposerText.Length > 0)
            {
                StartDiscard(true);
                return;
            }
            BeginHold();
        }

        private void BeginHold()
        {
            if ((EngineState)_state != EngineState.Ready)
            {
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
            if (reading.Kind == ScreenKind.Listening)
            {
                _phase = TakePhase.Recording;
                SetState(EngineState.Recording);
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
            _lastComposer = reading.ComposerText;
            _stableSince = Now();
            _emptySince = -1;
            if (reading.Kind != ScreenKind.Listening && reading.ComposerText.Length == 0)
            {
                _emptySince = Now();
            }
            ArmPoll(generation);
        }

        private void PollFinalize(int generation)
        {
            ScreenReading reading = ReadScreen();
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
                if (!string.Equals(text, _lastComposer, StringComparison.Ordinal))
                {
                    _lastComposer = text;
                    _stableSince = Now();
                }
                else if (Now() - _stableSince >= _options.StableMs)
                {
                    StartTranscriptExport();
                    return;
                }
            }
            else
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
            if (!StartExportRequest())
            {
                FailTake("Could not export the transcript", false);
                return;
            }
            ArmPoll(generation);
        }

        private bool StartExportRequest()
        {
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
            }
            catch (UnauthorizedAccessException)
            {
            }

            _exportDeadline = Now() + _options.ExportTimeoutMs;
            try
            {
                return terminal.TryWrite("\x07");
            }
            catch (Exception)
            {
                return false;
            }
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
                    CompleteDiscard();
                }
                return;
            }

            string path = session.ExportPath;
            if (File.Exists(path))
            {
                string text;
                if (TryReadExport(path, out text))
                {
                    DeleteExport(path);
                    if (_exportPurpose == ExportPurpose.Transcript)
                    {
                        string trimmed = text.Trim();
                        if (trimmed.Length > 0)
                        {
                            RaiseTranscript(trimmed);
                        }
                        _phase = TakePhase.ClearWait;
                        _clearDeadline = Now() + ClearComposerTimeoutMs;
                        ArmPoll(generation);
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
                    FailTake("Could not export the transcript", false);
                }
                else
                {
                    CompleteDiscard();
                }
                return;
            }
            ArmPoll(generation);
        }

        private void PollClear(int generation)
        {
            ScreenReading reading = ReadScreen();
            if (reading.ComposerText.Length == 0)
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

        private void CompleteToReady()
        {
            _phase = TakePhase.None;
            _generation++;
            SetState(EngineState.Ready);
            if (_pendingTake)
            {
                _pendingTake = false;
                BeginFromReady();
            }
        }

        private void RecycleChild()
        {
            RaiseDiagnostic("Recycled Claude after the export left the composer non-empty");
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

            if (!StartExportRequest())
            {
                CompleteDiscard();
                return;
            }
            ArmPoll(generation);
        }

        private void CompleteDiscard()
        {
            bool beginAfter = _beginAfterDiscard;
            _beginAfterDiscard = false;
            _phase = TakePhase.None;
            _generation++;
            SetState(EngineState.Ready);

            if (beginAfter)
            {
                BeginHold();
                return;
            }
            if (_pendingTake)
            {
                _pendingTake = false;
                BeginFromReady();
            }
        }

        private void CancelActiveTake()
        {
            StopHold();
            _phase = TakePhase.None;
            _generation++;
            SetState(EngineState.Ready);

            ScreenReading reading = ReadScreen();
            if (reading.ComposerText.Length > 0)
            {
                StartDiscard(false);
            }
        }

        private void FailTake(string message, bool attemptDiscard)
        {
            StopAllTimers();
            StopHold();
            _phase = TakePhase.None;
            _generation++;
            RaiseFailed(message);
            SetState(EngineState.Ready);

            if (attemptDiscard)
            {
                ScreenReading reading = ReadScreen();
                if (reading.ComposerText.Length > 0)
                {
                    StartDiscard(false);
                }
            }
        }

        private void HandleEngineError()
        {
            StopAllTimers();
            _phase = TakePhase.None;
            _pendingTake = false;
            _generation++;
            CleanupResources();
            RaiseFailed("Murmur engine error");
            SetState(EngineState.Faulted);
            _exitRequested = true;
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

        private bool WriteSpace()
        {
            ITerminalHost terminal = _terminal;
            if (terminal == null)
            {
                return false;
            }
            try
            {
                return terminal.TryWrite(" ");
            }
            catch (Exception)
            {
                return false;
            }
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

        private static void DeleteExport(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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
            int delay = _options.PollMs < 1 ? 1 : _options.PollMs;
            Timer timer = new Timer(PollCallback, generation, delay, Timeout.Infinite);
            Timer previous = Interlocked.Exchange(ref _pollTimer, timer);
            if (previous != null)
            {
                previous.Dispose();
            }
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

        private void RaiseTranscript(string text)
        {
            Action<string> handler = TranscriptReady;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(text);
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

        private enum TakePhase
        {
            None,
            Listening,
            Recording,
            FinalWait,
            ExportWait,
            ClearWait,
            Discard
        }

        private enum ExportPurpose
        {
            Transcript,
            Discard
        }

        private abstract class EngineMessage
        {
        }

        private sealed class WarmMessage : EngineMessage
        {
        }

        private sealed class BeginTakeMessage : EngineMessage
        {
        }

        private sealed class EndTakeMessage : EngineMessage
        {
        }

        private sealed class CancelTakeMessage : EngineMessage
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

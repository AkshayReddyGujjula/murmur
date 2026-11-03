// OwnedTerminal - one ConPTY child process owned exclusively by this object.
//
// Lifetime and boundedness (actual, not aspirational):
//   * The child is created CREATE_SUSPENDED, assigned to a private kill-on-close job
//     object, verified with IsProcessInJob, and only then resumed. Any containment
//     failure terminates the child and throws instead of running it uncontained.
//   * No native API is resolved at type initialization. ConPTY entry points are
//     resolved with GetProcAddress during Start; a pre-1809 host fails closed with
//     PlatformNotSupportedException before any process exists.
//   * Output bytes are read by a dedicated reader thread and decoded incrementally
//     with a stateful UTF-8 Decoder (no per-chunk string splitting). The reader never
//     invokes user code, so the drain is independent of the Output subscriber.
//   * A dispatcher thread raises Output/Closed. Its queue is bounded (256 chunks of
//     <= 4 KiB); if a subscriber blocks forever the reader stalls once that bound is
//     reached and the child eventually blocks on its own pipe. Memory stays bounded;
//     during shutdown the hard cap is 1024 chunks and excess output is dropped and
//     reported once as "output truncated".
//   * TryWrite never blocks on the child: the write queue is bounded by 128 KiB of
//     characters and returns false when full, not running, or after the pipe broke.
//   * There is no lifecycle mutex. A single short queue lock guards queue state and
//     is never held across a native call, a wait, or a callback. Dispose never waits
//     on the dispatcher, so Dispose from inside a handler cannot deadlock; from a
//     worker thread it returns immediately and the teardown thread finishes.
//
// Native gaps that are real and are not hidden here:
//   * ClosePseudoConsole can block indefinitely on some Windows builds/ConPTY
//     versions (notably pre-1903 and after abnormal client exit). It therefore runs
//     on a background teardown thread. Dispose waits at most ~6 s; if the close is
//     still blocked, Dispose returns with the terminal already killed but with the
//     pseudoconsole/handles still held by that thread. They are released when the
//     close returns, or reclaimed by the OS when this process exits.
//   * The ConPTY server (conhost/OpenConsole) is not part of our job object; only
//     the client process is. Killing the job cannot kill the pty server.
//   * A blocked reader/writer is released with CancelSynchronousIo (Vista+). If that
//     fails, the thread stays blocked, is background, and its pipe handle is not
//     closed underneath it (closing a handle with a pending synchronous I/O is
//     undefined); that handle is then reclaimed at process exit.
//   * AssignProcessToJobObject can fail inside restrictive pre-Windows-8 parent
//     jobs. That is treated as a hard start failure, never as a warning.
//   * The child receives its ConPTY through the process attribute list. General
//     handle inheritance is disabled, so unrelated host handles are not passed on.
//   * Start and Dispose are not intended to race: a Dispose during Start returns
//     immediately and Start tears the child down as soon as it finishes starting.
//   * Thread scheduling means Output/Closed can fire before Start returns. A start
//     that fails after the pseudoconsole/child were created also reports exactly one
//     Closed notice; a start rejected by argument validation reports none.
//   * Completion signals use managed monitor predicates, without dedicated kernel
//     event handles that would need disposal while workers may still be waiting.
//
// Scope: this type only ever acts on the process handle it created and on its own
// job object. It performs no process enumeration, no AttachConsole, no window
// inspection, no SendInput and no name-based kill.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Murmur.Transport
{
    public sealed class OwnedTerminal : IDisposable
    {
        private const int StateNew = 0;
        private const int StateStarting = 1;
        private const int StateRunning = 2;
        private const int StateDisposed = 4;

        private const int MaxOutputChunks = 256;
        private const int MaxOutputChunksWhileStopping = 1024;
        private const int MaxQueuedWriteCharacters = 128 * 1024;
        private const int MaxEnvironmentBlockCharacters = 32767;
        private const int GracefulCloseMilliseconds = 2000;
        private const int OutputDrainMilliseconds = 1500;
        private const int WriteDrainMilliseconds = 2000;
        public const short TerminalColumns = 120;
        public const short TerminalRows = 40;

        private const uint CreateSuspended = 0x00000004;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint DuplicateSameAccess = 0x00000002;
        private const uint JobObjectLimitDieOnUnhandledException = 0x00000400;
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint WaitObject0 = 0;
        private const uint Infinite = 0xFFFFFFFF;
        private const int ErrorBrokenPipe = 109;
        private const int ErrorOperationAborted = 995;
        private const int ErrorInvalidHandle = 6;

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly object _gate = new object();
        private readonly Queue<string> _output = new Queue<string>();
        private readonly Queue<string> _writes = new Queue<string>();
        private readonly Completion _readerDone = new Completion();
        private readonly Completion _writerDone = new Completion();
        private readonly Completion _reaperDone = new Completion();
        private readonly Completion _terminalDone = new Completion();

        private int _state = StateNew;
        private int _teardownStarted;
        private int _closedRaised;
        private volatile bool _contained;
        private int _queuedWriteCharacters;
        private volatile bool _stopping;
        private volatile bool _outputEnded;
        private volatile bool _outputTruncated;
        private volatile bool _writeBroken;
        private volatile bool _raiseClosed;
        private string _reason;

        private IntPtr _job = IntPtr.Zero;
        private IntPtr _process = IntPtr.Zero;
        private IntPtr _pseudoConsole = IntPtr.Zero;
        private IntPtr _inputWrite = IntPtr.Zero;
        private IntPtr _outputRead = IntPtr.Zero;
        private IntPtr _readerNative = IntPtr.Zero;
        private IntPtr _writerNative = IntPtr.Zero;
        private IntPtr _reaperNative = IntPtr.Zero;

        private int _dispatcherThreadId;
        private int _readerThreadId;
        private int _writerThreadId;
        private int _reaperThreadId;
        private volatile bool _dispatcherStarted;
        private volatile bool _readerStarted;
        private volatile bool _writerStarted;
        private volatile bool _reaperStarted;

        private CreatePseudoConsoleDelegate _createPseudoConsole;
        private ClosePseudoConsoleDelegate _closePseudoConsole;

        /// <summary>
        /// Incremental UTF-8 decoded terminal output. Raised on the dispatcher thread;
        /// the receiver must return promptly and must not block the drain.
        /// </summary>
        public event Action<string> Output;

        /// <summary>
        /// Raised exactly once when the started terminal is gone, after queued output
        /// has been delivered. Carries a short status, never transcript or environment
        /// data. Receivers must marshal UI work to their own thread.
        /// </summary>
        public event Action<string> Closed;

        /// <summary>
        /// Starts the single owned child. Throws before any native call for invalid
        /// arguments; throws and leaves the object disposed if native start fails.
        /// </summary>
        public void Start(string executable, string[] arguments, string workingDirectory, IDictionary<string, string> environmentOverrides)
        {
            ValidateStartArguments(executable, arguments, workingDirectory, environmentOverrides);

            if (Interlocked.CompareExchange(ref _state, StateStarting, StateNew) != StateNew)
                throw new InvalidOperationException("The terminal has already been started or disposed.");

            try
            {
                StartCore(executable, arguments, workingDirectory, environmentOverrides);
            }
            catch
            {
                Interlocked.Exchange(ref _state, StateDisposed);
                throw;
            }

            if (Interlocked.CompareExchange(ref _state, StateRunning, StateStarting) != StateStarting)
            {
                // Dispose raced this Start; the object is already owned by shutdown.
                _stopping = true;
                lock (_gate) Monitor.PulseAll(_gate);
                PerformForcedStop();
            }
        }

        /// <summary>
        /// Queues text for the child's stdin. Never waits on the child. Returns false when
        /// the terminal is not running, the queue bound is reached, or the pipe is broken.
        /// </summary>
        public bool TryWrite(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            if (text.Length == 0) return CanWrite();

            lock (_gate)
            {
                if (!CanWrite()) return false;
                if (_queuedWriteCharacters + text.Length > MaxQueuedWriteCharacters) return false;
                _writes.Enqueue(text);
                _queuedWriteCharacters += text.Length;
                Monitor.PulseAll(_gate);
            }
            return true;
        }

        private bool CanWrite()
        {
            // The child can issue a terminal query immediately after ResumeThread,
            // before Start returns. Its writer is already contained and ready then.
            return (_state == StateRunning || (_state == StateStarting && _contained && _writerStarted)) &&
                !_stopping && !_writeBroken;
        }

        /// <summary>
        /// Releases the child, job, pipes and pseudoconsole created by this object.
        /// Inert (no native call, no callback) for an object that never started.
        /// </summary>
        public void Dispose()
        {
            int previous = Interlocked.Exchange(ref _state, StateDisposed);
            if (previous == StateDisposed || previous == StateNew || previous == StateStarting) return;

            _stopping = true;
            lock (_gate) Monitor.PulseAll(_gate);

            // Never block a callback or a worker: teardown completes in the background.
            if (IsWorkerThread())
            {
                ScheduleForcedStop();
                return;
            }

            if (_terminalDone.Wait(GracefulCloseMilliseconds)) return;

            PerformForcedStop();
            if (_terminalDone.Wait(GracefulCloseMilliseconds)) return;
            _terminalDone.Wait(GracefulCloseMilliseconds);
        }

        /// <summary>
        /// Quotes one argument using the MSVCRT/CommandLineToArgvW rules. Pure.
        /// </summary>
        public static string QuoteArgument(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (value.Length == 0) return "\"\"";

            bool needsQuotes = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '"')
                {
                    needsQuotes = true;
                    break;
                }
            }
            if (!needsQuotes) return value;

            StringBuilder builder = new StringBuilder(value.Length + 8);
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                int backslashes = 0;
                while (i < value.Length && value[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == value.Length)
                {
                    builder.Append('\\', backslashes * 2);
                }
                else if (value[i] == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                }
                else
                {
                    builder.Append('\\', backslashes);
                    builder.Append(value[i]);
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Builds a CreateProcess environment block from the inherited environment with
        /// child-only overrides applied (null value removes the key). Pure: the parent
        /// process environment is read, never modified. Result is NUL separated, double
        /// NUL terminated, sorted case-insensitively by name.
        /// </summary>
        public static string EnvironmentBlock(IDictionary<string, string> overrides)
        {
            SortedDictionary<string, string> merged =
                new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            IDictionary inherited = Environment.GetEnvironmentVariables();
            foreach (DictionaryEntry entry in inherited)
            {
                string name = entry.Key as string;
                if (!IsValidVariableName(name)) continue;
                merged[name] = entry.Value == null ? string.Empty : Convert.ToString(entry.Value, CultureInfo.InvariantCulture);
            }

            if (overrides != null)
            {
                foreach (KeyValuePair<string, string> pair in overrides)
                {
                    if (!IsValidVariableName(pair.Key))
                        throw new ArgumentException("Environment override names must be non-empty and free of '=' and NUL.", "overrides");
                    if (pair.Value != null && pair.Value.IndexOf('\0') >= 0)
                        throw new ArgumentException("Environment override values must not contain NUL.", "overrides");

                    if (pair.Value == null) merged.Remove(pair.Key);
                    else merged[pair.Key] = pair.Value;
                }
            }

            StringBuilder builder = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in merged)
            {
                builder.Append(pair.Key);
                builder.Append('=');
                builder.Append(pair.Value);
                builder.Append('\0');
            }
            builder.Append('\0');

            if (builder.Length > MaxEnvironmentBlockCharacters)
                throw new InvalidOperationException("The environment block exceeds the CreateProcess limit of 32767 characters.");

            return builder.ToString();
        }

        /// <summary>Command line for the child: argv[0] plus quoted arguments. Pure.</summary>
        internal static string BuildCommandLine(string executable, string[] arguments)
        {
            if (executable == null) throw new ArgumentNullException("executable");

            StringBuilder builder = new StringBuilder(QuoteArgument(executable));
            if (arguments != null)
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    builder.Append(' ');
                    builder.Append(QuoteArgument(arguments[i]));
                }
            }
            return builder.ToString();
        }

        private static bool IsValidVariableName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.IndexOf('\0') < 0 && name.IndexOf('=') < 0;
        }

        private static void ValidateStartArguments(string executable, string[] arguments, string workingDirectory, IDictionary<string, string> environmentOverrides)
        {
            // Everything here runs before any native handle is created.
            if (executable == null) throw new ArgumentNullException("executable");
            if (executable.Length == 0 || executable.Trim().Length == 0)
                throw new ArgumentException("The executable path must not be empty.", "executable");
            if (executable.IndexOf('\0') >= 0)
                throw new ArgumentException("The executable path must not contain NUL.", "executable");
            if (!IsRootedPath(executable))
                throw new ArgumentException("The executable path must be absolute.", "executable");
            if (!string.Equals(SafeExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The executable must be an existing absolute .exe file.", "executable");
            if (!File.Exists(executable))
                throw new FileNotFoundException("The terminal executable does not exist.", executable);

            if (workingDirectory == null) throw new ArgumentNullException("workingDirectory");
            if (workingDirectory.Length == 0 || workingDirectory.Trim().Length == 0)
                throw new ArgumentException("The working directory must not be empty.", "workingDirectory");
            if (workingDirectory.IndexOf('\0') >= 0)
                throw new ArgumentException("The working directory must not contain NUL.", "workingDirectory");
            if (!IsRootedPath(workingDirectory))
                throw new ArgumentException("The working directory must be absolute.", "workingDirectory");
            if (!Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException("The working directory does not exist.");

            if (arguments != null)
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (arguments[i] == null)
                        throw new ArgumentException("Arguments must not be null.", "arguments");
                    if (arguments[i].IndexOf('\0') >= 0)
                        throw new ArgumentException("Arguments must not contain NUL.", "arguments");
                }
            }

            if (environmentOverrides != null)
            {
                foreach (KeyValuePair<string, string> pair in environmentOverrides)
                {
                    if (!IsValidVariableName(pair.Key))
                        throw new ArgumentException("Environment override names must be non-empty and free of '=' and NUL.", "environmentOverrides");
                    if (pair.Value != null && pair.Value.IndexOf('\0') >= 0)
                        throw new ArgumentException("Environment override values must not contain NUL.", "environmentOverrides");
                }
            }
        }

        private static bool IsRootedPath(string path)
        {
            try
            {
                return Path.IsPathRooted(path) && path.Length >= 3;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static string SafeExtension(string path)
        {
            try
            {
                return Path.GetExtension(path);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private void StartCore(string executable, string[] arguments, string workingDirectory, IDictionary<string, string> environmentOverrides)
        {
            ResolveConPty();

            IntPtr inputRead = IntPtr.Zero;
            IntPtr inputWrite = IntPtr.Zero;
            IntPtr outputRead = IntPtr.Zero;
            IntPtr outputWrite = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr initialThread = IntPtr.Zero;
            GCHandle environmentPin = default(GCHandle);
            bool started = false;
            bool attributesInitialized = false;

            try
            {
                _job = Native.CreateJobObjectW(IntPtr.Zero, null);
                if (_job == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

                JobObjectExtendedLimitInformation limits = new JobObjectExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitDieOnUnhandledException;
                if (!Native.SetInformationJobObject(_job, JobObjectExtendedLimitInformationClass, ref limits,
                        (uint)Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation))))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed.");

                SecurityAttributes inherit = new SecurityAttributes();
                inherit.nLength = Marshal.SizeOf(typeof(SecurityAttributes));
                inherit.bInheritHandle = true;

                if (!Native.CreatePipe(out inputRead, out inputWrite, ref inherit, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed for terminal input.");
                if (!Native.CreatePipe(out outputRead, out outputWrite, ref inherit, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed for terminal output.");
                if (!Native.SetHandleInformation(inputWrite, HandleFlagInherit, 0) ||
                    !Native.SetHandleInformation(outputRead, HandleFlagInherit, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SetHandleInformation failed.");

                _inputWrite = inputWrite;
                _outputRead = outputRead;

                Coord size = new Coord();
                size.X = TerminalColumns;
                size.Y = TerminalRows;
                int hr = _createPseudoConsole(size, inputRead, outputWrite, 0, out _pseudoConsole);
                if (hr < 0) throw new Win32Exception(hr, "CreatePseudoConsole failed.");

                IntPtr attributeListSize = IntPtr.Zero;
                Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
                if (attributeListSize == IntPtr.Zero)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList reported no size.");
                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!Native.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed.");
                attributesInitialized = true;
                if (!Native.UpdateProcThreadAttribute(attributeList, 0, new IntPtr(ProcThreadAttributePseudoConsole),
                        _pseudoConsole, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed.");

                StartupInfoEx startup = new StartupInfoEx();
                startup.StartupInfo.cb = Marshal.SizeOf(typeof(StartupInfoEx));
                // Explicit null standard handles let ConPTY supply its own console
                // handles. Otherwise Windows may duplicate a launcher's redirected
                // stdin/stdout even with bInheritHandles=false, making Claude exit
                // as a non-interactive --print invocation.
                startup.StartupInfo.dwFlags = 0x00000100; // STARTF_USESTDHANDLES
                startup.lpAttributeList = attributeList;

                char[] block = EnvironmentBlock(environmentOverrides).ToCharArray();
                environmentPin = GCHandle.Alloc(block, GCHandleType.Pinned);

                StringBuilder commandLine = new StringBuilder(BuildCommandLine(executable, arguments));
                ProcessInformation processInfo = new ProcessInformation();
                bool created = Native.CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended | ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    environmentPin.AddrOfPinnedObject(), workingDirectory, ref startup, out processInfo);
                if (!created)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for the terminal child.");

                _process = processInfo.hProcess;
                initialThread = processInfo.hThread;

                // The pseudoconsole duplicated the child-side ends; the parent must not keep them.
                Native.CloseHandle(inputRead);
                inputRead = IntPtr.Zero;
                Native.CloseHandle(outputWrite);
                outputWrite = IntPtr.Zero;

                if (!Native.AssignProcessToJobObject(_job, _process))
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "AssignProcessToJobObject failed; refusing to run an uncontained terminal.");

                bool contained;
                if (!Native.IsProcessInJob(_process, _job, out contained) || !contained)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "The terminal child is not contained by its private job object.");
                _contained = true;

                _raiseClosed = true;
                StartWorkers();

                if (Native.ResumeThread(initialThread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");

                started = true;
            }
            finally
            {
                if (environmentPin.IsAllocated) environmentPin.Free();
                if (attributeList != IntPtr.Zero)
                {
                    if (attributesInitialized) Native.DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
                if (initialThread != IntPtr.Zero) Native.CloseHandle(initialThread);
                if (inputRead != IntPtr.Zero) Native.CloseHandle(inputRead);
                if (outputWrite != IntPtr.Zero) Native.CloseHandle(outputWrite);
                if (!started) AbortStart();
            }
        }

        private void ResolveConPty()
        {
            IntPtr kernel32 = Native.GetModuleHandleW("kernel32.dll");
            if (kernel32 == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "kernel32.dll could not be loaded.");

            IntPtr create = Native.GetProcAddress(kernel32, "CreatePseudoConsole");
            IntPtr close = Native.GetProcAddress(kernel32, "ClosePseudoConsole");
            if (create == IntPtr.Zero || close == IntPtr.Zero)
                throw new PlatformNotSupportedException(
                    "ConPTY (CreatePseudoConsole) is unavailable; Windows 10 version 1809 or later is required.");

            _createPseudoConsole = (CreatePseudoConsoleDelegate)Marshal.GetDelegateForFunctionPointer(create, typeof(CreatePseudoConsoleDelegate));
            _closePseudoConsole = (ClosePseudoConsoleDelegate)Marshal.GetDelegateForFunctionPointer(close, typeof(ClosePseudoConsoleDelegate));
        }

        private void StartWorkers()
        {
            Thread dispatcher = new Thread(DispatchLoop);
            dispatcher.Name = "OwnedTerminal.output";
            dispatcher.IsBackground = true;
            dispatcher.Start();
            _dispatcherStarted = true;

            Thread reader = new Thread(ReaderLoop);
            reader.Name = "OwnedTerminal.read";
            reader.IsBackground = true;
            reader.Start();
            _readerStarted = true;

            Thread writer = new Thread(WriterLoop);
            writer.Name = "OwnedTerminal.write";
            writer.IsBackground = true;
            writer.Start();
            _writerStarted = true;

            Thread reaper = new Thread(ReaperLoop);
            reaper.Name = "OwnedTerminal.reaper";
            reaper.IsBackground = true;
            reaper.Start();
            _reaperStarted = true;
        }

        private void ReaderLoop()
        {
            _readerThreadId = Thread.CurrentThread.ManagedThreadId;
            _readerNative = CaptureThreadHandle();

            byte[] buffer = new byte[4096];
            char[] characters = new char[buffer.Length + 1];
            Decoder decoder = Utf8.GetDecoder();

            try
            {
                while (true)
                {
                    uint read;
                    if (!Native.ReadFile(_outputRead, buffer, (uint)buffer.Length, out read, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != ErrorBrokenPipe && error != ErrorOperationAborted && error != ErrorInvalidHandle)
                            SetReason("terminal read failed (win32 " + error.ToString(CultureInfo.InvariantCulture) + ")");
                        break;
                    }
                    if (read == 0) break;

                    int count = decoder.GetChars(buffer, 0, (int)read, characters, 0, false);
                    if (count > 0) EnqueueOutput(new string(characters, 0, count));
                }

                int tail = decoder.GetChars(new byte[0], 0, 0, characters, 0, true);
                if (tail > 0) EnqueueOutput(new string(characters, 0, tail));
            }
            catch (Exception)
            {
                SetReason("terminal read failed");
            }
            finally
            {
                _outputEnded = true;
                lock (_gate) Monitor.PulseAll(_gate);
                _readerDone.Set();
                CloseIfSet(ref _outputRead);
                CloseIfSet(ref _readerNative);
            }
        }

        private void WriterLoop()
        {
            _writerThreadId = Thread.CurrentThread.ManagedThreadId;
            _writerNative = CaptureThreadHandle();

            try
            {
                while (true)
                {
                    string item;
                    lock (_gate)
                    {
                        if (_writes.Count > 0)
                        {
                            item = _writes.Dequeue();
                            _queuedWriteCharacters -= item.Length;
                        }
                        else
                        {
                            if (_stopping || _writeBroken) break;
                            Monitor.Wait(_gate);
                            continue;
                        }
                    }

                    byte[] bytes = Utf8.GetBytes(item);
                    uint written;
                    if (!Native.WriteFile(_inputWrite, bytes, (uint)bytes.Length, out written, IntPtr.Zero) ||
                        written != bytes.Length)
                    {
                        int error = Marshal.GetLastWin32Error();
                        _writeBroken = true;
                        if (error != ErrorBrokenPipe && error != ErrorOperationAborted && error != ErrorInvalidHandle)
                            SetReason("terminal write failed (win32 " + error.ToString(CultureInfo.InvariantCulture) + ")");
                        break;
                    }
                }
            }
            catch (Exception)
            {
                _writeBroken = true;
                SetReason("terminal write failed");
            }
            finally
            {
                CloseIfSet(ref _inputWrite); // EOF for the child
                _writerDone.Set();
                CloseIfSet(ref _writerNative);
            }
        }

        private void DispatchLoop()
        {
            _dispatcherThreadId = Thread.CurrentThread.ManagedThreadId;

            try
            {
                while (true)
                {
                    string item = null;
                    bool ended;

                    lock (_gate)
                    {
                        while (_output.Count == 0 && !_outputEnded) Monitor.Wait(_gate);
                        if (_output.Count > 0)
                        {
                            item = _output.Dequeue();
                            Monitor.PulseAll(_gate);
                        }
                        ended = _outputEnded && _output.Count == 0;
                    }

                    if (item != null) Invoke(Output, item);
                    if (ended) return;
                }
            }
            finally
            {
                CompleteClosed();
            }
        }

        private void ReaperLoop()
        {
            _reaperThreadId = Thread.CurrentThread.ManagedThreadId;
            _reaperNative = CaptureThreadHandle();

            try
            {
                uint result = Native.WaitForSingleObject(_process, Infinite);
                if (result == WaitObject0)
                {
                    uint exitCode;
                    if (Native.GetExitCodeProcess(_process, out exitCode))
                        SetReason("terminal exited with code " + exitCode.ToString(CultureInfo.InvariantCulture));
                    else
                        SetReason("terminal exited");
                }
                else
                {
                    SetReason("terminal exit wait failed");
                }
            }
            catch (Exception)
            {
                SetReason("terminal exit wait failed");
            }
            finally
            {
                BeginTeardown();
                CloseIfSet(ref _reaperNative);
                CloseIfSet(ref _process); // The waiting thread owns this handle.
                _reaperDone.Set();
            }
        }

        private void EnqueueOutput(string text)
        {
            lock (_gate)
            {
                if (_outputEnded) return;
                int limit = _stopping ? MaxOutputChunksWhileStopping : MaxOutputChunks;
                while (_output.Count >= limit && !_stopping) Monitor.Wait(_gate, 250);
                if (_output.Count >= MaxOutputChunksWhileStopping)
                {
                    _outputTruncated = true;
                    return;
                }
                _output.Enqueue(text);
                Monitor.PulseAll(_gate);
            }
        }

        private void CompleteClosed()
        {
            if (!_raiseClosed) return;
            if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;

            string reason = _reason;
            if (reason == null) reason = "terminal closed";
            if (_outputTruncated) reason = reason + "; output truncated";
            Invoke(Closed, reason);
        }

        private static void Invoke(Action<string> handler, string text)
        {
            if (handler == null) return;
            Delegate[] receivers = handler.GetInvocationList();
            for (int i = 0; i < receivers.Length; i++)
            {
                try
                {
                    ((Action<string>)receivers[i])(text);
                }
                catch (Exception)
                {
                    // A faulty subscriber must not break the drain or the close notice.
                }
            }
        }

        private void AbortStart()
        {
            _stopping = true;
            lock (_gate) Monitor.PulseAll(_gate);

            // A child that never reached the job cannot be killed through it.
            if (!_contained && _process != IntPtr.Zero) Native.TerminateProcess(_process, 1);

            IntPtr job = TakeHandle(ref _job);
            if (job != IntPtr.Zero)
            {
                Native.TerminateJobObject(job, 1);
                Native.CloseHandle(job);
            }

            if (_process == IntPtr.Zero && _pseudoConsole == IntPtr.Zero)
            {
                CloseIfSet(ref _inputWrite);
                CloseIfSet(ref _outputRead);
                _outputEnded = true;
                _terminalDone.Set();
                return;
            }

            BeginTeardown();
        }

        private void PerformForcedStop()
        {
            if (_terminalDone.Wait(0)) return;

            CancelWorkerIo(ref _writerNative);

            IntPtr job = TakeHandle(ref _job);
            if (job != IntPtr.Zero)
            {
                Native.TerminateJobObject(job, 1);
                Native.CloseHandle(job);
            }
            else if (!_contained && _process != IntPtr.Zero)
            {
                Native.TerminateProcess(_process, 1);
            }

            if (!_reaperStarted) BeginTeardown();
        }

        private void ScheduleForcedStop()
        {
            Thread forced = new Thread(ForcedStopAfterGrace);
            forced.Name = "OwnedTerminal.stop";
            forced.IsBackground = true;
            try
            {
                forced.Start();
            }
            catch (Exception)
            {
                PerformForcedStop();
            }
        }

        private void ForcedStopAfterGrace()
        {
            try
            {
                Thread.Sleep(GracefulCloseMilliseconds);
            }
            catch (Exception)
            {
            }
            PerformForcedStop();
        }

        private void BeginTeardown()
        {
            if (Interlocked.CompareExchange(ref _teardownStarted, 1, 0) != 0) return;

            Thread teardown = new Thread(TeardownLoop);
            teardown.Name = "OwnedTerminal.teardown";
            teardown.IsBackground = true;
            try
            {
                teardown.Start();
            }
            catch (Exception)
            {
                TeardownLoop();
            }
        }

        private void TeardownLoop()
        {
            try
            {
                _stopping = true;
                lock (_gate) Monitor.PulseAll(_gate);

                IntPtr pseudoConsole = TakeHandle(ref _pseudoConsole);
                if (pseudoConsole != IntPtr.Zero && _closePseudoConsole != null)
                {
                    try
                    {
                        // Bounded only by ConPTY itself; on old builds this can block
                        // indefinitely, which is why it never runs on the caller's thread.
                        _closePseudoConsole(pseudoConsole);
                    }
                    catch (Exception)
                    {
                    }
                }

                if (_readerStarted && !_readerDone.Wait(OutputDrainMilliseconds))
                {
                    CancelWorkerIo(ref _readerNative);
                    _readerDone.Wait(500);
                }

                if (_writerStarted && !_writerDone.Wait(WriteDrainMilliseconds))
                {
                    CancelWorkerIo(ref _writerNative);
                    _writerDone.Wait(500);
                }

                _outputEnded = true;
                lock (_gate) Monitor.PulseAll(_gate);
                if (!_dispatcherStarted) CompleteClosed();

                // Only close a pipe handle once its worker is provably done with it.
                if (!_readerStarted || _readerDone.Wait(0)) CloseIfSet(ref _outputRead);
                if (!_writerStarted || _writerDone.Wait(0)) CloseIfSet(ref _inputWrite);

                // The reaper owns the process handle while its wait is pending.
                if (_reaperStarted) _reaperDone.Wait(2000);
                else CloseIfSet(ref _process);
                CloseIfSet(ref _job);
                CancelWorkerIo(ref _readerNative);
                CancelWorkerIo(ref _writerNative);
            }
            catch (Exception)
            {
            }
            finally
            {
                _terminalDone.Set();
            }
        }

        private bool IsWorkerThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            return current == _dispatcherThreadId || current == _readerThreadId ||
                   current == _writerThreadId || current == _reaperThreadId;
        }

        private void SetReason(string reason)
        {
            Interlocked.CompareExchange(ref _reason, reason, null);
        }

        private static IntPtr CaptureThreadHandle()
        {
            IntPtr duplicate;
            if (!Native.DuplicateHandle(Native.GetCurrentProcess(), Native.GetCurrentThread(),
                    Native.GetCurrentProcess(), out duplicate, 0, false, DuplicateSameAccess))
                return IntPtr.Zero;
            return duplicate;
        }

        private static void CancelWorkerIo(ref IntPtr field)
        {
            IntPtr handle = TakeHandle(ref field);
            if (handle == IntPtr.Zero) return;
            Native.CancelSynchronousIo(handle);
            Native.CloseHandle(handle);
        }

        private static IntPtr TakeHandle(ref IntPtr field)
        {
            return Interlocked.Exchange(ref field, IntPtr.Zero);
        }

        private static void CloseIfSet(ref IntPtr field)
        {
            IntPtr handle = TakeHandle(ref field);
            if (handle != IntPtr.Zero) Native.CloseHandle(handle);
        }

        // No native wait handle is allocated for an idle or never-started object.
        // The predicate loop tolerates spurious wakeups and honors a monotonic timeout.
        internal sealed class Completion
        {
            private readonly object gate = new object();
            private bool done;
            public void Set() { lock (gate) { done = true; Monitor.PulseAll(gate); } }
            public bool Wait(int milliseconds)
            {
                if (milliseconds < 0) throw new ArgumentOutOfRangeException("milliseconds");
                lock (gate)
                {
                    if (done) return true;
                    if (milliseconds == 0) return false;
                    Stopwatch watch = Stopwatch.StartNew();
                    while (!done)
                    {
                        long remaining = milliseconds - watch.ElapsedMilliseconds;
                        if (remaining <= 0) return false;
                        Monitor.Wait(gate, (int)remaining);
                    }
                    return true;
                }
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int CreatePseudoConsoleDelegate(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void ClosePseudoConsoleDelegate(IntPtr pseudoConsole);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Coord
        {
            public short X;
            public short Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfo
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private static class Native
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr GetModuleHandleW(string fileName);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr GetProcAddress(IntPtr module, string procName);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, ref SecurityAttributes attributes, uint size);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool TerminateJobObject(IntPtr job, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool CloseHandle(IntPtr handle);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern uint ResumeThread(IntPtr thread);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool ReadFile(IntPtr file, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool WriteFile(IntPtr file, byte[] buffer, uint toWrite, out uint written, IntPtr overlapped);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool CancelSynchronousIo(IntPtr thread);

            [DllImport("kernel32.dll")]
            internal static extern IntPtr GetCurrentThread();

            [DllImport("kernel32.dll")]
            internal static extern IntPtr GetCurrentProcess();

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess,
                out IntPtr target, uint access, bool inherit, uint options);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute,
                IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);

            [DllImport("kernel32.dll", SetLastError = true)]
            internal static extern void DeleteProcThreadAttributeList(IntPtr list);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern bool CreateProcessW(string application, StringBuilder commandLine,
                IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags,
                IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);
        }
    }
}

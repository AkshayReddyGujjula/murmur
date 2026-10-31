using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Murmur.Next.Windows
{
    // Owns a NEW process and its pseudoterminal. Never finds/attaches to an existing
    // process, window or console. No native resources are created until Start().
    public sealed class PseudoTerminal : IDisposable
    {
        private readonly BlockingCollection<byte[]> input = new BlockingCollection<byte[]>(128);
        private readonly object lifecycle = new object();
        private FileStream inputStream, outputStream;
        private IntPtr console, job, process;
        private bool started, disposed;
        private int faultReported;
        public event Action<string> Output;
        public event Action<Exception> Faulted;

        public void Start(string executable, string[] arguments, string directory,
                          IDictionary<string, string> environment)
        {
            if (!Path.IsPathRooted(executable) || !File.Exists(executable))
                throw new ArgumentException("An existing absolute executable path is required.");
            if (!Path.IsPathRooted(directory) || !Directory.Exists(directory))
                throw new ArgumentException("An existing absolute working directory is required.");
            lock (lifecycle)
            {
                if (started || disposed) throw new InvalidOperationException("Terminal cannot be restarted.");
                started = true;
                IntPtr inRead = IntPtr.Zero, inWrite = IntPtr.Zero;
                IntPtr outRead = IntPtr.Zero, outWrite = IntPtr.Zero;
                IntPtr attributes = IntPtr.Zero, env = IntPtr.Zero;
                PROCESS_INFORMATION child = new PROCESS_INFORMATION();
                bool resumed = false, attributesInitialized = false;
                try
                {
                    Check(CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0));
                    Check(CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0));
                    int hr = CreatePseudoConsole(new COORD { X = 120, Y = 40 }, inRead, outWrite, 0, out console);
                    Marshal.ThrowExceptionForHR(hr);
                    IntPtr size = IntPtr.Zero;
                    InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                    if (size == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    attributes = Marshal.AllocHGlobal(size);
                    Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref size));
                    attributesInitialized = true;
                    Check(UpdateProcThreadAttribute(attributes, 0, (IntPtr)0x00020016,
                        console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
                    STARTUPINFOEX startup = new STARTUPINFOEX();
                    startup.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));
                    startup.AttributeList = attributes;
                    env = Marshal.StringToHGlobalUni(BuildEnvironment(environment));
                    StringBuilder command = new StringBuilder(QuoteArgument(executable));
                    foreach (string argument in arguments) command.Append(' ').Append(QuoteArgument(argument));
                    // Suspended until assigned to our private kill-on-close job: no child
                    // can escape the cleanup boundary during startup.
                    Check(CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                        0x00080000 | 0x00000400 | 0x00000004, env, directory, ref startup, out child));
                    process = child.Process;
                    job = CreateJobObjectW(IntPtr.Zero, null);
                    Check(job != IntPtr.Zero);
                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    limits.BasicLimitInformation.LimitFlags = 0x00002000; // KILL_ON_JOB_CLOSE
                    Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)));
                    Check(AssignProcessToJobObject(job, process));
                    inputStream = new FileStream(new SafeFileHandle(inWrite, true), FileAccess.Write);
                    inWrite = IntPtr.Zero;
                    outputStream = new FileStream(new SafeFileHandle(outRead, true), FileAccess.Read);
                    outRead = IntPtr.Zero;
                    new Thread(ReadOutput) { IsBackground = true, Name = "Murmur terminal output" }.Start();
                    new Thread(WriteInput) { IsBackground = true, Name = "Murmur terminal input" }.Start();
                    if (ResumeThread(child.Thread) == UInt32.MaxValue)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    resumed = true;
                }
                finally
                {
                    Close(ref child.Thread);
                    Close(ref inRead); Close(ref outWrite); Close(ref inWrite); Close(ref outRead);
                    if (attributesInitialized) DeleteProcThreadAttributeList(attributes);
                    if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                    if (env != IntPtr.Zero) Marshal.FreeHGlobal(env);
                    if (!resumed)
                    {
                        if (process != IntPtr.Zero) TerminateProcess(process, 1);
                        Dispose();
                    }
                }
            }
        }

        public bool Send(string text)
        {
            lock (lifecycle)
            {
                if (!started || disposed || text == null) return false;
                return input.TryAdd(Encoding.UTF8.GetBytes(text));
            }
        }

        private void ReadOutput()
        {
            try
            {
                byte[] bytes = new byte[8192];
                char[] chars = new char[8192];
                Decoder decoder = new UTF8Encoding(false, false).GetDecoder();
                int count;
                while ((count = outputStream.Read(bytes, 0, bytes.Length)) > 0)
                {
                    int length = decoder.GetChars(bytes, 0, count, chars, 0, false);
                    Action<string> handler = Output;
                    if (handler != null && length > 0) handler(new string(chars, 0, length));
                }
            }
            catch (Exception error) { Report(error); }
            finally { Report(new EndOfStreamException("The owned terminal exited.")); }
        }

        private void WriteInput()
        {
            try
            {
                foreach (byte[] bytes in input.GetConsumingEnumerable())
                {
                    inputStream.Write(bytes, 0, bytes.Length);
                    inputStream.Flush();
                }
            }
            catch (Exception error) { Report(error); }
        }

        private void Report(Exception error)
        {
            lock (lifecycle) { if (disposed) return; }
            if (Interlocked.Exchange(ref faultReported, 1) != 0) return;
            Action<Exception> handler = Faulted;
            if (handler != null) handler(error);
        }

        public void Dispose()
        {
            IntPtr ownedJob, ownedConsole, ownedProcess;
            FileStream ownedInput, ownedOutput;
            lock (lifecycle)
            {
                if (disposed) return;
                disposed = true;
                input.CompleteAdding();
                ownedJob = job; job = IntPtr.Zero;
                ownedConsole = console; console = IntPtr.Zero;
                ownedProcess = process; process = IntPtr.Zero;
                ownedInput = inputStream; ownedOutput = outputStream;
            }
            // Never hold lifecycle while waiting on native shutdown: the reader
            // must remain free to drain and event handlers may call Send().
            Close(ref ownedJob);
            if (ownedConsole != IntPtr.Zero) ClosePseudoConsole(ownedConsole);
            if (ownedInput != null) ownedInput.Dispose();
            if (ownedOutput != null) ownedOutput.Dispose();
            Close(ref ownedProcess);
        }

        public static string QuoteArgument(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("Invalid argument.");
            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static string BuildEnvironment(IDictionary<string, string> changes)
        {
            SortedDictionary<string, string> values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry item in Environment.GetEnvironmentVariables()) values[(string)item.Key] = (string)item.Value;
            if (changes != null) foreach (KeyValuePair<string, string> item in changes)
            {
                if (String.IsNullOrEmpty(item.Key) || item.Key.IndexOfAny(new char[] { '=', '\0' }) >= 0 ||
                    (item.Value != null && item.Value.IndexOf('\0') >= 0)) throw new ArgumentException("Invalid environment entry.");
                if (item.Value == null) values.Remove(item.Key); else values[item.Key] = item.Value;
            }
            StringBuilder block = new StringBuilder();
            foreach (KeyValuePair<string, string> item in values) block.Append(item.Key).Append('=').Append(item.Value).Append('\0');
            return block.Append('\0').ToString();
        }

        private static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        private static void Close(ref IntPtr handle) { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
        [StructLayout(LayoutKind.Sequential)] private struct COORD { public short X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct STARTUPINFO
        {
            public int cb; public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, Reserved2Size; public IntPtr Reserved2, StdInput, StdOutput, StdError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
        [StructLayout(LayoutKind.Sequential)] private struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoInfo; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, uint size);
        [DllImport("kernel32.dll")] private static extern int CreatePseudoConsole(COORD size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace Murmur.App.Audio
{
    public sealed class MicLevelMeter : IDisposable
    {
        // Stop/Dispose drain in-flight callbacks. Handlers must stay short and
        // must not synchronously marshal to a UI thread which may be stopping us.
        public event Action<float> Level;
        public event Action<string> Error;

        private const uint WhdrDone = 0x1;
        private const int BufferCount = 4;
        private const int BufferBytes = 1024;
        private const int CallbackWaitMs = 200;

        // One physical worker and at most one replaceable pending request. A
        // hung driver must never accumulate waiting threads on repeated takes.
        private sealed class Session
        {
            public readonly ManualResetEvent Cancel = new ManualResetEvent(false);
            public Thread Worker;
            public bool ReleaseFailed;
            public bool FaultReported;
        }

        // If a driver refuses Close it may still signal this callback handle.
        // Keep it rooted until process exit rather than let its OS value be reused.
        private static readonly List<AutoResetEvent> retainedCallbacks = new List<AutoResetEvent>();

        private readonly object gate = new object();
        private readonly object emitGate = new object();
        private readonly Func<IMicNative> nativeFactory;
        private readonly Func<int> ticks;
        private bool disposed;
        private bool running;
        private Session current;
        private Session active;
        private Session blocked;
        private int lastLevelTick;

        public MicLevelMeter() : this(null)
        {
        }

        internal MicLevelMeter(Func<IMicNative> nativeFactory)
            : this(nativeFactory, delegate { return Environment.TickCount; })
        {
        }

        internal MicLevelMeter(Func<IMicNative> nativeFactory, Func<int> ticks)
        {
            if (ticks == null) throw new ArgumentNullException("ticks");
            this.nativeFactory = nativeFactory;
            this.ticks = ticks;
        }

        // True once Start is accepted and until Stop, Dispose or a driver fault,
        // even while the worker is still opening or releasing the driver.
        public bool IsRunning
        {
            get
            {
                lock (gate)
                {
                    return running;
                }
            }
        }

        public void Start()
        {
            Session session = null;
            bool launch = false;
            lock (gate)
            {
                if (disposed || running)
                {
                    return;
                }
                if (blocked == null)
                {
                    running = true;
                    lastLevelTick = unchecked(ticks() - 33);
                    if (current != null && current != active) current.Cancel.Close();
                    session = new Session();
                    current = session;
                    launch = active == null;
                    if (launch) active = session;
                }
            }
            if (session == null) { ReportBlockedRequest(); return; }
            if (launch) StartWorker(session);
        }

        private void StartWorker(Session session)
        {
            try
            {
                session.Worker = new Thread(delegate() { SessionLoop(session); });
                session.Worker.IsBackground = true;
                session.Worker.Name = "MicLevelMeter";
                session.Worker.Start();
            }
            catch (Exception)
            {
                RaiseErrorIfLive(session, "Microphone could not be opened.");
                MarkStartFailed(session);
                FinishSession(session);
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (running)
                {
                    running = false;
                    if (current != null) current.Cancel.Set();
                }
            }

            // Suppress levels before returning. The worker still owns reset,
            // unprepare and close and may take as long as the driver takes.
            lock (emitGate)
            {
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (!disposed)
                {
                    disposed = true;
                    running = false;
                    if (active != null) active.Cancel.Set();
                    if (current != null && current != active)
                    {
                        current.Cancel.Close();
                        current = active;
                    }
                }
            }
            lock (emitGate)
            {
            }
        }

        private void SessionLoop(Session session)
        {
            IMicNative native = null;
            IntPtr handle = IntPtr.Zero;
            AutoResetEvent ready = null;
            IntPtr[] headers = new IntPtr[BufferCount];
            IntPtr[] data = new IntPtr[BufferCount];
            bool[] prepared = new bool[BufferCount];
            bool faultReported = false;
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));

            try
            {
                if (IsCancelled(session))
                {
                    return;
                }

                try
                {
                    native = nativeFactory != null ? nativeFactory() : new WaveInMicNative();
                }
                catch (Exception)
                {
                    faultReported = true;
                    RaiseErrorIfLive(session, "Microphone could not be opened.");
                    MarkStartFailed(session);
                    return;
                }
                if (native == null)
                {
                    faultReported = true;
                    RaiseErrorIfLive(session, "Microphone could not be opened.");
                    MarkStartFailed(session);
                    return;
                }

                ready = new AutoResetEvent(false);
                int openResult = native.Open(out handle, ready);
                if (openResult != 0 || handle == IntPtr.Zero)
                {
                    handle = IntPtr.Zero;
                    faultReported = true;
                    RaiseErrorIfLive(session, "Microphone could not be opened (error " + openResult + ").");
                    MarkStartFailed(session);
                    return;
                }
                if (IsCancelled(session))
                {
                    return;
                }

                for (int i = 0; i < BufferCount; i++)
                {
                    headers[i] = Marshal.AllocHGlobal(headerSize);
                    data[i] = Marshal.AllocHGlobal(BufferBytes);

                    WAVEHDR header = new WAVEHDR();
                    header.lpData = data[i];
                    header.dwBufferLength = (uint)BufferBytes;
                    header.dwBytesRecorded = 0;
                    header.dwUser = IntPtr.Zero;
                    header.dwFlags = 0;
                    header.dwLoops = 0;
                    header.lpNext = IntPtr.Zero;
                    header.reserved = IntPtr.Zero;
                    Marshal.StructureToPtr(header, headers[i], false);

                    int prepareResult = native.PrepareHeader(handle, headers[i], headerSize);
                    if (prepareResult != 0)
                    {
                        faultReported = true;
                        RaiseErrorIfLive(session, "Microphone could not be opened (error " + prepareResult + ").");
                        MarkStartFailed(session);
                        return;
                    }
                    prepared[i] = true;

                    if (IsCancelled(session))
                    {
                        return;
                    }

                    int addResult = native.AddBuffer(handle, headers[i], headerSize);
                    if (addResult != 0)
                    {
                        faultReported = true;
                        RaiseErrorIfLive(session, "Microphone could not be opened (error " + addResult + ").");
                        MarkStartFailed(session);
                        return;
                    }
                    if (IsCancelled(session))
                    {
                        return;
                    }
                }

                int startResult = native.Start(handle);
                if (startResult != 0)
                {
                    faultReported = true;
                    RaiseErrorIfLive(session, "Microphone could not be opened (error " + startResult + ").");
                    MarkStartFailed(session);
                    return;
                }

                lock (gate)
                {
                    lastLevelTick = unchecked(ticks() - 33);
                }

                if (CaptureLoop(session, native, handle, ready, headers))
                {
                    faultReported = true;
                }
            }
            catch (Exception)
            {
                if (!faultReported)
                {
                    faultReported = true;
                    RaiseErrorIfLive(session, "Microphone could not be opened.");
                }
                MarkStartFailed(session);
            }
            finally
            {
                try
                {
                    session.FaultReported = faultReported;
                    CleanupDevice(session, native, handle, ready, headers, data, prepared);
                }
                catch (Exception)
                {
                }
                FinishSession(session);
            }
        }

        private bool CaptureLoop(Session session, IMicNative native, IntPtr handle, AutoResetEvent ready, IntPtr[] headers)
        {
            byte[] copy = new byte[BufferBytes];
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            WaitHandle[] waits = new WaitHandle[] { session.Cancel, ready };

            while (!IsCancelled(session))
            {
                try
                {
                    WaitHandle.WaitAny(waits, CallbackWaitMs);
                }
                catch (Exception)
                {
                    return false;
                }
                if (IsCancelled(session))
                {
                    break;
                }

                for (int i = 0; i < headers.Length; i++)
                {
                    if (IsCancelled(session))
                    {
                        break;
                    }
                    IntPtr headerPtr = headers[i];
                    if (headerPtr == IntPtr.Zero)
                    {
                        continue;
                    }

                    WAVEHDR header = (WAVEHDR)Marshal.PtrToStructure(headerPtr, typeof(WAVEHDR));
                    if ((header.dwFlags & WhdrDone) == 0)
                    {
                        continue;
                    }

                    uint recorded = header.dwBytesRecorded;
                    if (recorded > 0)
                    {
                        uint toCopy = recorded;
                        if (toCopy > (uint)BufferBytes)
                        {
                            toCopy = (uint)BufferBytes;
                        }
                        try
                        {
                            Marshal.Copy(header.lpData, copy, 0, (int)toCopy);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        float rms = LevelMath.Rms16(copy, (int)toCopy);
                        EmitLevel(session, LevelMath.ToMeter(rms));
                    }

                    if (IsCancelled(session))
                    {
                        break;
                    }

                    int addResult = native.AddBuffer(handle, headerPtr, headerSize);
                    if (addResult != 0)
                    {
                        // One rearm failure ends this capture session; the finally
                        // block still resets, unprepares and closes the device.
                        RaiseErrorIfLive(session, "Microphone level meter stopped (error " + addResult + ").");
                        MarkStartFailed(session);
                        return true;
                    }
                }
            }
            return false;
        }

        private void CleanupDevice(Session session, IMicNative native, IntPtr handle, AutoResetEvent ready, IntPtr[] headers, IntPtr[] data, bool[] prepared)
        {
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));

            if (native != null && handle != IntPtr.Zero)
            {
                try
                {
                    native.Reset(handle);
                }
                catch (Exception)
                {
                }

                for (int i = 0; i < headers.Length; i++)
                {
                    if (prepared[i])
                    {
                        ReleasePreparedBuffer(native, handle, headers[i], data[i], headerSize);
                    }
                    else
                    {
                        FreeBuffer(headers[i], data[i]);
                    }
                }

                int closeResult = -1;
                try
                {
                    closeResult = native.Close(handle);
                }
                catch (Exception)
                {
                    closeResult = -1;
                }
                if (closeResult != 0)
                {
                    session.ReleaseFailed = true;
                    lock (retainedCallbacks) retainedCallbacks.Add(ready);
                    ready = null;
                }
            }

            if (ready != null)
            {
                try
                {
                    ready.Close();
                }
                catch (Exception)
                {
                }
            }
        }

        private static void ReleasePreparedBuffer(IMicNative native, IntPtr handle, IntPtr headerPtr, IntPtr dataPtr, int headerSize)
        {
            if (headerPtr == IntPtr.Zero)
            {
                return;
            }

            int result;
            try
            {
                result = native.UnprepareHeader(handle, headerPtr, headerSize);
            }
            catch (Exception)
            {
                result = -1;
            }

            int retries = 0;
            while (MeterTeardown.ShouldRetryUnprepare(result, retries))
            {
                Thread.Sleep(MeterTeardown.RetryDelayMs);
                try
                {
                    result = native.UnprepareHeader(handle, headerPtr, headerSize);
                }
                catch (Exception)
                {
                    result = -1;
                    break;
                }
                retries++;
            }

            if (!MeterTeardown.MayFreeBuffer(result))
            {
                // The driver may still reference this block; leak it rather than
                // freeing memory we do not own.
                return;
            }

            FreeBuffer(headerPtr, dataPtr);
        }

        private static void FreeBuffer(IntPtr headerPtr, IntPtr dataPtr)
        {
            if (headerPtr != IntPtr.Zero)
            {
                try
                {
                    Marshal.FreeHGlobal(headerPtr);
                }
                catch (Exception)
                {
                }
            }
            if (dataPtr != IntPtr.Zero)
            {
                try
                {
                    Marshal.FreeHGlobal(dataPtr);
                }
                catch (Exception)
                {
                }
            }
        }

        private void EmitLevel(Session session, float value)
        {
            lock (emitGate)
            {
                if (!IsLive(session))
                {
                    return;
                }

                int now = ticks();
                int last;
                lock (gate)
                {
                    last = lastLevelTick;
                }
                if (unchecked(now - last) < 33)
                {
                    return;
                }
                lock (gate)
                {
                    lastLevelTick = now;
                }

                Action<float> handler = Level;
                if (handler == null)
                {
                    return;
                }
                try
                {
                    handler(value);
                }
                catch (Exception)
                {
                }
            }
        }

        private void RaiseErrorIfLive(Session session, string message)
        {
            lock (emitGate)
            {
                if (!IsLive(session))
                {
                    return;
                }
                Action<string> handler = Error;
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
        }

        private void MarkStartFailed(Session session)
        {
            lock (gate)
            {
                if (current == session && running)
                {
                    running = false;
                }
            }
        }

        private bool IsLive(Session session)
        {
            lock (gate)
            {
                return !disposed && running && current == session;
            }
        }

        private bool IsCancelled(Session session)
        {
            return session.Cancel.WaitOne(0);
        }

        private void FinishSession(Session session)
        {
            if (session.ReleaseFailed)
            {
                Session affected;
                lock (gate) { blocked = session; affected = current; }
                if (affected != null && (affected != session || !session.FaultReported))
                    RaiseErrorIfLive(affected, "Microphone meter could not release its device; restart Murmur before trying again.");
            }
            Session next = null;
            lock (gate)
            {
                if (active == session) active = null;
                if (current == session)
                {
                    current = null;
                    running = false;
                }
                else if (!disposed && blocked == null && running && current != null)
                {
                    next = current;
                    active = next;
                }
                else if (current != null && active == null)
                {
                    current.Cancel.Close();
                    current = null;
                    running = false;
                }
                session.Cancel.Close();
            }
            if (next != null) StartWorker(next);
        }

        private void ReportBlockedRequest()
        {
            lock (emitGate)
            {
                lock (gate) { if (disposed) return; }
                Action<string> handler = Error;
                if (handler != null)
                    try { handler("Microphone meter could not release its device; restart Murmur before trying again."); }
                    catch (Exception) { }
            }
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Murmur.App.Audio
{
    public sealed class MicLevelMeter : IDisposable
    {
        public event Action<float> Level;
        public event Action<string> Error;

        private const uint WaveMapper = 0xFFFFFFFF;
        private const uint CallbackEvent = 0x50000;
        private const uint WhdrDone = 0x1;
        private const int BufferCount = 4;
        private const int BufferBytes = 1024;
        private const int JoinTimeoutMs = 2000;

        private readonly object gate = new object();
        private bool disposed;
        private bool running;
        private bool tearingDown;
        private volatile bool stopping;
        private IntPtr waveHandle;
        private IntPtr[] headerPtrs;
        private IntPtr[] dataPtrs;
        private AutoResetEvent readyEvent;
        private Thread workerThread;
        private int lastLevelTick;

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
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                if (running || tearingDown)
                {
                    return;
                }
                // Reset here, never at the end of Stop: a worker that outlived a
                // join timeout must not be able to resume and touch the device.
                stopping = false;
            }

            AutoResetEvent evt = null;
            IntPtr handle = IntPtr.Zero;
            IntPtr[] hPtrs = null;
            IntPtr[] dPtrs = null;
            Thread worker = null;
            bool cleanupStarted = false;

            try
            {
                evt = new AutoResetEvent(false);

                WAVEFORMATEX fmt = new WAVEFORMATEX();
                fmt.wFormatTag = 1;
                fmt.nChannels = 1;
                fmt.nSamplesPerSec = 16000;
                fmt.nAvgBytesPerSec = 32000;
                fmt.nBlockAlign = 2;
                fmt.wBitsPerSample = 16;
                fmt.cbSize = 0;

                IntPtr eventHandle = evt.SafeWaitHandle.DangerousGetHandle();
                int openResult = waveInOpen(out handle, WaveMapper, ref fmt, eventHandle, IntPtr.Zero, CallbackEvent);
                if (openResult != 0 || handle == IntPtr.Zero)
                {
                    int code = openResult;
                    try
                    {
                        evt.Close();
                    }
                    catch
                    {
                    }
                    handle = IntPtr.Zero;
                    evt = null;
                    RaiseError("Microphone could not be opened (error " + code + ").");
                    return;
                }

                int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                hPtrs = new IntPtr[BufferCount];
                dPtrs = new IntPtr[BufferCount];

                for (int i = 0; i < BufferCount; i++)
                {
                    hPtrs[i] = IntPtr.Zero;
                    dPtrs[i] = IntPtr.Zero;

                    IntPtr hdrPtr = Marshal.AllocHGlobal(headerSize);
                    hPtrs[i] = hdrPtr;

                    IntPtr data = Marshal.AllocHGlobal(BufferBytes);
                    dPtrs[i] = data;

                    WAVEHDR hdr = new WAVEHDR();
                    hdr.lpData = data;
                    hdr.dwBufferLength = (uint)BufferBytes;
                    hdr.dwBytesRecorded = 0;
                    hdr.dwUser = IntPtr.Zero;
                    hdr.dwFlags = 0;
                    hdr.dwLoops = 0;
                    hdr.lpNext = IntPtr.Zero;
                    hdr.reserved = IntPtr.Zero;
                    Marshal.StructureToPtr(hdr, hdrPtr, false);

                    int prep = waveInPrepareHeader(handle, hdrPtr, (uint)headerSize);
                    if (prep != 0)
                    {
                        cleanupStarted = true;
                        CleanupFailedStart(handle, hPtrs, dPtrs, evt);
                        RaiseError("Microphone could not be opened (error " + prep + ").");
                        return;
                    }

                    int add = waveInAddBuffer(handle, hdrPtr, (uint)headerSize);
                    if (add != 0)
                    {
                        cleanupStarted = true;
                        CleanupFailedStart(handle, hPtrs, dPtrs, evt);
                        RaiseError("Microphone could not be opened (error " + add + ").");
                        return;
                    }
                }

                int startResult = waveInStart(handle);
                if (startResult != 0)
                {
                    cleanupStarted = true;
                    CleanupFailedStart(handle, hPtrs, dPtrs, evt);
                    RaiseError("Microphone could not be opened (error " + startResult + ").");
                    return;
                }

                Thread candidate = new Thread(WorkerLoop);
                candidate.IsBackground = true;
                candidate.Name = "MicLevelMeter";

                bool abandoned;
                lock (gate)
                {
                    abandoned = disposed || running;
                    if (!abandoned)
                    {
                        waveHandle = handle;
                        readyEvent = evt;
                        headerPtrs = hPtrs;
                        dataPtrs = dPtrs;
                        lastLevelTick = 0;
                        worker = candidate;
                        workerThread = worker;
                        running = true;
                        handle = IntPtr.Zero;
                        evt = null;
                        hPtrs = null;
                        dPtrs = null;
                    }
                }

                if (abandoned)
                {
                    cleanupStarted = true;
                    CleanupFailedStart(handle, hPtrs, dPtrs, evt);
                    return;
                }

                try
                {
                    worker.Start();
                }
                catch (Exception)
                {
                    try
                    {
                        Stop();
                    }
                    catch
                    {
                    }
                    RaiseError("Microphone could not be opened.");
                    return;
                }
            }
            catch (Exception)
            {
                if (!cleanupStarted)
                {
                    CleanupFailedStart(handle, hPtrs, dPtrs, evt);
                }
                RaiseError("Microphone could not be opened.");
            }
        }

        public void Stop()
        {
            lock (gate)
            {
                if (tearingDown)
                {
                    return;
                }
                if (!running)
                {
                    return;
                }
                tearingDown = true;
            }

            try
            {
                StopCore();
            }
            finally
            {
                lock (gate)
                {
                    tearingDown = false;
                }
            }
        }

        private void StopCore()
        {
            IntPtr handle;
            Thread worker;
            AutoResetEvent evt;
            IntPtr[] hPtrs;
            IntPtr[] dPtrs;

            lock (gate)
            {
                if (!running)
                {
                    return;
                }
                stopping = true;
                handle = waveHandle;
                worker = workerThread;
                evt = readyEvent;
                hPtrs = headerPtrs;
                dPtrs = dataPtrs;
            }

            // Wake the worker first; it must be gone before waveInReset so it
            // cannot call waveInAddBuffer on buffers that are being returned.
            if (evt != null)
            {
                try
                {
                    evt.Set();
                }
                catch
                {
                }
            }

            bool joined = true;
            if (worker != null && Thread.CurrentThread != worker)
            {
                try
                {
                    joined = worker.Join(JoinTimeoutMs);
                }
                catch (ThreadStateException)
                {
                    // Never started, so it cannot be touching the device.
                    joined = true;
                }
                catch (Exception)
                {
                    joined = false;
                }
            }

            if (!joined)
            {
                // The worker may still own the headers. Reset/close/free could
                // pull memory out from under it, so leave the whole device leaked.
                RaiseError("Microphone meter did not stop; the audio device was left open.");
                return;
            }

            try
            {
                waveInReset(handle);
            }
            catch
            {
            }

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            if (hPtrs != null)
            {
                for (int i = 0; i < hPtrs.Length; i++)
                {
                    IntPtr dataPtr = IntPtr.Zero;
                    if (dPtrs != null && i < dPtrs.Length)
                    {
                        dataPtr = dPtrs[i];
                    }
                    ReleaseBuffer(handle, hPtrs[i], dataPtr, headerSize);
                }
            }

            CloseDevice(handle);

            if (evt != null)
            {
                try
                {
                    evt.Close();
                }
                catch
                {
                }
            }

            lock (gate)
            {
                running = false;
                waveHandle = IntPtr.Zero;
                workerThread = null;
                readyEvent = null;
                headerPtrs = null;
                dataPtrs = null;
            }
        }

        public void Dispose()
        {
            bool already;
            lock (gate)
            {
                already = disposed;
                disposed = true;
            }
            if (already)
            {
                return;
            }
            try
            {
                Stop();
            }
            catch
            {
            }
            GC.SuppressFinalize(this);
        }

        private void CleanupFailedStart(IntPtr handle, IntPtr[] hPtrs, IntPtr[] dPtrs, AutoResetEvent evt)
        {
            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            if (handle != IntPtr.Zero)
            {
                try
                {
                    waveInReset(handle);
                }
                catch
                {
                }

                if (hPtrs != null)
                {
                    for (int i = 0; i < hPtrs.Length; i++)
                    {
                        IntPtr dataPtr = IntPtr.Zero;
                        if (dPtrs != null && i < dPtrs.Length)
                        {
                            dataPtr = dPtrs[i];
                        }
                        ReleaseBuffer(handle, hPtrs[i], dataPtr, headerSize);
                    }
                }

                CloseDevice(handle);
            }

            if (evt != null)
            {
                try
                {
                    evt.Close();
                }
                catch
                {
                }
            }
        }

        private static void ReleaseBuffer(IntPtr handle, IntPtr headerPtr, IntPtr dataPtr, int headerSize)
        {
            if (headerPtr == IntPtr.Zero)
            {
                return;
            }

            int result = UnprepareWithRetry(handle, headerPtr, headerSize);
            if (!MeterTeardown.MayFreeBuffer(result))
            {
                // The driver may still reference this block; leak it rather
                // than free memory we do not own.
                return;
            }

            try
            {
                Marshal.FreeHGlobal(headerPtr);
            }
            catch
            {
            }

            if (dataPtr != IntPtr.Zero)
            {
                try
                {
                    Marshal.FreeHGlobal(dataPtr);
                }
                catch
                {
                }
            }
        }

        private static int UnprepareWithRetry(IntPtr handle, IntPtr headerPtr, int headerSize)
        {
            int result = CallUnprepareHeader(handle, headerPtr, headerSize);
            int retries = 0;
            while (MeterTeardown.ShouldRetryUnprepare(result, retries))
            {
                Thread.Sleep(MeterTeardown.RetryDelayMs);
                result = CallUnprepareHeader(handle, headerPtr, headerSize);
                retries++;
            }
            return result;
        }

        private static int CallUnprepareHeader(IntPtr handle, IntPtr headerPtr, int headerSize)
        {
            try
            {
                return waveInUnprepareHeader(handle, headerPtr, (uint)headerSize);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private void CloseDevice(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            int result = -1;
            try
            {
                result = waveInClose(handle);
            }
            catch (Exception)
            {
                result = -1;
            }

            if (result != 0)
            {
                RaiseError("Microphone could not be closed (error " + result + ").");
            }
        }

        private void WorkerLoop()
        {
            byte[] copy = new byte[BufferBytes];
            while (!stopping)
            {
                AutoResetEvent evt;
                IntPtr handle;
                IntPtr[] headers;
                lock (gate)
                {
                    evt = readyEvent;
                    handle = waveHandle;
                    headers = headerPtrs;
                }
                if (evt == null || handle == IntPtr.Zero || headers == null)
                {
                    break;
                }
                try
                {
                    evt.WaitOne(200);
                }
                catch
                {
                    break;
                }
                if (stopping)
                {
                    break;
                }
                for (int i = 0; i < headers.Length; i++)
                {
                    if (stopping)
                    {
                        break;
                    }
                    IntPtr hdrPtr = headers[i];
                    if (hdrPtr == IntPtr.Zero)
                    {
                        continue;
                    }
                    WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(hdrPtr, typeof(WAVEHDR));
                    if ((hdr.dwFlags & WhdrDone) == 0)
                    {
                        continue;
                    }
                    uint recorded = hdr.dwBytesRecorded;
                    if (recorded > 0)
                    {
                        uint toCopy = recorded;
                        if (toCopy > (uint)BufferBytes)
                        {
                            toCopy = (uint)BufferBytes;
                        }
                        try
                        {
                            Marshal.Copy(hdr.lpData, copy, 0, (int)toCopy);
                        }
                        catch
                        {
                            continue;
                        }
                        float rms = LevelMath.Rms16(copy, (int)toCopy);
                        float meter = LevelMath.ToMeter(rms);
                        int now = Environment.TickCount;
                        int last;
                        lock (gate)
                        {
                            last = lastLevelTick;
                        }
                        bool due = unchecked(now - last) >= 33;
                        if (due)
                        {
                            lock (gate)
                            {
                                lastLevelTick = now;
                            }
                            RaiseLevel(meter);
                        }
                    }
                    if (stopping)
                    {
                        break;
                    }
                    int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
                    try
                    {
                        waveInAddBuffer(handle, hdrPtr, (uint)headerSize);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private void RaiseLevel(float value)
        {
            Action<float> handler = Level;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(value);
            }
            catch
            {
            }
        }

        private void RaiseError(string message)
        {
            Action<string> handler = Error;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(message);
            }
            catch
            {
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll", SetLastError = true)]
        private static extern int waveInClose(IntPtr hwi);
    }
}

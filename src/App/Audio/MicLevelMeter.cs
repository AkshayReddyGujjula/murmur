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

        private readonly object gate = new object();
        private bool disposed;
        private bool running;
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
                if (running)
                {
                    return;
                }
            }

            AutoResetEvent evt = null;
            IntPtr handle = IntPtr.Zero;
            IntPtr[] hPtrs = null;
            IntPtr[] dPtrs = null;
            Thread worker = null;

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
                }

                for (int i = 0; i < BufferCount; i++)
                {
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

                    IntPtr hdrPtr = Marshal.AllocHGlobal(headerSize);
                    hPtrs[i] = hdrPtr;
                    Marshal.StructureToPtr(hdr, hdrPtr, false);

                    int prep = waveInPrepareHeader(handle, hdrPtr, (uint)headerSize);
                    if (prep != 0)
                    {
                        throw new InvalidOperationException("Microphone could not be opened (error " + prep + ").");
                    }

                    int add = waveInAddBuffer(handle, hdrPtr, (uint)headerSize);
                    if (add != 0)
                    {
                        try
                        {
                            waveInUnprepareHeader(handle, hdrPtr, (uint)headerSize);
                        }
                        catch
                        {
                        }
                        throw new InvalidOperationException("Microphone could not be opened (error " + add + ").");
                    }
                }

                int startResult = waveInStart(handle);
                if (startResult != 0)
                {
                    throw new InvalidOperationException("Microphone could not be opened (error " + startResult + ").");
                }

                bool abandoned = false;
                lock (gate)
                {
                    if (disposed || running)
                    {
                        abandoned = true;
                    }
                    else
                    {
                        waveHandle = handle;
                        readyEvent = evt;
                        headerPtrs = hPtrs;
                        dataPtrs = dPtrs;
                        stopping = false;
                        lastLevelTick = 0;
                        worker = new Thread(WorkerLoop);
                        worker.IsBackground = true;
                        worker.Name = "MicLevelMeter";
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
                    int headerSize2 = Marshal.SizeOf(typeof(WAVEHDR));
                    if (hPtrs != null)
                    {
                        for (int i = 0; i < hPtrs.Length; i++)
                        {
                            IntPtr hp = hPtrs[i];
                            if (hp != IntPtr.Zero)
                            {
                                if (handle != IntPtr.Zero)
                                {
                                    try
                                    {
                                        waveInUnprepareHeader(handle, hp, (uint)headerSize2);
                                    }
                                    catch
                                    {
                                    }
                                }
                                try
                                {
                                    Marshal.FreeHGlobal(hp);
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    if (dPtrs != null)
                    {
                        for (int i = 0; i < dPtrs.Length; i++)
                        {
                            if (dPtrs[i] != IntPtr.Zero)
                            {
                                try
                                {
                                    Marshal.FreeHGlobal(dPtrs[i]);
                                }
                                catch
                                {
                                }
                            }
                        }
                    }
                    if (handle != IntPtr.Zero)
                    {
                        try
                        {
                            waveInClose(handle);
                        }
                        catch
                        {
                        }
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
                if (hPtrs != null)
                {
                    int headerSize3 = Marshal.SizeOf(typeof(WAVEHDR));
                    for (int i = 0; i < hPtrs.Length; i++)
                    {
                        IntPtr hp = hPtrs[i];
                        if (hp != IntPtr.Zero)
                        {
                            if (handle != IntPtr.Zero)
                            {
                                try
                                {
                                    waveInUnprepareHeader(handle, hp, (uint)headerSize3);
                                }
                                catch
                                {
                                }
                            }
                            try
                            {
                                Marshal.FreeHGlobal(hp);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                if (dPtrs != null)
                {
                    for (int i = 0; i < dPtrs.Length; i++)
                    {
                        if (dPtrs[i] != IntPtr.Zero)
                        {
                            try
                            {
                                Marshal.FreeHGlobal(dPtrs[i]);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        waveInClose(handle);
                    }
                    catch
                    {
                    }
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
                RaiseError("Microphone could not be opened.");
            }
        }

        public void Stop()
        {
            IntPtr handle = IntPtr.Zero;
            Thread worker = null;
            AutoResetEvent evt = null;
            IntPtr[] hPtrs = null;
            IntPtr[] dPtrs = null;

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

            try
            {
                waveInReset(handle);
            }
            catch
            {
            }

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

            if (worker != null && Thread.CurrentThread != worker)
            {
                try
                {
                    worker.Join(2000);
                }
                catch
                {
                }
            }

            int headerSize = Marshal.SizeOf(typeof(WAVEHDR));
            if (hPtrs != null)
            {
                for (int i = 0; i < hPtrs.Length; i++)
                {
                    IntPtr hp = hPtrs[i];
                    if (hp == IntPtr.Zero)
                    {
                        continue;
                    }
                    try
                    {
                        waveInUnprepareHeader(handle, hp, (uint)headerSize);
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                waveInClose(handle);
            }
            catch
            {
            }

            if (hPtrs != null)
            {
                for (int i = 0; i < hPtrs.Length; i++)
                {
                    if (hPtrs[i] != IntPtr.Zero)
                    {
                        try
                        {
                            Marshal.FreeHGlobal(hPtrs[i]);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            if (dPtrs != null)
            {
                for (int i = 0; i < dPtrs.Length; i++)
                {
                    if (dPtrs[i] != IntPtr.Zero)
                    {
                        try
                        {
                            Marshal.FreeHGlobal(dPtrs[i]);
                        }
                        catch
                        {
                        }
                    }
                }
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

            lock (gate)
            {
                running = false;
                waveHandle = IntPtr.Zero;
                workerThread = null;
                readyEvent = null;
                headerPtrs = null;
                dataPtrs = null;
                stopping = false;
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

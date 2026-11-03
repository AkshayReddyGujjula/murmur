using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Murmur.App.Audio
{
    // Internal seam so the offline tests can exercise lifecycle ordering with a
    // fake driver. Production always uses WaveInMicNative.
    internal interface IMicNative
    {
        int Open(out IntPtr handle, AutoResetEvent callbackEvent);
        int PrepareHeader(IntPtr handle, IntPtr headerPtr, int headerSize);
        int AddBuffer(IntPtr handle, IntPtr headerPtr, int headerSize);
        int Start(IntPtr handle);
        int Reset(IntPtr handle);
        int UnprepareHeader(IntPtr handle, IntPtr headerPtr, int headerSize);
        int Close(IntPtr handle);
    }

    internal sealed class WaveInMicNative : IMicNative
    {
        private const uint WaveMapper = 0xFFFFFFFF;
        private const uint CallbackEvent = 0x50000;

        public int Open(out IntPtr handle, AutoResetEvent callbackEvent)
        {
            WAVEFORMATEX format = new WAVEFORMATEX();
            format.wFormatTag = 1;
            format.nChannels = 1;
            format.nSamplesPerSec = 16000;
            format.nAvgBytesPerSec = 32000;
            format.nBlockAlign = 2;
            format.wBitsPerSample = 16;
            format.cbSize = 0;

            IntPtr eventHandle = callbackEvent.SafeWaitHandle.DangerousGetHandle();
            return waveInOpen(out handle, WaveMapper, ref format, eventHandle, IntPtr.Zero, CallbackEvent);
        }

        public int PrepareHeader(IntPtr handle, IntPtr headerPtr, int headerSize)
        {
            return waveInPrepareHeader(handle, headerPtr, (uint)headerSize);
        }

        public int AddBuffer(IntPtr handle, IntPtr headerPtr, int headerSize)
        {
            return waveInAddBuffer(handle, headerPtr, (uint)headerSize);
        }

        public int Start(IntPtr handle)
        {
            return waveInStart(handle);
        }

        public int Reset(IntPtr handle)
        {
            return waveInReset(handle);
        }

        public int UnprepareHeader(IntPtr handle, IntPtr headerPtr, int headerSize)
        {
            return waveInUnprepareHeader(handle, headerPtr, (uint)headerSize);
        }

        public int Close(IntPtr handle)
        {
            return waveInClose(handle);
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct WAVEHDR
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
}

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Murmur.App.Audio;

namespace Murmur.Tests.App
{
    internal static class AudioTests
    {
        private sealed class FakeMic : IMicNative
        {
            private readonly object gate = new object();
            private readonly string label;
            private readonly List<string> events;
            private AutoResetEvent callback;

            public readonly List<int> ThreadIds = new List<int>();
            public readonly ManualResetEventSlim OpenEntered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim OpenRelease = new ManualResetEventSlim(true);
            public readonly ManualResetEventSlim CloseEntered = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim CloseRelease = new ManualResetEventSlim(true);

            public IntPtr Handle = new IntPtr(1);
            public int OpenResult;
            public int PrepareFailAt;
            public int AddBufferFailAt;
            public int StartResult;
            public int UnprepareStillPlaying;
            public bool EmitOnAdd;
            public int OpenCalls;
            public int PrepareCalls;
            public int AddCalls;
            public int StartCalls;
            public int ResetCalls;
            public int UnprepareCalls;
            public int CloseCalls;
            public int CloseResult;

            public FakeMic(string label, List<string> events)
            {
                this.label = label;
                this.events = events;
            }

            private void Record(string name)
            {
                lock (gate)
                {
                    ThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
                }
                if (events != null)
                {
                    lock (events)
                    {
                        events.Add(label + ":" + name);
                    }
                }
            }

            public int Open(out IntPtr handle, AutoResetEvent callbackEvent)
            {
                Record("Open");
                lock (gate)
                {
                    OpenCalls++;
                    callback = callbackEvent;
                }
                OpenEntered.Set();
                OpenRelease.Wait(5000);
                lock (gate)
                {
                    if (OpenResult == 0)
                    {
                        handle = Handle;
                        return 0;
                    }
                    handle = IntPtr.Zero;
                    return OpenResult;
                }
            }

            public int PrepareHeader(IntPtr handle, IntPtr headerPtr, int headerSize)
            {
                Record("Prepare");
                lock (gate)
                {
                    PrepareCalls++;
                    if (PrepareFailAt > 0 && PrepareCalls == PrepareFailAt)
                    {
                        return 9;
                    }
                    return 0;
                }
            }

            public int AddBuffer(IntPtr handle, IntPtr headerPtr, int headerSize)
            {
                Record("AddBuffer");
                int call;
                bool emit;
                lock (gate)
                {
                    AddCalls++;
                    call = AddCalls;
                    emit = EmitOnAdd;
                    if (AddBufferFailAt > 0 && call == AddBufferFailAt)
                    {
                        return 5;
                    }
                }
                if (emit)
                {
                    FillDone(headerPtr, 0x2000);
                }
                return 0;
            }

            public int Start(IntPtr handle)
            {
                Record("Start");
                lock (gate)
                {
                    StartCalls++;
                    return StartResult;
                }
            }

            public int Reset(IntPtr handle)
            {
                Record("Reset");
                lock (gate)
                {
                    ResetCalls++;
                    return 0;
                }
            }

            public int UnprepareHeader(IntPtr handle, IntPtr headerPtr, int headerSize)
            {
                Record("Unprepare");
                lock (gate)
                {
                    UnprepareCalls++;
                    if (UnprepareStillPlaying > 0)
                    {
                        UnprepareStillPlaying--;
                        return MeterTeardown.StillPlaying;
                    }
                    return 0;
                }
            }

            public int Close(IntPtr handle)
            {
                Record("Close");
                lock (gate)
                {
                    CloseCalls++;
                }
                CloseEntered.Set();
                CloseRelease.Wait(5000);
                return CloseResult;
            }

            private void FillDone(IntPtr headerPtr, short sample)
            {
                AutoResetEvent eventHandle;
                lock (gate)
                {
                    eventHandle = callback;
                }
                if (eventHandle == null || headerPtr == IntPtr.Zero)
                {
                    return;
                }

                WAVEHDR header = (WAVEHDR)Marshal.PtrToStructure(headerPtr, typeof(WAVEHDR));
                for (int i = 0; i + 1 < (int)header.dwBufferLength; i += 2)
                {
                    Marshal.WriteInt16(header.lpData, i, sample);
                }
                header.dwBytesRecorded = header.dwBufferLength;
                header.dwFlags = header.dwFlags | 0x1;
                Marshal.StructureToPtr(header, headerPtr, false);
                try
                {
                    eventHandle.Set();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private static void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }
        }

        private static bool Near(float a, float b, float tol)
        {
            return Math.Abs((double)a - (double)b) <= (double)tol;
        }

        private static void WaitUntil(Func<bool> condition, string name)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }
                Thread.Sleep(5);
            }
            throw new Exception("timed out waiting for " + name);
        }

        private static void LongUptimePublishesFirstLevel()
        {
            FakeMic fake = new FakeMic("long uptime", null);
            fake.EmitOnAdd = true;
            int levels = 0;
            using (MicLevelMeter meter = new MicLevelMeter(delegate { return fake; }, delegate { return int.MinValue + 100; }))
            {
                meter.Level += delegate { Interlocked.Increment(ref levels); };
                meter.Start();
                WaitUntil(delegate { return levels > 0; }, "negative TickCount still publishes first level");
            }
            WaitUntil(delegate { return fake.CloseCalls == 1; }, "long uptime test releases device");
        }

        private static void RepeatedShutdownDrainsCallback(bool dispose)
        {
            FakeMic fake = new FakeMic("shutdown barrier", null);
            fake.EmitOnAdd = true;
            using (ManualResetEvent entered = new ManualResetEvent(false))
            using (ManualResetEvent release = new ManualResetEvent(false))
            using (ManualResetEvent secondEntered = new ManualResetEvent(false))
            using (ManualResetEvent secondDone = new ManualResetEvent(false))
            {
                MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
                meter.Level += delegate { entered.Set(); release.WaitOne(3000); };
                Thread first = new Thread(delegate() { if (dispose) meter.Dispose(); else meter.Stop(); });
                Thread second = new Thread(delegate() {
                    secondEntered.Set();
                    if (dispose) meter.Dispose(); else meter.Stop();
                    secondDone.Set();
                });
                first.IsBackground = second.IsBackground = true;
                try
                {
                    meter.Start();
                    Check(entered.WaitOne(2000), "shutdown barrier has an in-flight callback");
                    first.Start();
                    WaitUntil(delegate { return !meter.IsRunning; }, "first shutdown sets logical state");
                    second.Start();
                    Check(secondEntered.WaitOne(2000), "second shutdown starts");
                    Check(!secondDone.WaitOne(100), "repeated shutdown also drains in-flight callback");
                }
                finally
                {
                    release.Set();
                    if ((first.ThreadState & System.Threading.ThreadState.Unstarted) == 0) first.Join(2000);
                    if ((second.ThreadState & System.Threading.ThreadState.Unstarted) == 0) second.Join(2000);
                    meter.Dispose();
                }
            }
            WaitUntil(delegate { return fake.CloseCalls == 1; }, "shutdown barrier releases device");
        }

        private static void FailedCloseCannotOpenAnotherDevice()
        {
            FakeMic fake = new FakeMic("failed close", null);
            fake.CloseResult = 33;
            fake.CloseRelease.Reset();
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Error += delegate { Interlocked.Increment(ref errors); };
            try
            {
                meter.Start();
                WaitUntil(delegate { return fake.StartCalls == 1; }, "failed-close initial capture");
                meter.Stop();
                Check(fake.CloseEntered.Wait(2000), "failed close is pending");
                meter.Start();
                fake.CloseRelease.Set();
                WaitUntil(delegate { return errors > 0 && !meter.IsRunning; }, "failed close rejects queued restart");
                meter.Start();
                Check(!meter.IsRunning, "failed-close meter stays stopped");
                Check(fake.OpenCalls == 1, "failed close never overlaps another device");
            }
            finally { meter.Dispose(); fake.CloseRelease.Set(); }
        }

        private static void RestartBurstStaysBounded()
        {
            FakeMic fake = new FakeMic("burst", null);
            fake.CloseRelease.Reset();
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            try
            {
                meter.Start();
                WaitUntil(delegate { return fake.StartCalls == 1; }, "burst initial capture");
                meter.Stop();
                Check(fake.CloseEntered.Wait(2000), "burst teardown is blocked");
                int before;
                using (Process own = Process.GetCurrentProcess()) before = own.Threads.Count;
                for (int i = 0; i < 24; i++) { meter.Start(); meter.Stop(); }
                Thread.Sleep(100);
                int after;
                using (Process own = Process.GetCurrentProcess()) after = own.Threads.Count;
                Check(after - before < 6, "restart burst must not accumulate waiting worker threads (grew " + (after - before) + ")");
                Check(fake.OpenCalls == 1, "restart burst cannot overlap the blocked device");
            }
            finally { meter.Dispose(); fake.CloseRelease.Set(); }
        }

        private static void BlockedOpenDoesNotBlockStartOrStop()
        {
            FakeMic fake = new FakeMic("open", null);
            fake.OpenRelease.Reset();
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });

            DateTime started = DateTime.UtcNow;
            meter.Start();
            double startMs = (DateTime.UtcNow - started).TotalMilliseconds;
            Check(startMs < 1000, "Start returns while the driver open is blocked (took " + startMs + " ms)");
            Check(fake.OpenEntered.Wait(2000), "worker reached the blocked open");
            Check(meter.IsRunning, "meter is running while the open is pending");

            DateTime stopping = DateTime.UtcNow;
            meter.Stop();
            double stopMs = (DateTime.UtcNow - stopping).TotalMilliseconds;
            Check(stopMs < 1000, "Stop returns while the driver open is blocked (took " + stopMs + " ms)");
            Check(!meter.IsRunning, "Stop clears running before the driver open returns");

            fake.OpenRelease.Set();
            WaitUntil(delegate { return fake.CloseCalls == 1; }, "device close after a cancelled open");
            Check(fake.PrepareCalls == 0, "cancelled open never prepares buffers");
            Check(fake.StartCalls == 0, "cancelled open never starts capture");
            meter.Dispose();
        }

        private static void CancelledOpenCanRestart()
        {
            FakeMic fake = new FakeMic("cancel", null);
            fake.OpenRelease.Reset();
            int levels = 0;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Level += delegate { Interlocked.Increment(ref levels); };
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            Check(fake.OpenEntered.Wait(2000), "worker reached the open to cancel");
            meter.Stop();
            fake.OpenRelease.Set();
            WaitUntil(delegate { return fake.CloseCalls == 1; }, "cancelled open released");
            Check(errors == 0, "cancelled open does not report a driver error");

            fake.EmitOnAdd = true;
            meter.Start();
            WaitUntil(delegate { return fake.StartCalls == 1 && levels > 0; }, "restart after a cancelled open captures");
            Check(fake.OpenCalls == 2, "restart opens a fresh session, got " + fake.OpenCalls);
            meter.Stop();
            WaitUntil(delegate { return fake.CloseCalls == 2; }, "restarted session released");
            meter.Dispose();
        }

        private static void DisposeDuringOpen()
        {
            FakeMic fake = new FakeMic("dispose-open", null);
            fake.OpenRelease.Reset();
            int levels = 0;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Level += delegate { Interlocked.Increment(ref levels); };
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            Check(fake.OpenEntered.Wait(2000), "worker reached the open to dispose");
            meter.Dispose();
            Check(!meter.IsRunning, "dispose clears running while the open is pending");
            fake.OpenRelease.Set();
            WaitUntil(delegate { return fake.CloseCalls == 1; }, "dispose closes the opened device");
            Check(fake.PrepareCalls == 0, "dispose during open never prepares buffers");
            Check(fake.StartCalls == 0, "dispose during open never starts capture");
            Thread.Sleep(60);
            Check(levels == 0 && errors == 0, "dispose during open emits no callbacks");
        }

        private static void RestartWaitsForOldTeardown()
        {
            List<string> events = new List<string>();
            FakeMic first = new FakeMic("first", events);
            first.CloseRelease.Reset();
            FakeMic second = new FakeMic("second", events);
            Queue<FakeMic> queue = new Queue<FakeMic>();
            queue.Enqueue(first);
            queue.Enqueue(second);
            MicLevelMeter meter = new MicLevelMeter(delegate { return queue.Dequeue(); });

            meter.Start();
            WaitUntil(delegate { return first.StartCalls == 1; }, "first capture starts");

            DateTime stopping = DateTime.UtcNow;
            meter.Stop();
            double stopMs = (DateTime.UtcNow - stopping).TotalMilliseconds;
            Check(stopMs < 1000, "Stop returns while the driver close is blocked (took " + stopMs + " ms)");
            Check(first.CloseEntered.Wait(2000), "worker reached the blocked close");

            meter.Start();
            Check(meter.IsRunning, "restart is accepted while the old driver is closing");
            Thread.Sleep(100);
            Check(second.OpenCalls == 0, "restart does not open while the old session still releases the driver");

            first.CloseRelease.Set();
            WaitUntil(delegate { return second.StartCalls == 1; }, "restart opens after the old close");

            int closeIndex = -1;
            int openIndex = -1;
            lock (events)
            {
                for (int i = 0; i < events.Count; i++)
                {
                    if (events[i] == "first:Close")
                    {
                        closeIndex = i;
                    }
                    if (events[i] == "second:Open")
                    {
                        openIndex = i;
                    }
                }
            }
            Check(closeIndex >= 0, "old session recorded its close");
            Check(openIndex > closeIndex, "old close completes before the new open");

            meter.Stop();
            WaitUntil(delegate { return second.CloseCalls == 1; }, "second session released");
            meter.Dispose();
        }

        private static void SingleOwnerAndNoLevelsAfterStop()
        {
            FakeMic fake = new FakeMic("owner", null);
            fake.EmitOnAdd = true;
            int levels = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Level += delegate { Interlocked.Increment(ref levels); };

            meter.Start();
            WaitUntil(delegate { return levels > 0; }, "capture emits a level");

            DateTime stopping = DateTime.UtcNow;
            meter.Stop();
            double stopMs = (DateTime.UtcNow - stopping).TotalMilliseconds;
            Check(stopMs < 1000, "Stop returns promptly during capture (took " + stopMs + " ms)");
            int afterStop = levels;
            Thread.Sleep(150);
            Check(levels == afterStop, "no level events after Stop returned (got " + levels + " after " + afterStop + ")");
            Check(!meter.IsRunning, "meter is stopped");

            WaitUntil(delegate { return fake.CloseCalls == 1; }, "meter device closes after stop");
            Check(fake.ThreadIds.Count > 0, "fake driver saw native calls");
            int owner = fake.ThreadIds[0];
            for (int i = 0; i < fake.ThreadIds.Count; i++)
            {
                Check(fake.ThreadIds[i] == owner, "all native calls use one owner thread");
            }
            Check(owner != Thread.CurrentThread.ManagedThreadId, "native calls never use the caller thread");
            meter.Dispose();
        }

        private static void NoCallbacksAfterDispose()
        {
            FakeMic fake = new FakeMic("dispose", null);
            fake.EmitOnAdd = true;
            int levels = 0;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Level += delegate { Interlocked.Increment(ref levels); };
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            WaitUntil(delegate { return levels > 0; }, "capture emits a level before dispose");
            meter.Dispose();
            int afterDispose = levels;
            Thread.Sleep(150);
            Check(levels == afterDispose, "no level events after Dispose returned");
            Check(errors == 0, "dispose during capture emits no error");
            WaitUntil(delegate { return fake.CloseCalls == 1; }, "dispose releases the device");
        }

        private static void OpenFaultReportsOnce()
        {
            FakeMic fake = new FakeMic("open-fault", null);
            fake.OpenResult = 100;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            WaitUntil(delegate { return errors == 1 && !meter.IsRunning; }, "open failure is reported");
            Thread.Sleep(60);
            Check(errors == 1, "open failure reports exactly one error");
            Check(fake.PrepareCalls == 0, "failed open never prepares buffers");
            Check(fake.StartCalls == 0, "failed open never starts capture");
            Check(fake.CloseCalls == 0, "failed open without a handle does not close");
            meter.Dispose();
        }

        private static void PrepareFaultCleansUp()
        {
            FakeMic fake = new FakeMic("prepare-fault", null);
            fake.PrepareFailAt = 2;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            WaitUntil(delegate { return errors == 1 && fake.CloseCalls == 1 && !meter.IsRunning; }, "prepare failure cleanup");
            Check(fake.PrepareCalls == 2, "prepare failure stops at the failed header");
            Check(fake.AddCalls == 1, "only the prepared header was queued");
            Check(fake.UnprepareCalls == 1, "only actually prepared headers are unprepared");
            Check(fake.ResetCalls == 1, "prepare failure still resets the device");
            Check(fake.StartCalls == 0, "prepare failure never starts capture");
            Thread.Sleep(60);
            Check(errors == 1, "prepare failure reports exactly one error");
            meter.Dispose();
        }

        private static void StartFaultCleansUp()
        {
            FakeMic fake = new FakeMic("start-fault", null);
            fake.StartResult = 7;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            WaitUntil(delegate { return errors == 1 && fake.CloseCalls == 1 && !meter.IsRunning; }, "start failure cleanup");
            Check(fake.PrepareCalls == 4, "start failure prepared all buffers");
            Check(fake.AddCalls == 4, "start failure queued all buffers");
            Check(fake.UnprepareCalls == 4, "start failure unprepares all prepared buffers");
            Check(fake.ResetCalls == 1, "start failure resets the device");
            Thread.Sleep(60);
            Check(errors == 1, "start failure reports exactly one error");
            meter.Dispose();
        }

        private static void RearmFaultReportsOnceAndCleansUp()
        {
            FakeMic fake = new FakeMic("rearm-fault", null);
            fake.EmitOnAdd = true;
            fake.AddBufferFailAt = 5;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return fake; });
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            WaitUntil(delegate { return errors == 1 && fake.CloseCalls == 1 && !meter.IsRunning; }, "rearm failure cleanup");
            Check(fake.AddCalls == 5, "rearm failure stops after the first failed add, got " + fake.AddCalls);
            Check(fake.ResetCalls == 1, "rearm failure resets the device");
            Check(fake.UnprepareCalls == 4, "rearm failure unprepares every prepared buffer");
            Thread.Sleep(60);
            Check(errors == 1, "rearm failure reports exactly one error");
            meter.Dispose();
        }

        private static void StartAfterWorkerExit()
        {
            List<string> events = new List<string>();
            FakeMic failed = new FakeMic("failed", events);
            failed.OpenResult = 100;
            FakeMic good = new FakeMic("good", events);
            good.EmitOnAdd = true;
            Queue<FakeMic> queue = new Queue<FakeMic>();
            queue.Enqueue(failed);
            queue.Enqueue(good);
            int levels = 0;
            int errors = 0;
            MicLevelMeter meter = new MicLevelMeter(delegate { return queue.Dequeue(); });
            meter.Level += delegate { Interlocked.Increment(ref levels); };
            meter.Error += delegate { Interlocked.Increment(ref errors); };

            meter.Start();
            WaitUntil(delegate { return errors == 1 && !meter.IsRunning; }, "failed session ends");
            meter.Start();
            WaitUntil(delegate { return good.StartCalls == 1 && levels > 0; }, "start after a worker exit captures");
            Check(failed.CloseCalls == 0, "failed session had no device to close");
            meter.Stop();
            WaitUntil(delegate { return good.CloseCalls == 1; }, "post-failure session released");
            meter.Dispose();
        }

        public static int Main()
        {
            try
            {
                byte[] silence = new byte[1024];
                float silenceRms = LevelMath.Rms16(silence, silence.Length);
                Check(silenceRms == 0f, "silence rms is 0, got " + silenceRms);
                Check(LevelMath.ToMeter(silenceRms) == 0f, "silence meter is 0");

                byte[] full = new byte[1024];
                for (int i = 0; i < full.Length; i += 2)
                {
                    full[i] = 0xFF;
                    full[i + 1] = 0x7F;
                }
                float fullRms = LevelMath.Rms16(full, full.Length);
                Check(fullRms > 0.99f && fullRms <= 1f, "full-scale square rms near 1, got " + fullRms);
                float fullMeter = LevelMath.ToMeter(fullRms);
                Check(fullMeter > 0.99f && fullMeter <= 1f, "full-scale square meter near 1, got " + fullMeter);

                byte[] negFull = new byte[1024];
                for (int i = 0; i < negFull.Length; i += 2)
                {
                    negFull[i] = 0x00;
                    negFull[i + 1] = 0x80;
                }
                float negRms = LevelMath.Rms16(negFull, negFull.Length);
                Check(negRms > 0.99f && negRms <= 1f, "negative full-scale rms near 1, got " + negRms);

                byte[] sineBytes = new byte[320];
                for (int n = 0; n < 160; n++)
                {
                    double angle = 2.0 * Math.PI * (double)n / 16.0;
                    short s = (short)(4634.0 * Math.Sin(angle));
                    sineBytes[2 * n] = (byte)(s & 0xFF);
                    sineBytes[(2 * n) + 1] = (byte)((s >> 8) & 0xFF);
                }
                float sineRms = LevelMath.Rms16(sineBytes, sineBytes.Length);
                Check(Near(sineRms, 0.1f, 0.015f), "-20 dBFS sine rms near 0.1, got " + sineRms);
                float sineMeter = LevelMath.ToMeter(sineRms);
                Check(Near(sineMeter, 0.6667f, 0.03f), "-20 dBFS sine meter near 0.667, got " + sineMeter);

                byte[] odd = new byte[5];
                odd[0] = 0x00;
                odd[1] = 0x00;
                odd[2] = 0xFF;
                odd[3] = 0x7F;
                odd[4] = 0xAA;
                float oddFive = LevelMath.Rms16(odd, 5);
                float oddFour = LevelMath.Rms16(odd, 4);
                Check(oddFive == oddFour, "odd byte count ignores trailing byte");
                Check(LevelMath.Rms16(odd, 1) == 0f, "single trailing byte yields 0");
                Check(LevelMath.Rms16(odd, 0) == 0f, "zero count yields 0");
                Check(LevelMath.Rms16(odd, -3) == 0f, "negative count yields 0");

                byte[] small = new byte[2];
                small[0] = 0xFF;
                small[1] = 0x7F;
                float clamped = LevelMath.Rms16(small, 100);
                float exact = LevelMath.Rms16(small, 2);
                Check(clamped == exact, "byteCount larger than array is clamped");

                Check(LevelMath.Rms16(null, 0) == 0f, "null input yields 0");
                Check(LevelMath.Rms16(null, 10) == 0f, "null input with count yields 0");
                Check(LevelMath.Rms16(new byte[0], 0) == 0f, "empty input yields 0");
                Check(LevelMath.Rms16(new byte[10], 0) == 0f, "zero count on empty yields 0");

                Check(LevelMath.ToMeter(0f) == 0f, "meter of 0 is 0");
                Check(LevelMath.ToMeter(-0.5f) == 0f, "meter of negative is 0");
                Check(LevelMath.ToMeter(float.NaN) == 0f, "meter of NaN is 0");
                Check(LevelMath.ToMeter(1f) == 1f, "meter of 1 is 1");
                Check(LevelMath.ToMeter(2f) == 1f, "meter above 1 is clamped to 1");
                Check(Near(LevelMath.ToMeter(0.001f), 0f, 0.002f), "meter at -60 dBFS is 0");
                float half = LevelMath.ToMeter(0.5f);
                Check(half > 0.8f && half < 1f, "meter of 0.5 in range, got " + half);

                Check(MeterTeardown.MaxUnprepareRetries == 10, "unprepare retries are bounded at 10");
                Check(MeterTeardown.RetryDelayMs == 10, "unprepare retries sleep 10 ms");
                Check(MeterTeardown.ClassifyUnprepare(0) == UnprepareOutcome.Free, "successful unprepare is freed");
                Check(MeterTeardown.ClassifyUnprepare(MeterTeardown.StillPlaying) == UnprepareOutcome.Retry, "WAVERR_STILLPLAYING retries unprepare");
                Check(MeterTeardown.ClassifyUnprepare(34) == UnprepareOutcome.Leak, "other unprepare errors leak");
                Check(MeterTeardown.MayFreeBuffer(0), "header and data free only after successful unprepare");
                Check(!MeterTeardown.MayFreeBuffer(MeterTeardown.StillPlaying), "still-playing header and data are leaked");
                Check(!MeterTeardown.MayFreeBuffer(34), "failed unprepare leaks header and data");
                Check(!MeterTeardown.MayFreeBuffer(-1), "unknown unprepare result leaks header and data");
                Check(MeterTeardown.ShouldRetryUnprepare(0, 0) == false, "successful unprepare is not retried");
                Check(MeterTeardown.ShouldRetryUnprepare(34, 0) == false, "other unprepare errors are not retried");
                Check(MeterTeardown.ShouldRetryUnprepare(MeterTeardown.StillPlaying, 0), "still-playing unprepare is retried");
                Check(MeterTeardown.ShouldRetryUnprepare(MeterTeardown.StillPlaying, MeterTeardown.MaxUnprepareRetries - 1), "last bounded retry runs");
                Check(MeterTeardown.ShouldRetryUnprepare(MeterTeardown.StillPlaying, MeterTeardown.MaxUnprepareRetries) == false, "retries stop after 10");

                MicLevelMeter meter = new MicLevelMeter();
                Check(!meter.IsRunning, "fresh meter is not running");
                meter.Stop();
                Check(!meter.IsRunning, "stopping a never-started meter is safe");
                meter.Stop();
                meter.Dispose();
                Check(!meter.IsRunning, "disposed meter is not running");
                meter.Dispose();
                meter.Stop();
                Check(!meter.IsRunning, "stop after dispose is safe");

                MicLevelMeter meter2 = new MicLevelMeter();
                meter2.Dispose();
                meter2.Dispose();
                Check(!meter2.IsRunning, "double dispose is safe");

                RestartBurstStaysBounded();
                FailedCloseCannotOpenAnotherDevice();
                LongUptimePublishesFirstLevel();
                RepeatedShutdownDrainsCallback(false);
                RepeatedShutdownDrainsCallback(true);
                BlockedOpenDoesNotBlockStartOrStop();
                CancelledOpenCanRestart();
                DisposeDuringOpen();
                RestartWaitsForOldTeardown();
                SingleOwnerAndNoLevelsAfterStop();
                NoCallbacksAfterDispose();
                OpenFaultReportsOnce();
                PrepareFaultCleansUp();
                StartFaultCleansUp();
                RearmFaultReportsOnceAndCleansUp();
                StartAfterWorkerExit();

                Console.WriteLine("PASS: LevelMath silence, full-scale, -20dBFS sine, odd and empty input; teardown free-only-on-success rules; MicLevelMeter non-blocking worker lifecycle, cancellation, restart ordering, single native owner, driver faults and callback suppression.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error.Message);
                return 1;
            }
        }
    }
}

using System;
using Murmur.App.Audio;

namespace Murmur.Tests.App
{
    internal static class AudioTests
    {
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

                Console.WriteLine("PASS: LevelMath silence, full-scale, -20dBFS sine, odd and empty input; teardown free-only-on-success rules; MicLevelMeter never-started stop/dispose safe.");
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

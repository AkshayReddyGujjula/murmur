using System;

namespace Murmur.App.Audio
{
    public static class LevelMath
    {
        public static float Rms16(byte[] pcm, int byteCount)
        {
            if (pcm == null)
            {
                return 0f;
            }
            if (byteCount <= 0)
            {
                return 0f;
            }
            if (byteCount > pcm.Length)
            {
                byteCount = pcm.Length;
            }
            byteCount = byteCount & ~1;
            if (byteCount <= 0)
            {
                return 0f;
            }
            int samples = byteCount / 2;
            double sum = 0.0;
            for (int i = 0; i < byteCount; i += 2)
            {
                int lo = (int)pcm[i];
                int hi = (int)pcm[i + 1];
                short s = (short)(lo | (hi << 8));
                double v = (double)s / 32768.0;
                sum += v * v;
            }
            double rms = Math.Sqrt(sum / (double)samples);
            if (rms > 1.0)
            {
                rms = 1.0;
            }
            if (rms < 0.0)
            {
                rms = 0.0;
            }
            return (float)rms;
        }

        public static float ToMeter(float rms)
        {
            if (float.IsNaN(rms))
            {
                return 0f;
            }
            if (rms <= 0f)
            {
                return 0f;
            }
            if (rms >= 1f)
            {
                return 1f;
            }
            if (rms <= 0.001f)
            {
                return 0f;
            }
            double db = 20.0 * Math.Log10((double)rms);
            if (db <= -60.0)
            {
                return 0f;
            }
            if (db >= 0.0)
            {
                return 1f;
            }
            return (float)((db + 60.0) / 60.0);
        }
    }
}

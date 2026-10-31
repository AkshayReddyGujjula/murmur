using System;

namespace Murmur.App.Ui
{
    public sealed class WaveHistory
    {
        private const float RiseRate = 0.65f;
        private const float FallRate = 0.22f;

        private readonly float[] values;
        private float latest;

        public WaveHistory(int bars)
        {
            int count = bars;
            if (count < 1) count = 1;
            if (count > 256) count = 256;
            values = new float[count];
            latest = 0f;
        }

        public void Push(float level)
        {
            float target = level;
            if (Single.IsNaN(target) || target < 0f) target = 0f;
            if (target > 1f) target = 1f;

            float smoothed;
            if (target >= latest) smoothed = latest + (target - latest) * RiseRate;
            else smoothed = latest + (target - latest) * FallRate;
            if (smoothed < 0f) smoothed = 0f;
            if (smoothed > 1f) smoothed = 1f;
            latest = smoothed;

            if (values.Length > 1) Array.Copy(values, 1, values, 0, values.Length - 1);
            values[values.Length - 1] = smoothed;
        }

        public float[] Snapshot()
        {
            return (float[])values.Clone();
        }
    }
}

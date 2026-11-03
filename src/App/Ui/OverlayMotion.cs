using System;
using System.Drawing;

namespace Murmur.App.Ui
{
    internal static class OverlayMotion
    {
        internal const int Bars = 27;
        internal const float RecordingWidth = 132f;
        internal const float Height = 36f * 1.05f * 1.10f;

        // Damped spring: starts with zero velocity and has only ~1% overshoot.
        internal static float Spring(double seconds)
        {
            if (seconds <= 0) return 0f;
            if (seconds >= 0.6) return 1f;
            return (float)(1 - Math.Exp(-16.4 * seconds) *
                (Math.Cos(11.45 * seconds) + 16.4 / 11.45 * Math.Sin(11.45 * seconds)));
        }

        internal static float Ease(double value)
        {
            float t = (float)Math.Max(0, Math.Min(1, value));
            return t * t * (3f - 2f * t);
        }

        internal static RectangleF Bounds(int width, int height, SizeF from, SizeF to, double seconds, bool animate)
        {
            float p = animate ? Spring(seconds) : 1f;
            float w = from.Width + (to.Width - from.Width) * p;
            float h = from.Height + (to.Height - from.Height) * p;
            w = Math.Max(1f, Math.Min(width, w));
            h = Math.Max(1f, Math.Min(height, h));
            return new RectangleF((width - w) / 2f, (height - h) / 2f, w, h);
        }

        // Preserve the waveform's span, with 28% thicker strokes than the prior
        // 48-bar / 47-gap layout. Fewer bars leave clear space between strokes.
        internal static float BarWidth(float span) { return span / 95f * 1.28f; }

        internal static float VisualEnergy(float level)
        {
            if (Single.IsNaN(level) || level <= 0.08f) return 0f;
            float normalised = Math.Min(1f, (level - 0.08f) / 0.92f);
            return Math.Min(1f, 0.975f * 1.35f * (float)Math.Pow(normalised, 1.5));
        }

        internal static float BarHeight(float height, float energy, float contour)
        {
            float scale = height / Height;
            float minimum = 2f * scale;
            float maximum = height - 4f * scale; // 2 logical pixels above and below
            return minimum + (maximum - minimum) * Math.Max(0f, Math.Min(1f, energy * contour));
        }

        internal static float EntranceOpacity(bool wasHidden, double seconds, bool animate)
        { return animate && wasHidden ? Ease(seconds / 0.10) : 1f; }
    }

    internal sealed class WaveMotion
    {
        internal readonly float[] Values = new float[OverlayMotion.Bars];
        private readonly float[] velocity = new float[OverlayMotion.Bars];

        internal void Clear() { Array.Clear(Values, 0, Values.Length); Array.Clear(velocity, 0, velocity.Length); }

        internal void Step(float[] target, double elapsed)
        {
            // Small integration steps keep a delayed UI frame stable.
            double remaining = Math.Min(0.08, Math.Max(0, elapsed));
            while (remaining > 0)
            {
                float dt = (float)Math.Min(0.008, remaining);
                for (int i = 0; i < Values.Length; i++)
                {
                    float goal = target[i];
                    velocity[i] += ((goal - Values[i]) * 900f - velocity[i] * 46f) * dt;
                    Values[i] = Math.Max(0f, Math.Min(1f, Values[i] + velocity[i] * dt));
                }
                remaining -= dt;
            }
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Murmur.App.Ui;

namespace Murmur.Tests
{
    // Offline native renderer capture. Simulated levels; no window/audio/provider.
    internal static class OverlayCapture
    {
        private static int Main(string[] args)
        {
            Directory.CreateDirectory(args[0]);
            WaveHistory history = new WaveHistory(OverlayMotion.Bars);
            WaveMotion wave = new WaveMotion();
            float[] target = new float[OverlayMotion.Bars];
            double next = 0;
            int pillHeight = (int)Math.Round(OverlayMotion.Height) * 2;
            int canvasHeight = pillHeight + 8;
            for (int frame = 0; frame < 250; frame++)
            {
                double t = frame / 60.0;
                OverlayMode mode = t < 0.55 ? OverlayMode.Starting : t < 2.20 ? OverlayMode.Recording : t < 3.0 ? OverlayMode.Working : OverlayMode.Done;
                double start = mode == OverlayMode.Starting ? 0 : mode == OverlayMode.Recording ? 0.55 : mode == OverlayMode.Working ? 2.20 : 3.0;
                OverlayMode previous = mode == OverlayMode.Starting ? OverlayMode.Hidden : mode == OverlayMode.Recording ? OverlayMode.Starting : mode == OverlayMode.Working ? OverlayMode.Recording : OverlayMode.Working;
                float from = mode == OverlayMode.Starting ? 110 * 0.82f : mode == OverlayMode.Working ? 132 : 110;
                float to = mode == OverlayMode.Recording ? 132 : mode == OverlayMode.Done ? (float)Math.Round(OverlayMotion.Height) : 110;
                double seconds = t - start;
                if (mode == OverlayMode.Recording)
                {
                    if (t >= next)
                    {
                        float level = (float)Math.Max(0, Math.Min(1, 0.38 + 0.32 * Math.Sin(t * 11) + 0.17 * Math.Sin(t * 24)));
                        history.Push(level); history.CopyTo(target); next = t + 1.0 / 45;
                    }
                    wave.Step(target, 1.0 / 60);
                }
                using (Bitmap image = new Bitmap(272, canvasHeight, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(image))
                {
                    g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    RectangleF pill = OverlayMotion.Bounds(272, canvasHeight, new SizeF(from * 2, mode == OverlayMode.Starting ? pillHeight * 0.92f : pillHeight), new SizeF(to * 2, pillHeight), seconds, true);
                    float opacity = OverlayMotion.EntranceOpacity(previous == OverlayMode.Hidden, seconds, true);
                    if (mode == OverlayMode.Done) opacity *= OverlayMotion.Ease((1.1 - seconds) / 0.18);
                    if (mode == OverlayMode.Done)
                    {
                        float shrink = 0.94f + 0.06f * OverlayMotion.Ease((1.1 - seconds) / 0.18);
                        pill.Inflate(-pill.Width * (1 - shrink) / 2, -pill.Height * (1 - shrink) / 2);
                    }
                    OverlayVisual.Pill(g, pill, opacity);
                    float blend = OverlayMotion.Ease(seconds / 0.16);
                    OverlayVisual.Content(g, pill, previous, false, "", wave.Values, 0.6, opacity * (1 - blend), true);
                    OverlayVisual.Content(g, pill, mode, mode == OverlayMode.Done, "", wave.Values, seconds, opacity * blend, true);
                    image.Save(Path.Combine(args[0], "frame-" + frame.ToString("D3") + ".png"), ImageFormat.Png);
                    if (frame == 122) image.Save(Path.Combine(args[0], "listening.png"), ImageFormat.Png);
                    if (frame == 207) image.Save(Path.Combine(args[0], "copied.png"), ImageFormat.Png);
                }
            }
            using (Bitmap image = new Bitmap(272, canvasHeight, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(image))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float[] loud = new float[OverlayMotion.Bars];
                for (int i = 0; i < loud.Length; i++) loud[i] = 1f;
                RectangleF pill = new RectangleF(4, 4, 264, pillHeight);
                OverlayVisual.Pill(g, pill, 1);
                OverlayVisual.Content(g, pill, OverlayMode.Recording, false, "", loud, 0.6, 1, true);
                image.Save(Path.Combine(args[0], "peak.png"), ImageFormat.Png);
            }
            using (Bitmap image = new Bitmap(canvasHeight, canvasHeight, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(image))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                RectangleF circle = new RectangleF(4, 4, pillHeight, pillHeight);
                OverlayVisual.Pill(g, circle, 1);
                OverlayVisual.Content(g, circle, OverlayMode.Error, true, "", wave.Values, 0.6, 1, true);
                image.Save(Path.Combine(args[0], "failed.png"), ImageFormat.Png);
            }
            Console.WriteLine("Captured 250 native frames; simulated audio, no visible window.");
            return 0;
        }
    }
}

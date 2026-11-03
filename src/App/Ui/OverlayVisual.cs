using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Murmur.App.Ui
{
    // Shared by the real layered window and the offline frame capture.
    internal static class OverlayVisual
    {
        internal static void Pill(Graphics g, RectangleF bounds, float opacity)
        {
            using (GraphicsPath path = Logo.Rounded(bounds, bounds.Height / 2f))
            using (SolidBrush black = new SolidBrush(Colour(Color.Black, opacity))) g.FillPath(black, path);
            RectangleF border = bounds; border.Inflate(-0.5f, -0.5f);
            using (GraphicsPath path = Logo.Rounded(border, border.Height / 2f))
            using (Pen pen = new Pen(Colour(Color.FromArgb(42, 42, 42), opacity), 1f)) g.DrawPath(pen, path);
        }

        internal static void Content(Graphics g, RectangleF bounds, OverlayMode mode, bool compact,
            string caption, float[] bars, double seconds, float opacity, bool animate)
        {
            if (opacity <= 0f) return;
            GraphicsState saved = g.Save();
            try
            {
                using (GraphicsPath clip = Logo.Rounded(bounds, bounds.Height / 2f)) g.SetClip(clip, CombineMode.Intersect);
                g.TranslateTransform(bounds.X, bounds.Y);
                float width = bounds.Width, height = bounds.Height;
                float scale = height / OverlayMotion.Height;
                float cy = height / 2f;
                if (compact)
                {
                    Result(g, width, height, mode == OverlayMode.Done, animate ? OverlayMotion.Ease((seconds - 0.10) / 0.22) : 1f, opacity);
                    return;
                }
                if (mode == OverlayMode.Recording)
                {
                    float pulse = animate ? 1f + 0.08f * (float)Math.Sin(seconds * 4) : 1f;
                    float radius = 2.7f * scale * pulse;
                    using (SolidBrush red = new SolidBrush(Colour(Color.FromArgb(255, 69, 58), opacity)))
                        g.FillEllipse(red, 16f * scale - radius, cy - radius, radius * 2f, radius * 2f);
                    float left = 26f * scale, span = width - left - 10f * scale;
                    if (span <= 0) return;
                    float bar = Math.Max(1f, OverlayMotion.BarWidth(span));
                    float step = (span - bar) / (OverlayMotion.Bars - 1);
                    using (SolidBrush white = new SolidBrush(Colour(Color.White, opacity)))
                    using (GraphicsPath path = new GraphicsPath())
                    for (int i = 0; i < OverlayMotion.Bars; i++)
                    {
                        float energy = OverlayMotion.VisualEnergy(bars[i]);
                        // Fine contour keeps adjacent RMS samples individually legible;
                        // its movement is weighted by real microphone energy.
                        float contour = 0.60f + 0.40f * (float)(0.5 + 0.5 * Math.Sin(i * 2.399 + (animate ? seconds * 1.4 : 0)));
                        float barHeight = OverlayMotion.BarHeight(height, energy, contour);
                        // Snap thin strokes to physical pixels at 100%/125% DPI.
                        // Fractional stroke width preserves the requested 28% increase.
                        float x = (float)Math.Round(left + i * step);
                        path.Reset();
                        path.AddArc(x, cy - barHeight / 2f, bar, bar, 180f, 180f);
                        path.AddArc(x, cy + barHeight / 2f - bar, bar, bar, 0f, 180f);
                        path.CloseFigure();
                        g.FillPath(white, path);
                    }
                    return;
                }
                if (mode == OverlayMode.Starting || mode == OverlayMode.Working)
                {
                    // Three tiny dots, a continuous staggered wave, no wraparound jump.
                    for (int i = 0; i < 3; i++)
                    {
                        float beat = animate ? (float)(0.5 + 0.5 * Math.Sin(seconds * Math.PI * 2 / 1.15 - i * 0.7)) : 0.7f;
                        beat = beat * beat;
                        float radius = (1.65f + 0.45f * beat) * scale;
                        float y = cy - (animate ? 2.2f * beat * scale : 0f);
                        using (SolidBrush white = new SolidBrush(Colour(Color.White, opacity * (0.35f + 0.65f * beat))))
                            g.FillEllipse(white, width / 2f + (i - 1) * 9f * scale - radius, y - radius, radius * 2f, radius * 2f);
                    }
                    return;
                }
                // Explicit launch hints and actionable setup errors retain their copy.
                using (Font font = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point))
                using (StringFormat format = new StringFormat())
                using (SolidBrush brush = new SolidBrush(Colour(mode == OverlayMode.Error ? Color.FromArgb(255, 101, 90) : Color.White, opacity)))
                {
                    format.Alignment = StringAlignment.Center; format.LineAlignment = StringAlignment.Center;
                    format.Trimming = StringTrimming.EllipsisCharacter; format.FormatFlags = StringFormatFlags.NoWrap;
                    g.DrawString(caption, font, brush, new RectangleF(12f * scale, 0, Math.Max(0, width - 24f * scale), height), format);
                }
            }
            finally { g.Restore(saved); }
        }

        private static void Result(Graphics g, float width, float height, bool success, float progress, float opacity)
        {
            float unit = height / OverlayMotion.Height;
            float cx = width / 2f, cy = height / 2f;
            using (Pen pen = new Pen(Colour(success ? Color.FromArgb(68, 215, 138) : Color.FromArgb(255, 101, 90), opacity), 2f * unit))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                if (success)
                {
                    PointF a = new PointF(cx - 6f * unit, cy), b = new PointF(cx - 1.5f * unit, cy + 4.5f * unit), c = new PointF(cx + 7f * unit, cy - 5f * unit);
                    Stroke(g, pen, a, b, Math.Min(1f, progress / 0.35f));
                    if (progress > 0.35f) Stroke(g, pen, b, c, (progress - 0.35f) / 0.65f);
                }
                else
                {
                    Stroke(g, pen, new PointF(cx - 5f * unit, cy - 5f * unit), new PointF(cx + 5f * unit, cy + 5f * unit), progress);
                    Stroke(g, pen, new PointF(cx + 5f * unit, cy - 5f * unit), new PointF(cx - 5f * unit, cy + 5f * unit), progress);
                }
            }
        }

        private static void Stroke(Graphics g, Pen pen, PointF a, PointF b, float progress)
        {
            if (progress > 0f) g.DrawLine(pen, a, new PointF(a.X + (b.X - a.X) * progress, a.Y + (b.Y - a.Y) * progress));
        }

        internal static Color Colour(Color colour, float opacity)
        { return Color.FromArgb((int)Math.Round(Math.Max(0f, Math.Min(1f, opacity)) * 255), colour); }
    }
}

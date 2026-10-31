using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Murmur.App.Ui
{
    public static class Logo
    {
        private static readonly Color BorderColour = Color.FromArgb(42, 42, 42);
        private static readonly Color RecordingColour = Color.FromArgb(255, 69, 58);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static void Draw(Graphics g, RectangleF bounds, bool recording)
        {
            if (g == null || bounds.Width <= 0f || bounds.Height <= 0f) return;
            float size = Math.Min(bounds.Width, bounds.Height);
            if (size <= 0f) return;

            RectangleF square = new RectangleF(
                bounds.X + (bounds.Width - size) / 2f,
                bounds.Y + (bounds.Height - size) / 2f,
                size,
                size);

            SmoothingMode smoothing = g.SmoothingMode;
            PixelOffsetMode pixelOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            try
            {
                using (GraphicsPath shape = Rounded(square, size * 0.24f))
                using (SolidBrush fill = new SolidBrush(Color.Black))
                {
                    g.FillPath(fill, shape);
                }

                float inset = Math.Max(0.5f, size / 256f);
                RectangleF borderBounds = new RectangleF(
                    square.X + inset,
                    square.Y + inset,
                    square.Width - inset * 2f,
                    square.Height - inset * 2f);
                using (GraphicsPath border = Rounded(borderBounds, Math.Max(0f, size * 0.24f - inset)))
                using (Pen pen = new Pen(BorderColour, Math.Max(1f, size / 128f)))
                {
                    g.DrawPath(pen, border);
                }

                float barWidth = size * 0.085f;
                float gap = size * 0.055f;
                float span = barWidth * 5f + gap * 4f;
                float[] heights = new float[] { 0.38f, 0.66f, 1f, 0.66f, 0.38f };
                float centreX = square.X + square.Width / 2f;
                float centreY = square.Y + square.Height / 2f;
                float maxHeight = size * 0.56f;

                using (SolidBrush white = new SolidBrush(Color.White))
                {
                    for (int index = 0; index < 5; index++)
                    {
                        float barHeight = maxHeight * heights[index];
                        if (barHeight < barWidth) barHeight = barWidth;
                        float x = centreX - span / 2f + index * (barWidth + gap);
                        RectangleF bar = new RectangleF(x, centreY - barHeight / 2f, barWidth, barHeight);
                        using (GraphicsPath path = Rounded(bar, barWidth / 2f))
                        {
                            g.FillPath(white, path);
                        }
                    }
                }

                if (recording)
                {
                    float dot = size * 0.17f;
                    float margin = size * 0.13f;
                    using (SolidBrush red = new SolidBrush(RecordingColour))
                    {
                        g.FillEllipse(red, square.Right - margin - dot, square.Top + margin, dot, dot);
                    }
                }
            }
            finally
            {
                g.SmoothingMode = smoothing;
                g.PixelOffsetMode = pixelOffset;
            }
        }

        public static Icon CreateIcon(int size, bool recording)
        {
            int pixels = Math.Max(8, Math.Min(256, size));
            using (Bitmap bitmap = RenderBitmap(pixels, recording))
            {
                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon source = Icon.FromHandle(handle))
                    {
                        return (Icon)source.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        public static void SaveIco(string path)
        {
            if (path == null) throw new ArgumentNullException("path");
            string folder = Path.GetDirectoryName(path);
            if (!String.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            int[] sizes = new int[] { 16, 24, 32, 48, 64, 256 };
            byte[][] images = new byte[sizes.Length][];
            for (int index = 0; index < sizes.Length; index++)
            {
                using (Bitmap bitmap = RenderBitmap(sizes[index], false))
                using (MemoryStream stream = new MemoryStream())
                {
                    bitmap.Save(stream, ImageFormat.Png);
                    images[index] = stream.ToArray();
                }
            }

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)sizes.Length);

                int offset = 6 + 16 * sizes.Length;
                for (int index = 0; index < sizes.Length; index++)
                {
                    byte dimension = sizes[index] >= 256 ? (byte)0 : (byte)sizes[index];
                    writer.Write(dimension);
                    writer.Write(dimension);
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)images[index].Length);
                    writer.Write((uint)offset);
                    offset += images[index].Length;
                }

                for (int index = 0; index < images.Length; index++) writer.Write(images[index]);
            }
        }

        internal static GraphicsPath Rounded(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width <= 0f || bounds.Height <= 0f) return path;

            float limit = Math.Min(bounds.Width, bounds.Height) / 2f;
            if (radius > limit) radius = limit;
            if (radius <= 0.01f)
            {
                path.AddRectangle(bounds);
                return path;
            }

            float diameter = radius * 2f;
            path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270f, 90f);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0f, 90f);
            path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        private static Bitmap RenderBitmap(int size, bool recording)
        {
            Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    Configure(graphics);
                    Draw(graphics, new RectangleF(0f, 0f, size, size), recording);
                }
                return bitmap;
            }
            catch (Exception)
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static void Configure(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Murmur.App.Ui
{
    internal sealed class OverlayPresentException : Exception
    {
        internal OverlayPresentException(string message) : base(message)
        {
        }
    }

    internal static class OverlayPixels
    {
        // StretchBlt-free conversion used by UpdateLayeredWindow: the DIB is bottom-up and
        // every channel must be premultiplied by alpha. Keep this byte-for-byte equivalent to
        // the old CopyPremultiplied implementation (including the +127 rounding).
        internal static void PackPremultipliedBottomUp(
            byte[] source,
            int sourceStride,
            int width,
            int height,
            byte[] destination)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (destination == null) throw new ArgumentNullException("destination");
            if (width <= 0) throw new ArgumentOutOfRangeException("width");
            if (height <= 0) throw new ArgumentOutOfRangeException("height");

            int rowBytes = width * 4;
            if (sourceStride < rowBytes) throw new ArgumentOutOfRangeException("sourceStride");
            if (destination.Length < rowBytes * height) throw new ArgumentException("Destination is too small.", "destination");

            for (int row = 0; row < height; row++)
            {
                int sourceOffset = row * sourceStride;
                int destinationOffset = (height - 1 - row) * rowBytes;
                for (int column = 0; column < width; column++)
                {
                    int sourcePixel = sourceOffset + column * 4;
                    int destinationPixel = destinationOffset + column * 4;
                    int alpha = source[sourcePixel + 3];
                    if (alpha < 255)
                    {
                        destination[destinationPixel] = (byte)((source[sourcePixel] * alpha + 127) / 255);
                        destination[destinationPixel + 1] = (byte)((source[sourcePixel + 1] * alpha + 127) / 255);
                        destination[destinationPixel + 2] = (byte)((source[sourcePixel + 2] * alpha + 127) / 255);
                    }
                    else
                    {
                        destination[destinationPixel] = source[sourcePixel];
                        destination[destinationPixel + 1] = source[sourcePixel + 1];
                        destination[destinationPixel + 2] = source[sourcePixel + 2];
                    }
                    destination[destinationPixel + 3] = (byte)alpha;
                }
            }
        }
    }

    // Thin seam over the GDI calls so tests can exercise the lifetime rules without a window.
    internal class OverlayNativeOps
    {
        internal const int UlwAlpha = 2;

        internal virtual IntPtr GetScreenDc()
        {
            return GetDC(IntPtr.Zero);
        }

        internal virtual void ReleaseScreenDc(IntPtr screenDc)
        {
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }

        internal virtual IntPtr CreateMemoryDc(IntPtr screenDc)
        {
            return CreateCompatibleDC(screenDc);
        }

        internal virtual bool DeleteMemoryDc(IntPtr memoryDc)
        {
            return memoryDc == IntPtr.Zero || DeleteDC(memoryDc);
        }

        internal virtual IntPtr CreateDib(IntPtr dc, int width, int height, out IntPtr bits)
        {
            BITMAPINFO info = new BITMAPINFO();
            info.bmiHeader.biSize = (uint)Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = 0;
            info.bmiHeader.biSizeImage = 0;
            info.bmiHeader.biXPelsPerMeter = 0;
            info.bmiHeader.biYPelsPerMeter = 0;
            info.bmiHeader.biClrUsed = 0;
            info.bmiHeader.biClrImportant = 0;
            info.bmiColors = 0;
            return CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
        }

        internal virtual IntPtr Select(IntPtr memoryDc, IntPtr bitmap)
        {
            return SelectObject(memoryDc, bitmap);
        }

        internal virtual bool DeleteBitmap(IntPtr bitmap)
        {
            return bitmap == IntPtr.Zero || DeleteObject(bitmap);
        }

        internal virtual void CopyToDib(byte[] pixels, IntPtr destination)
        {
            Marshal.Copy(pixels, 0, destination, pixels.Length);
        }

        internal virtual bool Update(
            IntPtr windowHandle,
            IntPtr screenDc,
            ref POINT destination,
            ref SIZE size,
            IntPtr memoryDc,
            ref POINT source,
            ref BLENDFUNCTION blend)
        {
            return UpdateLayeredWindow(
                windowHandle,
                screenDc,
                ref destination,
                ref size,
                memoryDc,
                ref source,
                0,
                ref blend,
                UlwAlpha);
        }

        internal virtual int LastError
        {
            get { return Marshal.GetLastWin32Error(); }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr window, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(
            IntPtr dc,
            ref BITMAPINFO info,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UpdateLayeredWindow(
            IntPtr handle,
            IntPtr destinationDc,
            ref POINT destination,
            ref SIZE size,
            IntPtr sourceDc,
            ref POINT source,
            int colorKey,
            ref BLENDFUNCTION blend,
            int flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    internal sealed class OverlayFramePresenter : IDisposable
    {
        private readonly OverlayNativeOps ops;

        private Bitmap frame;
        private Graphics frameGraphics;
        private byte[] rawPixels;
        private byte[] packedPixels;
        private int width;
        private int height;

        private IntPtr memoryDc;
        private IntPtr dib;
        private IntPtr previousBitmap;
        private IntPtr dibBits;
        private int surfaceWidth;
        private int surfaceHeight;

        private bool disposed;

        internal OverlayFramePresenter() : this(new OverlayNativeOps())
        {
        }

        internal OverlayFramePresenter(OverlayNativeOps nativeOps)
        {
            if (nativeOps == null) throw new ArgumentNullException("nativeOps");
            ops = nativeOps;
        }

        internal Bitmap Frame
        {
            get { return frame; }
        }

        internal Graphics FrameGraphics
        {
            get { return frameGraphics; }
        }

        internal byte[] RawScratch
        {
            get { return rawPixels; }
        }

        internal byte[] PackedScratch
        {
            get { return packedPixels; }
        }

        internal IntPtr MemoryDc
        {
            get { return memoryDc; }
        }

        internal IntPtr Dib
        {
            get { return dib; }
        }

        internal void EnsureFrame(int newWidth, int newHeight)
        {
            if (disposed) throw new ObjectDisposedException("OverlayFramePresenter");
            if (newWidth <= 0) throw new ArgumentOutOfRangeException("newWidth");
            if (newHeight <= 0) throw new ArgumentOutOfRangeException("newHeight");
            if (frame != null && frameGraphics != null && rawPixels != null && packedPixels != null
                && width == newWidth && height == newHeight) return;

            // A dimension change invalidates both halves of the cache. Release first so the
            // old GDI objects are unselected and deleted before the new frame is allocated.
            ReleaseSurface();
            ReleaseFrame();

            Bitmap createdFrame = null;
            Graphics createdGraphics = null;
            try
            {
                createdFrame = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
                createdGraphics = Graphics.FromImage(createdFrame);
                createdGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                createdGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                createdGraphics.CompositingMode = CompositingMode.SourceOver;
                createdGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            }
            catch (Exception)
            {
                if (createdGraphics != null) createdGraphics.Dispose();
                if (createdFrame != null) createdFrame.Dispose();
                throw;
            }

            frame = createdFrame;
            frameGraphics = createdGraphics;
            width = newWidth;
            height = newHeight;
            int bytes = width * 4 * height;
            rawPixels = new byte[bytes];
            packedPixels = new byte[bytes];
        }

        internal void Present(IntPtr windowHandle, int x, int y)
        {
            if (disposed) throw new ObjectDisposedException("OverlayFramePresenter");
            if (frame == null || frameGraphics == null || packedPixels == null)
                throw new OverlayPresentException("Overlay frame is not ready.");

            IntPtr screenDc = ops.GetScreenDc();
            if (screenDc == IntPtr.Zero)
                throw new OverlayPresentException("GetDC failed.");

            try
            {
                // The screen DC is cheap and follows display changes; the memory DC, DIB and
                // both pixel buffers stay cached until the dimensions change or the window closes.
                EnsureSurface(screenDc, width, height);
                PackFrame();

                POINT destination = new POINT();
                destination.X = x;
                destination.Y = y;
                SIZE size = new SIZE();
                size.cx = width;
                size.cy = height;
                POINT source = new POINT();
                source.X = 0;
                source.Y = 0;
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = 0;
                blend.BlendFlags = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;

                if (!ops.Update(
                    windowHandle,
                    screenDc,
                    ref destination,
                    ref size,
                    memoryDc,
                    ref source,
                    ref blend))
                {
                    throw new OverlayPresentException(
                        "UpdateLayeredWindow failed (Win32 error " + ops.LastError + ").");
                }
            }
            finally
            {
                ops.ReleaseScreenDc(screenDc);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ReleaseSurface();
            ReleaseFrame();
        }

        private void EnsureSurface(IntPtr screenDc, int newWidth, int newHeight)
        {
            if (memoryDc != IntPtr.Zero && dib != IntPtr.Zero
                && surfaceWidth == newWidth && surfaceHeight == newHeight)
            {
                return;
            }

            ReleaseSurface();

            IntPtr createdDc = IntPtr.Zero;
            IntPtr createdDib = IntPtr.Zero;
            IntPtr createdPrevious = IntPtr.Zero;
            try
            {
                createdDc = ops.CreateMemoryDc(screenDc);
                if (createdDc == IntPtr.Zero)
                    throw new OverlayPresentException("CreateCompatibleDC failed.");
                IntPtr bits;
                createdDib = ops.CreateDib(screenDc, newWidth, newHeight, out bits);
                if (createdDib == IntPtr.Zero || bits == IntPtr.Zero)
                    throw new OverlayPresentException("CreateDIBSection failed.");
                createdPrevious = ops.Select(createdDc, createdDib);
                if (createdPrevious == IntPtr.Zero || createdPrevious == new IntPtr(-1))
                {
                    createdPrevious = IntPtr.Zero;
                    throw new OverlayPresentException("SelectObject failed.");
                }
                memoryDc = createdDc;
                dib = createdDib;
                previousBitmap = createdPrevious;
                dibBits = bits;
                surfaceWidth = newWidth;
                surfaceHeight = newHeight;
                createdDc = IntPtr.Zero;
                createdDib = IntPtr.Zero;
                createdPrevious = IntPtr.Zero;
            }
            finally
            {
                if (createdPrevious != IntPtr.Zero) ops.Select(createdDc, createdPrevious);
                // Destroy the DC before deleting its bitmap even if restoring
                // the selected object failed.
                if (createdDc != IntPtr.Zero) ops.DeleteMemoryDc(createdDc);
                if (createdDib != IntPtr.Zero) ops.DeleteBitmap(createdDib);
            }
        }

        private void PackFrame()
        {
            BitmapData data = frame.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                int required = stride * height;
                if (rawPixels == null || rawPixels.Length < required) rawPixels = new byte[required];
                Marshal.Copy(data.Scan0, rawPixels, 0, required);
                OverlayPixels.PackPremultipliedBottomUp(
                    rawPixels,
                    stride,
                    width,
                    height,
                    packedPixels);
            }
            finally
            {
                frame.UnlockBits(data);
            }

            ops.CopyToDib(packedPixels, dibBits);
        }

        private void ReleaseFrame()
        {
            if (frameGraphics != null)
            {
                frameGraphics.Dispose();
                frameGraphics = null;
            }
            if (frame != null)
            {
                frame.Dispose();
                frame = null;
            }
            rawPixels = null;
            packedPixels = null;
            width = 0;
            height = 0;
        }

        private void ReleaseSurface()
        {
            if (memoryDc != IntPtr.Zero && previousBitmap != IntPtr.Zero)
                ops.Select(memoryDc, previousBitmap);

            previousBitmap = IntPtr.Zero;
            if (memoryDc != IntPtr.Zero)
            {
                ops.DeleteMemoryDc(memoryDc);
                memoryDc = IntPtr.Zero;
            }
            if (dib != IntPtr.Zero)
            {
                ops.DeleteBitmap(dib);
                dib = IntPtr.Zero;
            }
            dibBits = IntPtr.Zero;
            surfaceWidth = 0;
            surfaceHeight = 0;
        }
    }
}

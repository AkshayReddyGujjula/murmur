using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Murmur.App.Ui
{
    public sealed class OverlayWindow : Form
    {
        private const int BarCount = 24;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopMost = 0x00000008;
        private const int WsExNoActivate = 0x08000000;
        private const int WsExLayered = 0x00080000;
        private const int SwShowNoActivate = 4;
        private const int SwHide = 0;
        private const int SwpNoSize = 0x0001;
        private const int SwpNoMove = 0x0002;
        private const int SwpNoActivate = 0x0010;
        private const int SwpShowWindow = 0x0040;
        private const int MonitorDefaultToNearest = 2;

        private readonly Timer timer;
        private readonly object sync = new object();
        private readonly WaveHistory wave;
        private readonly OverlayState state;
        private readonly OverlayFramePresenter presenter;

        private string caption;
        private float pendingLevel;
        private bool hasLevel;
        private bool faulted;
        private bool disposed;

        public OverlayWindow() : this(null)
        {
        }

        internal OverlayWindow(OverlayFramePresenter framePresenter)
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            Text = "Murmur";
            Size = new Size(1, 1);

            state = new OverlayState();
            caption = String.Empty;
            wave = new WaveHistory(BarCount);
            presenter = framePresenter ?? new OverlayFramePresenter();
            timer = new Timer();
            timer.Interval = 33;
            timer.Tick += OnTick;
        }

        // Raised once per window when native presentation fails. The overlay has already
        // stopped its animation and hidden itself when this fires; the host should log it.
        public event Action<string> Fault;

        public bool PlaceAtTop { get; set; }

        public OverlayMode Mode
        {
            get { return state.Mode; }
        }

        public bool IsShowing
        {
            get { return state.IsShowing; }
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExLayered | WsExNoActivate | WsExToolWindow | WsExTopMost | WsExTransparent;
                return parameters;
            }
        }

        public void PushLevel(float level)
        {
            if (disposed) return;
            if (Single.IsNaN(level) || level < 0f) level = 0f;
            if (level > 1f) level = 1f;
            lock (sync)
            {
                pendingLevel = level;
                hasLevel = true;
            }
        }

        public void ShowMode(OverlayMode overlayMode, string text)
        {
            if (disposed) return;
            state.Enter(overlayMode, DateTime.UtcNow);
            caption = text == null ? String.Empty : text;

            if (overlayMode == OverlayMode.Hidden)
            {
                timer.Stop();
                if (IsHandleCreated) ShowWindow(Handle, SwHide);
                return;
            }

            if (overlayMode == OverlayMode.Recording)
            {
                lock (sync)
                {
                    ResetWave();
                    pendingLevel = 0f;
                    hasLevel = false;
                }
            }

            ApplyBounds();
            Render(0.0);
            if (state.Mode == OverlayMode.Hidden) return; // Render failed and already concealed the window
            ShowNoActivate();
            if (!timer.Enabled) timer.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                disposed = true;
                timer.Stop();
                timer.Tick -= OnTick;
                timer.Dispose();
                presenter.Dispose();
            }
            base.Dispose(disposing);
        }

        private void ResetWave()
        {
            int count = wave.Snapshot().Length;
            for (int index = 0; index < count; index++) wave.Push(0f);
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (state.Mode == OverlayMode.Hidden)
            {
                timer.Stop();
                return;
            }

            DateTime now = DateTime.UtcNow;
            double seconds = (now - state.ShownAt).TotalSeconds;
            if (state.ShouldHide(now))
            {
                ShowMode(OverlayMode.Hidden, null);
                return;
            }

            if (state.Mode == OverlayMode.Recording)
            {
                float level;
                lock (sync)
                {
                    level = hasLevel ? pendingLevel : 0f;
                    hasLevel = false;
                }
                wave.Push(level);
            }

            Render(seconds);
        }

        private void ApplyBounds()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            float scale = GetScale();
            Rectangle area = screen.WorkingArea;
            int margin = (int)Math.Round(24f * scale);
            Size size = Measure(state.Mode, caption, scale);
            int maximumWidth = area.Width - margin * 2;
            if (maximumWidth > 64 && size.Width > maximumWidth) size.Width = maximumWidth;
            Rectangle bounds = OverlayLayout.Place(area, size, PlaceAtTop, margin);
            SetBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }

        private void Render(double seconds)
        {
            if (disposed || state.Mode == OverlayMode.Hidden) return;
            RenderFrame(seconds, Handle, Width, Height);
        }

        // Test seam: draws and presents at explicit dimensions without creating or showing a
        // window handle. The injected presenter decides what the native layer does.
        internal void RenderForTests(int width, int height)
        {
            RenderFrame(0.0, IntPtr.Zero, width, height);
        }

        // Test seam: exercises the normal Render guard without changing the overlay mode.
        internal void RenderIfShowingForTests()
        {
            Render(0.0);
        }

        internal bool TimerEnabledForTests
        {
            get { return timer.Enabled; }
        }

        internal void StartTimerForTests()
        {
            if (!disposed && !timer.Enabled) timer.Start();
        }

        private void RenderFrame(double seconds, IntPtr target, int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            try
            {
                presenter.EnsureFrame(width, height);
                Graphics graphics = presenter.FrameGraphics;
                graphics.Clear(Color.Transparent);
                DrawPill(graphics, width, height);
                DrawContent(graphics, width, height, seconds);
                presenter.Present(target, Left, Top);
            }
            catch (Exception error)
            {
                Fail("Overlay presentation failed: " + error.Message);
            }
        }

        private void Fail(string message)
        {
            bool notify;
            lock (sync)
            {
                notify = !faulted;
                faulted = true;
            }

            timer.Stop();
            state.Enter(OverlayMode.Hidden, DateTime.UtcNow);
            if (IsHandleCreated) ShowWindow(Handle, SwHide);

            if (!notify) return;
            Action<string> handler = Fault;
            if (handler == null) return;
            try
            {
                handler(String.IsNullOrEmpty(message) ? "Overlay presentation failed." : message);
            }
            catch (Exception)
            {
                // A logging or tray handler must never take the UI thread down with it.
            }
        }

        private static void DrawPill(Graphics graphics, int width, int height)
        {
            float radius = height / 2f;
            using (GraphicsPath shape = Logo.Rounded(new RectangleF(0f, 0f, width, height), radius))
            using (SolidBrush fill = new SolidBrush(Color.Black))
            {
                graphics.FillPath(fill, shape);
            }

            using (GraphicsPath border = Logo.Rounded(new RectangleF(0.5f, 0.5f, width - 1f, height - 1f), Math.Max(0f, radius - 0.5f)))
            using (Pen pen = new Pen(Color.FromArgb(42, 42, 42), 1f))
            {
                graphics.DrawPath(pen, border);
            }
        }

        private void DrawContent(Graphics graphics, int width, int height, double seconds)
        {
            switch (state.Mode)
            {
                case OverlayMode.Recording:
                    DrawRecording(graphics, width, height);
                    break;
                case OverlayMode.Starting:
                    DrawPulse(graphics, width, height, seconds);
                    break;
                case OverlayMode.Working:
                    DrawShimmer(graphics, width, height, seconds);
                    break;
                case OverlayMode.Done:
                    DrawDone(graphics, width, height);
                    break;
                case OverlayMode.Error:
                    DrawError(graphics, width, height);
                    break;
            }
        }

        private void DrawRecording(Graphics graphics, int width, int height)
        {
            float scale = height / 36f;
            float centreY = height / 2f;
            float dotRadius = 3f * scale;
            using (SolidBrush red = new SolidBrush(Color.FromArgb(255, 69, 58)))
            {
                graphics.FillEllipse(red, 16f * scale - dotRadius, centreY - dotRadius, dotRadius * 2f, dotRadius * 2f);
            }

            float left = 28f * scale;
            float right = width - 14f * scale;
            float span = right - left;
            if (span <= 0f) return;

            float gap = 1.6f * scale;
            float barWidth = (span - gap * (BarCount - 1)) / BarCount;
            if (barWidth < 1f * scale)
            {
                barWidth = 1f * scale;
                gap = Math.Max(0.5f * scale, (span - barWidth * BarCount) / (BarCount - 1));
            }

            float[] levels = wave.Snapshot();
            float maxHeight = height - 12f * scale;
            if (maxHeight < barWidth) maxHeight = barWidth;

            using (SolidBrush white = new SolidBrush(Color.White))
            {
                for (int index = 0; index < BarCount; index++)
                {
                    float level = index < levels.Length ? levels[index] : 0f;
                    if (level < 0f) level = 0f;
                    if (level > 1f) level = 1f;
                    float barHeight = barWidth + (maxHeight - barWidth) * level;
                    float x = left + index * (barWidth + gap);
                    RectangleF bar = new RectangleF(x, centreY - barHeight / 2f, barWidth, barHeight);
                    using (GraphicsPath path = Logo.Rounded(bar, barWidth / 2f))
                    {
                        graphics.FillPath(white, path);
                    }
                }
            }
        }

        private static void DrawPulse(Graphics graphics, int width, int height, double seconds)
        {
            float scale = height / 36f;
            float radius = 3f * scale;
            float spacing = 10f * scale;
            float centreY = height / 2f;
            float startX = width / 2f - spacing;

            for (int index = 0; index < 3; index++)
            {
                double motion = Math.Sin((seconds * 2.4 - index * 0.23) * Math.PI * 2.0);
                float alpha = 0.30f + 0.70f * (float)((motion + 1.0) / 2.0);
                if (alpha < 0f) alpha = 0f;
                if (alpha > 1f) alpha = 1f;
                using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(alpha * 255f), 255, 255, 255)))
                {
                    graphics.FillEllipse(brush, startX + index * spacing - radius, centreY - radius, radius * 2f, radius * 2f);
                }
            }
        }

        private static void DrawShimmer(Graphics graphics, int width, int height, double seconds)
        {
            float scale = height / 36f;
            float radius = 2.6f * scale;
            float spacing = 8.5f * scale;
            float centreY = height / 2f;
            float startX = width / 2f - spacing * 2f;
            float travel = (float)((seconds * 5.0) % 4.0);

            for (int index = 0; index < 5; index++)
            {
                float distance = Math.Abs(index - travel);
                float alpha = 0.22f + 0.78f * Math.Max(0f, 1f - distance * 0.55f);
                if (alpha > 1f) alpha = 1f;
                using (SolidBrush brush = new SolidBrush(Color.FromArgb((int)(alpha * 255f), 255, 255, 255)))
                {
                    graphics.FillEllipse(brush, startX + index * spacing - radius, centreY - radius, radius * 2f, radius * 2f);
                }
            }
        }

        private void DrawDone(Graphics graphics, int width, int height)
        {
            float scale = height / 36f;
            float padding = 14f * scale;
            float icon = 12f * scale;
            float centreY = height / 2f;

            using (Pen pen = new Pen(Color.White, Math.Max(1.5f, 1.6f * scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                PointF[] check = new PointF[]
                {
                    new PointF(padding, centreY),
                    new PointF(padding + icon * 0.42f, centreY + icon * 0.42f),
                    new PointF(padding + icon, centreY - icon * 0.45f)
                };
                graphics.DrawLines(pen, check);
            }

            if (String.IsNullOrEmpty(caption)) return;
            using (Font font = CreateFont(scale))
            using (StringFormat format = new StringFormat())
            using (SolidBrush white = new SolidBrush(Color.White))
            {
                format.LineAlignment = StringAlignment.Center;
                format.Alignment = StringAlignment.Near;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                float textLeft = padding + icon + 8f * scale;
                float textWidth = Math.Max(0f, width - textLeft - padding);
                graphics.DrawString(caption, font, white, new RectangleF(textLeft, 0f, textWidth, height), format);
            }
        }

        private void DrawError(Graphics graphics, int width, int height)
        {
            if (caption.Length == 0) return;
            float scale = height / 36f;
            float padding = 14f * scale;
            using (Font font = CreateFont(scale))
            using (StringFormat format = new StringFormat())
            using (SolidBrush red = new SolidBrush(Color.FromArgb(255, 69, 58)))
            {
                format.LineAlignment = StringAlignment.Center;
                format.Alignment = StringAlignment.Center;
                format.Trimming = StringTrimming.EllipsisCharacter;
                format.FormatFlags = StringFormatFlags.NoWrap;
                RectangleF textBounds = new RectangleF(padding, 0f, Math.Max(0f, width - padding * 2f), height);
                graphics.DrawString(caption, font, red, textBounds, format);
            }
        }

        private static Font CreateFont(float scale)
        {
            return new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static Size Measure(OverlayMode overlayMode, string text, float scale)
        {
            int height = (int)Math.Round(36f * scale);
            switch (overlayMode)
            {
                case OverlayMode.Recording:
                    return new Size((int)Math.Round(132f * scale), height);
                case OverlayMode.Starting:
                case OverlayMode.Working:
                    return new Size((int)Math.Round(110f * scale), height);
                case OverlayMode.Done:
                {
                    float width = (14f + 12f + 8f + 14f) * scale + MeasureText(text, scale);
                    int minimum = (int)Math.Round(96f * scale);
                    return new Size(Math.Max((int)Math.Ceiling(width), minimum), height);
                }
                case OverlayMode.Error:
                {
                    int width = (int)Math.Ceiling(MeasureText(text, scale) + 28f * scale);
                    int minimum = (int)Math.Round(80f * scale);
                    int maximum = (int)Math.Round(420f * scale);
                    if (width < minimum) width = minimum;
                    if (width > maximum) width = maximum;
                    return new Size(width, height);
                }
                default:
                    return new Size((int)Math.Round(96f * scale), height);
            }
        }

        private static float MeasureText(string text, float scale)
        {
            if (String.IsNullOrEmpty(text)) return 0f;
            using (Font font = CreateFont(scale))
            using (Bitmap measure = new Bitmap(1, 1, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(measure))
            {
                return graphics.MeasureString(text, font).Width;
            }
        }

        private static float GetScale()
        {
            try
            {
                POINT cursor;
                if (GetCursorPos(out cursor))
                {
                    IntPtr monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
                    if (monitor != IntPtr.Zero)
                    {
                        uint dpiX;
                        uint dpiY;
                        if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 && dpiX > 0)
                            return ClampScale(dpiX / 96f);
                    }
                }
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }

            try
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return ClampScale(graphics.DpiX / 96f);
                }
            }
            catch (Exception)
            {
            }
            return 1f;
        }

        private static float ClampScale(float value)
        {
            if (Single.IsNaN(value) || value <= 0f) return 1f;
            if (value < 1f) return 1f;
            if (value > 4f) return 4f;
            return value;
        }

        private void ShowNoActivate()
        {
            IntPtr handle = Handle;
            ShowWindow(handle, SwShowNoActivate);
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpShowWindow);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr handle, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr handle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            int flags);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    }
}

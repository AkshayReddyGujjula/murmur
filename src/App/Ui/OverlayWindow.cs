using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Murmur.App.Ui
{
    public sealed class OverlayWindow : Form
    {
        private const int BarCount = OverlayMotion.Bars;
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
        private readonly Stopwatch animation = Stopwatch.StartNew();
        private readonly WaveMotion animatedWave = new WaveMotion();
        private readonly float[] samples = new float[BarCount];
        private SizeF fromSize, targetSize;
        private OverlayMode previousMode;
        private string previousCaption = String.Empty;
        private bool compactResult, previousCompact, animate = true;
        private double lastTick, nextWaveSample;
        private long levelAt;

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
            timer.Interval = 15;
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
                levelAt = Stopwatch.GetTimestamp();
            }
        }

        public void ShowMode(OverlayMode overlayMode, string text)
        {
            ShowModeCore(overlayMode, text, false);
        }

        public void ShowResult(bool copied, string description)
        {
            ShowModeCore(copied ? OverlayMode.Done : OverlayMode.Error, description, true);
        }

        private void ShowModeCore(OverlayMode overlayMode, string text, bool compact)
        {
            if (disposed) return;
            bool wasShowing = state.IsShowing;
            SizeF current = targetSize.Width > 0
                ? OverlayMotion.Bounds(Width, Height, fromSize, targetSize, animation.Elapsed.TotalSeconds, animate).Size : SizeF.Empty;
            previousMode = state.Mode;
            previousCaption = caption;
            previousCompact = compactResult;
            state.Enter(overlayMode, DateTime.UtcNow);
            caption = text == null ? String.Empty : text;
            AccessibleName = caption;
            compactResult = compact;

            if (overlayMode == OverlayMode.Hidden)
            {
                timer.Stop();
                if (IsHandleCreated) ShowWindow(Handle, SwHide);
                return;
            }

            bool enabled;
            animate = !SystemParametersInfo(0x1042, 0, out enabled, 0) || enabled; // SPI_GETCLIENTAREAANIMATION
            float scale = GetScale();
            int resultSize = (int)Math.Round(OverlayMotion.Height * scale);
            targetSize = compact ? new Size(resultSize, resultSize) : Measure(overlayMode, caption, scale);
            fromSize = wasShowing && current.Width > 0 ? current : new SizeF(targetSize.Width * 0.82f, targetSize.Height * 0.92f);
            animation.Restart(); lastTick = 0; nextWaveSample = 0;

            if (overlayMode == OverlayMode.Recording)
            {
                lock (sync)
                {
                    ResetWave();
                    pendingLevel = 0f;
                    hasLevel = false;
                    animatedWave.Clear();
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
            double seconds = animation.Elapsed.TotalSeconds;
            if (state.ShouldHide(now))
            {
                ShowMode(OverlayMode.Hidden, null);
                return;
            }

            if (state.Mode == OverlayMode.Recording)
            {
                if (seconds >= nextWaveSample)
                {
                    float level;
                    lock (sync)
                    {
                        double age = (Stopwatch.GetTimestamp() - levelAt) / (double)Stopwatch.Frequency;
                        level = hasLevel && age < 0.18 ? pendingLevel : 0f;
                    }
                    wave.Push(level);
                    wave.CopyTo(samples);
                    nextWaveSample = seconds + 1.0 / 45.0;
                }
                animatedWave.Step(samples, seconds - lastTick);
            }
            lastTick = seconds;
            Render(seconds);
        }

        private void ApplyBounds()
        {
            Screen screen = Screen.FromPoint(Cursor.Position);
            float scale = GetScale();
            Rectangle area = screen.WorkingArea;
            int margin = (int)Math.Round(22f * scale); // 2px transparent animation inset retains the visible 24px margin
            // Keep one reusable surface through the normal recording/loading/result
            // sequence. Only the drawn silhouette morphs, not the native DIB size.
            Size size = new Size((int)Math.Ceiling(Math.Max(OverlayMotion.RecordingWidth * scale, Math.Max(fromSize.Width, targetSize.Width)) + 4f * scale),
                (int)Math.Ceiling(Math.Max(fromSize.Height, targetSize.Height) + 4f * scale));
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
                DrawVisual(graphics, width, height, seconds);
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

        private void DrawVisual(Graphics graphics, int width, int height, double seconds)
        {
            if (targetSize.Width <= 0)
            {
                OverlayVisual.Pill(graphics, new RectangleF(0, 0, width, height), 1f);
                return;
            }
            RectangleF pill = OverlayMotion.Bounds(width, height, fromSize, targetSize, seconds, animate);
            double duration = state.Mode == OverlayMode.Done ? OverlayState.DoneHideSeconds
                : state.Mode == OverlayMode.Error ? OverlayState.ErrorHideSeconds : Double.MaxValue;
            float opacity = OverlayMotion.EntranceOpacity(previousMode == OverlayMode.Hidden, seconds, animate)
                * (animate ? OverlayMotion.Ease((duration - seconds) / 0.18) : 1f);
            float exit = animate ? OverlayMotion.Ease((duration - seconds) / 0.18) : 1f;
            float shrink = 0.94f + 0.06f * exit;
            pill.Inflate(-pill.Width * (1f - shrink) / 2f, -pill.Height * (1f - shrink) / 2f);
            OverlayVisual.Pill(graphics, pill, opacity);
            float blend = animate ? OverlayMotion.Ease(seconds / 0.16) : 1f;
            if (previousMode != OverlayMode.Hidden && blend < 1f)
                OverlayVisual.Content(graphics, pill, previousMode, previousCompact, previousCaption,
                    animatedWave.Values, 0.6, opacity * (1f - blend), animate);
            OverlayVisual.Content(graphics, pill, state.Mode, compactResult, caption,
                animatedWave.Values, seconds, opacity * blend, animate);
        }

        private static Font CreateFont(float scale)
        {
            return new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
        }

        private static Size Measure(OverlayMode overlayMode, string text, float scale)
        {
            int height = (int)Math.Round(OverlayMotion.Height * scale);
            switch (overlayMode)
            {
                case OverlayMode.Recording:
                    return new Size((int)Math.Round(OverlayMotion.RecordingWidth * scale), height);
                case OverlayMode.Starting:
                case OverlayMode.Working:
                    return new Size((int)Math.Round(110f * scale), height);
                case OverlayMode.Done:
                {
                    float width = 28f * scale + MeasureText(text, scale);
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

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        private static extern bool SystemParametersInfo(uint action, uint parameter, out bool value, uint flags);

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

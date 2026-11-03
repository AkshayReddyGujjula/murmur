using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Murmur.App.Ui;

namespace Murmur.Tests.App
{
    internal static class UiTests
    {
        private static int checks;

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            checks++;
        }

        [STAThread]
        public static int Main()
        {
            try
            {
                CheckLogoIcons();
                CheckLogoIco();
                CheckLayout();
                CheckWaveHistory();
                CheckOverlayMotion();
                CheckSilentNotifications();
                CheckTrayItem();
                CheckOverlayState();
                CheckOverlayWindowContract();
                CheckOverlayPixelConversion();
                CheckOverlayPresenter();
                CheckOverlayRenderReuse();
                CheckOverlayFaultHandling();
                CheckOverlaySurfaceFailures();
                CheckTrayMenuHelper();
                CheckMenuItemFlags();
                CheckTrayIconConstruction();
                Console.WriteLine("PASS: Ui logo, ICO export, overlay layout, waveform history and tray model (" + checks + " checks).");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error.Message);
                return 1;
            }
        }

        private static void CheckLogoIcons()
        {
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 256 };
            for (int index = 0; index < sizes.Length; index++)
            {
                int size = sizes[index];
                using (Icon icon = Logo.CreateIcon(size, false))
                {
                    Check(icon.Width == size && icon.Height == size, "logo icon " + size + " size");
                }
                using (Icon icon = Logo.CreateIcon(size, true))
                {
                    Check(icon.Width == size && icon.Height == size, "recording icon " + size + " size");
                }
            }

            using (Bitmap bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                Logo.Draw(graphics, new RectangleF(0f, 0f, 64f, 64f), false);
                Check(bitmap.GetPixel(0, 0).A == 0, "logo corner is transparent");
                Color centre = bitmap.GetPixel(32, 32);
                Check(centre.R > 220 && centre.G > 220 && centre.B > 220, "logo centre bar is white");
                Color body = bitmap.GetPixel(32, 58);
                Check(body.R < 10 && body.G < 10 && body.B < 10, "logo surface is black");
            }

            using (Bitmap bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                Logo.Draw(graphics, new RectangleF(0f, 0f, 64f, 64f), true);
                bool found = false;
                for (int y = 0; y < 64 && !found; y++)
                {
                    for (int x = 0; x < 64 && !found; x++)
                    {
                        Color pixel = bitmap.GetPixel(x, y);
                        if (pixel.R > 200 && pixel.G < 120 && pixel.B < 120) found = true;
                    }
                }
                Check(found, "recording logo has a red dot");
            }
        }

        private static void CheckLogoIco()
        {
            string path = Path.Combine(Path.GetTempPath(), "murmur-ui-" + Guid.NewGuid().ToString("N") + ".ico");
            try
            {
                Logo.SaveIco(path);
                Check(File.Exists(path), "ICO file written");
                byte[] bytes = File.ReadAllBytes(path);
                Check(bytes.Length > 6 + 16 * 6, "ICO has image data");
                Check(BitConverter.ToUInt16(bytes, 0) == 0, "ICO reserved header");
                Check(BitConverter.ToUInt16(bytes, 2) == 1, "ICO type is icon");
                int count = BitConverter.ToUInt16(bytes, 4);
                Check(count == 6, "ICO has six images");

                int[] expected = new int[] { 16, 24, 32, 48, 64, 256 };
                uint next = (uint)(6 + 16 * count);
                for (int index = 0; index < count; index++)
                {
                    int offset = 6 + index * 16;
                    byte dimension = expected[index] >= 256 ? (byte)0 : (byte)expected[index];
                    Check(bytes[offset] == dimension && bytes[offset + 1] == dimension, "ICO entry " + index + " dimensions");
                    Check(bytes[offset + 2] == 0 && bytes[offset + 3] == 0, "ICO entry " + index + " colour/reserved");
                    Check(BitConverter.ToUInt16(bytes, offset + 4) == 1, "ICO entry " + index + " planes");
                    Check(BitConverter.ToUInt16(bytes, offset + 6) == 32, "ICO entry " + index + " bit depth");
                    uint length = BitConverter.ToUInt32(bytes, offset + 8);
                    uint imageOffset = BitConverter.ToUInt32(bytes, offset + 12);
                    Check(length > 8, "ICO entry " + index + " payload");
                    Check(imageOffset == next, "ICO entry " + index + " offset");
                    Check((long)imageOffset + (long)length <= bytes.Length, "ICO entry " + index + " bounds");
                    Check(bytes[imageOffset] == 0x89 && bytes[imageOffset + 1] == 0x50
                        && bytes[imageOffset + 2] == 0x4E && bytes[imageOffset + 3] == 0x47,
                        "ICO entry " + index + " is a PNG");
                    next = imageOffset + length;
                }
                Check(next == (uint)bytes.Length, "ICO payload is contiguous");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void CheckLayout()
        {
            Rectangle area = new Rectangle(0, 0, 1920, 1040);
            Size pill = new Size(132, 36);

            Rectangle bottom = OverlayLayout.Place(area, pill, false, 24);
            Check(bottom.X == 894 && bottom.Y == 980, "bottom centre placement");
            Check(bottom.Width == pill.Width && bottom.Height == pill.Height, "placement keeps size");

            Rectangle top = OverlayLayout.Place(area, pill, true, 24);
            Check(top.X == 894 && top.Y == 24, "top centre placement");

            Rectangle framed = new Rectangle(100, 50, 1000, 700);
            Check(OverlayLayout.Place(framed, pill, true, 10).Y == 60, "top margin inside working area");
            Check(OverlayLayout.Place(framed, pill, false, 10).Y == 704, "bottom margin inside working area");
            Check(OverlayLayout.Place(framed, pill, true, 10).X == 534, "centred in offset working area");
            Check(OverlayLayout.Place(framed, pill, true, -5).Y == 50, "negative margin clamps to the edge");

            Rectangle oversized = OverlayLayout.Place(new Rectangle(10, 20, 200, 100), new Size(500, 300), false, 10);
            Check(oversized.X == 10 && oversized.Y == 20, "oversized pill stays at the working-area origin");
            Check(oversized.Width == 500 && oversized.Height == 300, "oversized pill keeps its requested size");
        }

        private static void CheckWaveHistory()
        {
            WaveHistory history = new WaveHistory(8);
            float[] empty = history.Snapshot();
            Check(empty.Length == 8, "wave history bar count");
            for (int index = 0; index < empty.Length; index++) Check(empty[index] == 0f, "wave history starts at zero");

            history.Push(1f);
            float first = history.Snapshot()[7];
            Check(first > 0.5f && first <= 1f, "wave history rises fast");

            history.Push(1f);
            float second = history.Snapshot()[7];
            Check(second >= first, "wave history keeps rising toward the target");

            float peak = second;
            history.Push(0f);
            float falling = history.Snapshot()[7];
            Check(falling < peak && falling > peak * 0.5f, "wave history falls slowly");

            for (int index = 0; index < 80; index++) history.Push(0f);
            Check(history.Snapshot()[7] < 0.01f, "wave history eventually settles at zero");

            history.Push(5f);
            float high = history.Snapshot()[7];
            Check(high > 0f && high <= 1f, "wave history clamps high levels");
            history.Push(-5f);
            Check(history.Snapshot()[7] >= 0f, "wave history clamps low levels");
            history.Push(Single.NaN);
            float nan = history.Snapshot()[7];
            Check(nan >= 0f && nan <= 1f, "wave history rejects NaN");

            float[] copy = history.Snapshot();
            copy[0] = 42f;
            Check(history.Snapshot()[0] != 42f, "wave history snapshot is a copy");

            WaveHistory rolled = new WaveHistory(3);
            rolled.Push(1f);
            float[] before = rolled.Snapshot();
            rolled.Push(0f);
            float[] after = rolled.Snapshot();
            Check(after[0] == before[1] && after[1] == before[2], "wave history rolls oldest samples out");

            WaveHistory tiny = new WaveHistory(0);
            Check(tiny.Snapshot().Length == 1, "wave history needs at least one bar");
            tiny.Push(1f);
            Check(tiny.Snapshot()[0] > 0f, "single-bar history still records levels");

            WaveHistory large = new WaveHistory(1000);
            Check(large.Snapshot().Length == 256, "wave history bounds its bar count");
        }

        private static void CheckOverlayMotion()
        {
            Check(OverlayMotion.Bars == 27, "waveform has another twenty percent fewer bars than 34");
            Check(Math.Abs(OverlayMotion.Height / (36f * 1.05f) - 1.10f) < 0.0001f, "pill is another ten percent taller before pixel rounding");
            Check(OverlayMotion.RecordingWidth == 132f, "recording visual width unchanged");
            float bar = OverlayMotion.BarWidth(96f);
            Check(Math.Abs(bar / (96f / 95f) - 1.28f) < 0.001f, "strokes are 28 percent thicker in the same span");
            Check((96f - bar * OverlayMotion.Bars) / (OverlayMotion.Bars - 1) > 1f, "thicker bars retain clear gaps in the existing pill");
            Check(OverlayMotion.VisualEnergy(0.08f) == 0, "quiet ambient levels leave the waveform calm");
            float previous = 0.975f * (float)Math.Pow((0.4f - 0.08f) / 0.92f, 1.5);
            Check(Math.Abs(OverlayMotion.VisualEnergy(0.4f) / previous - 1.35f) < 0.0001f, "visual response increases another thirty-five percent");
            Check(OverlayMotion.VisualEnergy(1f) == 1f, "loud sound reaches full visual range without overflowing");
            Check(Math.Abs(OverlayMotion.BarHeight(OverlayMotion.Height, 1f, 1f) - (OverlayMotion.Height - 4f)) < 0.001f, "strong peaks reach two pixels from the top and bottom");
            Check(OverlayMotion.BarHeight(OverlayMotion.Height, 0f, 1f) == 2f, "silent bars remain small");
            Check(OverlayMotion.BarHeight(OverlayMotion.Height * 2, 5f, 1f) <= (OverlayMotion.Height - 4f) * 2, "scaled loud peaks stay within their padding");
            Check(OverlayMotion.Spring(0) == 0 && OverlayMotion.Spring(0.6) == 1, "spring starts and settles exactly");
            for (int i = 0; i <= 120; i++)
            {
                float p = OverlayMotion.Spring(i / 200.0);
                Check(!Single.IsNaN(p) && p >= 0f && p < 1.02f, "spring stays gently bounded " + i);
            }
            RectangleF end = OverlayMotion.Bounds(136, 42, new SizeF(110, 38), new SizeF(38, 38), 0.7, true);
            Check(end.Width == end.Height && end.Width == 38f && end.X + end.Width / 2 == 68f, "completion collapses to a centred circle");
            RectangleF reduced = OverlayMotion.Bounds(136, 42, new SizeF(110, 38), new SizeF(38, 38), 0, false);
            Check(reduced == end, "reduced motion settles without a transition");
            Check(OverlayMotion.EntranceOpacity(false, 0, true) == 1f, "visible state transitions never flash the pill transparent");
            Check(OverlayMotion.EntranceOpacity(true, 0, true) == 0f, "hidden entrance fades in gently");
            Check(OverlayMotion.EntranceOpacity(true, 0, false) == 1f, "reduced motion entrance is immediately visible");
            WaveMotion motion = new WaveMotion();
            float[] target = new float[OverlayMotion.Bars];
            for (int i = 0; i < target.Length; i++) target[i] = 1f;
            motion.Step(target, 0.016);
            Check(motion.Values[0] > 0 && motion.Values[0] < 1, "wave responds immediately with interpolation");
            for (int i = 0; i < 100; i++) motion.Step(target, 0.016);
            Check(motion.Values[0] > 0.99, "wave reaches real microphone target");
            Array.Clear(target, 0, target.Length);
            motion.Step(target, 3.0);
            Check(motion.Values[0] >= 0 && motion.Values[0] <= 1, "delayed frames cannot destabilize waveform");
            for (int i = 0; i < 100; i++) motion.Step(target, 0.016);
            Check(motion.Values[0] < 0.001f, "wave settles to silence");
        }

        private static void CheckSilentNotifications()
        {
            bool called = false;
            bool sent = SilentBalloon.Show(new IntPtr(4242), 7, new string('t', 80), new string('b', 300),
                delegate(uint command, ref SilentBalloon.NotificationData data) {
                    called = true;
                    Check(command == 1 && data.Flags == 0x10, "silent balloon modifies existing notification only");
                    Check(data.InfoFlags == 0x10, "notification requests NIIF_NOSOUND with no sound-capable fallback");
                    Check(data.Window == new IntPtr(4242) && data.Id == 7, "silent notification retains the tray identity");
                    Check(data.Title.Length == 63 && data.Info.Length == 255, "notification fields fit the native string buffers");
                    Check(data.Size == 976, "notification struct matches the x64 Unicode shell ABI");
                    return true;
                });
            Check(called && sent, "silent notification passes native success through");
            Check(!SilentBalloon.Show(IntPtr.Zero, 7, "Murmur", "message", null), "missing notification identity has no fallback");
            BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Check(typeof(NotifyIcon).GetField("window", flags) != null && typeof(NotifyIcon).GetField("id", flags) != null,
                "target Framework exposes the tray identity required for silent notifications");
        }

        private static void CheckTrayItem()
        {
            TrayItem item = new TrayItem();
            Check(item.Enabled, "tray item is enabled by default");
            Check(!item.Checked && !item.IsSeparator && !item.IsHeader, "tray item flags default to false");
            Check(item.Children != null && item.Children.Count == 0, "tray item starts with an empty child list");
            Check(item.Text == null && item.OnClick == null, "tray item text and action start empty");

            bool clicked = false;
            item.Text = "Sign in";
            item.Checked = true;
            item.OnClick = delegate { clicked = true; };
            item.OnClick();
            Check(clicked, "tray item invokes its action");
            item.Children.Add(new TrayItem());
            Check(item.Children.Count == 1, "tray item keeps children");
        }

        private static void CheckOverlayState()
        {
            OverlayState state = new OverlayState();
            Check(state.Mode == OverlayMode.Hidden, "overlay state starts hidden");
            Check(!state.IsShowing, "overlay state IsShowing false when hidden");

            DateTime start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            state.Enter(OverlayMode.Recording, start);
            Check(state.Mode == OverlayMode.Recording, "overlay state tracks recording mode");
            Check(state.IsShowing, "overlay state IsShowing true when recording");
            Check(state.ShownAt == start, "overlay state tracks shown time");
            Check(!state.ShouldHide(start.AddSeconds(60.0)), "recording never auto-hides");
            Check(!state.ShouldHide(start.AddSeconds(10000.0)), "recording never auto-hides after hours");

            state.Enter(OverlayMode.Starting, start);
            Check(state.IsShowing, "overlay state IsShowing true when starting");
            Check(!state.ShouldHide(start.AddSeconds(10000.0)), "starting never auto-hides");

            state.Enter(OverlayMode.Working, start);
            Check(state.Mode == OverlayMode.Working, "overlay state tracks working mode");
            Check(state.IsShowing, "overlay state IsShowing true when working");
            Check(!state.ShouldHide(start.AddSeconds(119.9)), "working stays visible before 120 s");
            Check(state.ShouldHide(start.AddSeconds(120.0)), "working auto-hides at 120 s");
            Check(state.ShouldHide(start.AddSeconds(1000.0)), "working stays hidden after 120 s");

            state.Enter(OverlayMode.Done, start);
            Check(state.IsShowing, "overlay state IsShowing true when done");
            Check(!state.ShouldHide(start.AddSeconds(1.09)), "done stays visible before 1.1 s");
            Check(state.ShouldHide(start.AddSeconds(1.1)), "done auto-hides at 1.1 s");

            state.Enter(OverlayMode.Error, start);
            Check(state.IsShowing, "overlay state IsShowing true when error");
            Check(!state.ShouldHide(start.AddSeconds(3.49)), "error stays visible before 3.5 s");
            Check(state.ShouldHide(start.AddSeconds(3.5)), "error auto-hides at 3.5 s");

            state.Enter(OverlayMode.Hidden, start);
            Check(state.Mode == OverlayMode.Hidden, "overlay state returns to hidden");
            Check(!state.IsShowing, "overlay state IsShowing false after hide");
            Check(!state.ShouldHide(start.AddSeconds(10000.0)), "hidden never auto-hides");
        }

        private static void CheckOverlayWindowContract()
        {
            PropertyInfo modeProperty = typeof(OverlayWindow).GetProperty("Mode");
            Check(modeProperty != null && modeProperty.PropertyType == typeof(OverlayMode), "overlay window exposes Mode");
            Check(modeProperty.CanRead, "overlay window Mode is readable");
            PropertyInfo showingProperty = typeof(OverlayWindow).GetProperty("IsShowing");
            Check(showingProperty != null && showingProperty.PropertyType == typeof(bool), "overlay window exposes IsShowing");
            Check(showingProperty.CanRead, "overlay window IsShowing is readable");

            using (OverlayWindow window = new OverlayWindow())
            {
                Check(window.Mode == OverlayMode.Hidden, "overlay window starts hidden");
                Check(!window.IsShowing, "overlay window IsShowing false when hidden");
            }
        }

        private static void CheckOverlayPixelConversion()
        {
            // 2x2 BGRA frame with a 12-byte stride (four bytes of row padding). The source rows
            // are top-down; UpdateLayeredWindow expects a bottom-up DIB.
            byte[] source = new byte[]
            {
                10, 20, 30, 128, 200, 150, 100, 255, 0xEE, 0xEE, 0xEE, 0xEE,
                255, 1, 2, 0, 40, 50, 60, 64, 0xEE, 0xEE, 0xEE, 0xEE
            };
            byte[] packed = new byte[16];
            OverlayPixels.PackPremultipliedBottomUp(source, 12, 2, 2, packed);

            byte[] expected = new byte[]
            {
                0, 0, 0, 0, 10, 13, 15, 64,
                5, 10, 15, 128, 200, 150, 100, 255
            };
            bool identical = true;
            for (int index = 0; index < expected.Length; index++)
            {
                if (packed[index] != expected[index]) identical = false;
            }
            Check(identical, "pixel conversion is deterministic and bottom-up");

            Check(packed[0] == 0 && packed[1] == 0 && packed[2] == 0 && packed[3] == 0,
                "pixel conversion zeroes transparent colour");
            Check(packed[15] == 255 && packed[12] == 200 && packed[13] == 150 && packed[14] == 100,
                "pixel conversion leaves opaque pixels untouched");
            Check(packed[8] == 5 && packed[9] == 10 && packed[10] == 15 && packed[11] == 128,
                "pixel conversion premultiplies partial alpha with rounding");

            bool rejected = false;
            try
            {
                OverlayPixels.PackPremultipliedBottomUp(source, 4, 2, 2, packed);
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected = true;
            }
            Check(rejected, "pixel conversion rejects a stride narrower than a row");
        }

        private static void CheckOverlayPresenter()
        {
            FakeOverlayOps ops = new FakeOverlayOps();
            using (OverlayFramePresenter presenter = new OverlayFramePresenter(ops))
            {
                presenter.EnsureFrame(132, 36);
                Bitmap firstFrame = presenter.Frame;
                Graphics firstGraphics = presenter.FrameGraphics;
                byte[] firstRaw = presenter.RawScratch;
                byte[] firstPacked = presenter.PackedScratch;
                Check(firstFrame != null && firstGraphics != null && firstRaw != null && firstPacked != null,
                    "presenter caches a frame at the requested size");
                Check(firstPacked.Length == 132 * 36 * 4, "packed scratch matches the frame size");

                firstGraphics.Clear(Color.Red);
                firstGraphics.Clear(Color.Transparent);
                Color cleared = firstFrame.GetPixel(0, 0);
                Check(cleared.A == 0 && cleared.R == 0 && cleared.G == 0 && cleared.B == 0,
                    "reused frame clears to fully transparent pixels");

                presenter.Present(new IntPtr(1), 10, 20);
                IntPtr firstDc = presenter.MemoryDc;
                IntPtr firstDib = presenter.Dib;
                Check(firstDc != IntPtr.Zero && firstDib != IntPtr.Zero, "present creates a DC and DIB");
                Check(ops.MemoryDcCalls == 1 && ops.DibCalls == 1, "first present creates one DC and DIB");
                Check(ops.SelectCalls == 1 && ops.UpdateCalls == 1, "first present selects the DIB and updates");
                Check(ops.DibPixels != null && ops.DibPixels.Length == 132 * 36 * 4,
                    "first present writes the packed frame to the DIB");

                presenter.EnsureFrame(132, 36);
                presenter.Present(new IntPtr(1), 10, 20);
                Check(ReferenceEquals(firstFrame, presenter.Frame), "same-size render reuses the frame");
                Check(ReferenceEquals(firstGraphics, presenter.FrameGraphics), "same-size render reuses the Graphics");
                Check(ReferenceEquals(firstRaw, presenter.RawScratch), "same-size render reuses the raw scratch");
                Check(ReferenceEquals(firstPacked, presenter.PackedScratch), "same-size render reuses the packed scratch");
                Check(firstDc == presenter.MemoryDc && firstDib == presenter.Dib, "same-size present reuses the DC and DIB");
                Check(ops.MemoryDcCalls == 1 && ops.DibCalls == 1, "same-size present creates no extra GDI surfaces");

                presenter.EnsureFrame(160, 36);
                Check(!ReferenceEquals(firstFrame, presenter.Frame), "resize replaces the frame");
                Check(!ReferenceEquals(firstGraphics, presenter.FrameGraphics), "resize replaces the Graphics");
                Check(!ReferenceEquals(firstRaw, presenter.RawScratch), "resize replaces the scratch buffers");
                Check(presenter.MemoryDc == IntPtr.Zero && presenter.Dib == IntPtr.Zero, "resize releases the old DC and DIB");
                Check(ops.RestoreCalls == 1 && ops.DeleteDibCalls == 1 && ops.DeleteMemoryDcCalls == 1,
                    "resize restores and deletes the old GDI objects");

                presenter.Present(new IntPtr(1), 0, 0);
                Check(ops.MemoryDcCalls == 2 && ops.DibCalls == 2, "resize creates a new DC and DIB");
                Check(ops.UpdateCalls == 3, "resize presents with the new surface");
            }
            Check(ops.DeleteDibCalls == 2 && ops.DeleteMemoryDcCalls == 2, "dispose releases the resized DC and DIB");
            Check(ops.RestoreCalls == 2, "dispose restores the selected object");
        }

        private static void CheckOverlayRenderReuse()
        {
            FakeOverlayOps ops = new FakeOverlayOps();
            OverlayFramePresenter presenter = new OverlayFramePresenter(ops);
            using (OverlayWindow window = new OverlayWindow(presenter))
            {
                window.RenderIfShowingForTests();
                Check(presenter.Frame == null && ops.UpdateCalls == 0, "hidden overlay does not render");

                window.RenderForTests(132, 36);
                Bitmap firstFrame = presenter.Frame;
                Graphics firstGraphics = presenter.FrameGraphics;
                byte[] firstRaw = presenter.RawScratch;
                Check(firstFrame != null && firstGraphics != null && firstRaw != null,
                    "hidden render builds a reusable frame");
                Check(ops.UpdateCalls == 1 && ops.DibCalls == 1, "hidden render presents once");
                Check(ops.DibPixels != null && ops.DibPixels.Length == 132 * 36 * 4,
                    "hidden render writes the packed frame to the DIB");
                int centre = ((36 - 1 - 18) * 132 + 66) * 4;
                Check(ops.DibPixels[centre] == 0 && ops.DibPixels[centre + 1] == 0
                    && ops.DibPixels[centre + 2] == 0 && ops.DibPixels[centre + 3] == 255,
                    "rendered pill reaches the bottom-up DIB");
                int corner = (36 - 1) * 132 * 4;
                Check(ops.DibPixels[corner + 3] == 0, "rendered corner stays transparent");

                window.RenderForTests(132, 36);
                Check(ReferenceEquals(firstFrame, presenter.Frame), "same-size render path reuses the frame");
                Check(ReferenceEquals(firstGraphics, presenter.FrameGraphics), "same-size render path reuses the Graphics");
                Check(ReferenceEquals(firstRaw, presenter.RawScratch), "same-size render path reuses the scratch");
                Check(ops.DibCalls == 1 && ops.UpdateCalls == 2, "same-size render path presents without new GDI surfaces");

                window.RenderForTests(180, 36);
                Check(!ReferenceEquals(firstFrame, presenter.Frame), "resize path replaces the frame");
                Check(ops.DibCalls == 2 && ops.UpdateCalls == 3, "resize path presents with a new surface");
                Check(ops.DeleteDibCalls == 1 && ops.DeleteMemoryDcCalls == 1,
                    "resize path releases the old GDI surface");
            }
            Check(ops.DeleteDibCalls == 2 && ops.DeleteMemoryDcCalls == 2,
                "window dispose releases the cached GDI surface");
        }

        private static void CheckOverlayFaultHandling()
        {
            FakeOverlayOps ops = new FakeOverlayOps();
            ops.UpdateResult = false;
            using (OverlayWindow window = new OverlayWindow(new OverlayFramePresenter(ops)))
            {
                int faults = 0;
                string message = null;
                window.Fault += delegate(string text)
                {
                    faults++;
                    message = text;
                };

                window.StartTimerForTests();
                Check(window.TimerEnabledForTests, "overlay timer can run before a present failure");
                window.RenderForTests(132, 36);
                Check(ops.UpdateCalls == 1, "overlay reaches UpdateLayeredWindow");
                Check(faults == 1, "present failure raises Fault once");
                Check(message != null && message.Length > 0, "fault carries a diagnostic message");
                Check(!window.TimerEnabledForTests, "present failure stops the animation timer");
                Check(window.Mode == OverlayMode.Hidden && !window.IsShowing,
                    "present failure hides the overlay state");
                Check(ops.ReleaseScreenDcCalls == 1, "present failure releases the screen DC");

                window.StartTimerForTests();
                window.RenderForTests(132, 36);
                Check(ops.UpdateCalls == 2, "repeated render retries presentation");
                Check(faults == 1, "repeated present failure does not notify twice");
                Check(!window.TimerEnabledForTests, "repeated present failure stops the timer again");
            }
            Check(ops.DeleteDibCalls == 1 && ops.DeleteMemoryDcCalls == 1,
                "window dispose releases a failed present surface");
        }

        private static void CheckOverlaySurfaceFailures()
        {
            for (int scenario = 0; scenario < 3; scenario++)
            {
                FakeOverlayOps ops = new FakeOverlayOps();
                ops.ThrowDib = scenario == 0;
                ops.ZeroBits = scenario == 1;
                ops.BadSelect = scenario == 2;
                using (OverlayFramePresenter presenter = new OverlayFramePresenter(ops))
                {
                    presenter.EnsureFrame(4, 4);
                    bool threw = false;
                    try { presenter.Present(new IntPtr(1), 0, 0); }
                    catch (Exception) { threw = true; }
                    Check(threw, "invalid GDI surface is rejected " + scenario);
                }
                Check(ops.ReleaseScreenDcCalls == 1, "failed GDI allocation releases screen DC " + scenario);
                Check(ops.DeleteMemoryDcCalls == 1, "failed GDI allocation releases memory DC " + scenario);
                Check(ops.DeleteDibCalls == (scenario == 0 ? 0 : 1), "failed GDI allocation releases bitmap " + scenario);
                Check(ops.UpdateCalls == 0, "invalid surface is never presented " + scenario);
            }
        }

        private sealed class FakeOverlayOps : OverlayNativeOps
        {
            internal int ReleaseScreenDcCalls;
            internal int MemoryDcCalls;
            internal int DeleteMemoryDcCalls;
            internal int DibCalls;
            internal int DeleteDibCalls;
            internal int SelectCalls;
            internal int RestoreCalls;
            internal int UpdateCalls;
            internal bool UpdateResult = true;
            internal bool ThrowDib, ZeroBits, BadSelect;
            internal byte[] DibPixels;

            internal override IntPtr GetScreenDc()
            {
                return new IntPtr(0x1000);
            }

            internal override void ReleaseScreenDc(IntPtr screenDc)
            {
                ReleaseScreenDcCalls++;
            }

            internal override IntPtr CreateMemoryDc(IntPtr screenDc)
            {
                MemoryDcCalls++;
                return new IntPtr(0x2000);
            }

            internal override bool DeleteMemoryDc(IntPtr memoryDc)
            {
                DeleteMemoryDcCalls++;
                return true;
            }

            internal override IntPtr CreateDib(IntPtr memoryDc, int width, int height, out IntPtr bits)
            {
                DibCalls++;
                if (ThrowDib) throw new InvalidOperationException("DIB allocation failed");
                bits = ZeroBits ? IntPtr.Zero : new IntPtr(0x4000);
                return new IntPtr(0x3000);
            }

            internal override IntPtr Select(IntPtr memoryDc, IntPtr bitmap)
            {
                if (bitmap == new IntPtr(0x3000))
                {
                    SelectCalls++;
                    if (BadSelect) return new IntPtr(-1);
                    return new IntPtr(0x5000);
                }
                RestoreCalls++;
                return new IntPtr(0x5000);
            }

            internal override bool DeleteBitmap(IntPtr bitmap)
            {
                DeleteDibCalls++;
                return true;
            }

            internal override void CopyToDib(byte[] pixels, IntPtr destination)
            {
                DibPixels = (byte[])pixels.Clone();
            }

            internal override bool Update(
                IntPtr windowHandle,
                IntPtr screenDc,
                ref POINT destination,
                ref SIZE size,
                IntPtr memoryDc,
                ref POINT source,
                ref BLENDFUNCTION blend)
            {
                UpdateCalls++;
                return UpdateResult;
            }

            internal override int LastError
            {
                get { return 5; }
            }
        }

        private static List<TrayItem> BuildTwoItems()
        {
            List<TrayItem> items = new List<TrayItem>();
            TrayItem first = new TrayItem();
            first.Text = "First";
            items.Add(first);
            TrayItem second = new TrayItem();
            second.Text = "Second";
            items.Add(second);
            return items;
        }

        private static List<TrayItem> BuildThrowingItems()
        {
            throw new InvalidOperationException("menu failed");
        }

        private static void CheckTrayMenuHelper()
        {
            List<TrayItem> built;
            Check(TrayMenuHelper.TryBuildMenu(new Func<List<TrayItem>>(BuildTwoItems), out built), "tray helper builds a valid menu");
            Check(built != null && built.Count == 2, "tray helper returns built items");

            List<TrayItem> previous = BuildTwoItems();
            List<TrayItem> failed;
            Check(!TrayMenuHelper.TryBuildMenu(new Func<List<TrayItem>>(BuildThrowingItems), out failed), "tray helper reports build failure");
            Check(failed == null, "tray helper returns null on throw");
            List<TrayItem> kept = TrayMenuHelper.SafeBuild(new Func<List<TrayItem>>(BuildThrowingItems), previous);
            Check(kept == previous, "tray helper keeps previous menu when build throws");

            List<TrayItem> nullKept = TrayMenuHelper.SafeBuild(null, previous);
            Check(nullKept == previous, "tray helper keeps previous menu when builder is missing");

            Func<List<TrayItem>> nullBuilder = delegate { return null; };
            List<TrayItem> nullResult;
            Check(!TrayMenuHelper.TryBuildMenu(nullBuilder, out nullResult), "tray helper treats null menu as failure");
            Check(TrayMenuHelper.SafeBuild(nullBuilder, previous) == previous, "tray helper keeps previous menu when build returns null");

            Func<List<TrayItem>> emptyBuilder = delegate { return new List<TrayItem>(); };
            List<TrayItem> emptyResult;
            Check(TrayMenuHelper.TryBuildMenu(emptyBuilder, out emptyResult), "tray helper accepts an empty menu");
            Check(emptyResult != null && emptyResult.Count == 0, "tray helper returns the empty menu so the caller can rebuild");

            MethodInfo showMenu = typeof(TrayIcon).GetMethod("ShowMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(showMenu != null, "tray icon keeps a left-click ShowMenu path");
            MethodInfo rebuild = typeof(TrayIcon).GetMethod("RebuildMenu", BindingFlags.Instance | BindingFlags.NonPublic);
            Check(rebuild != null, "tray icon keeps a shared RebuildMenu path");
        }

        private static void CheckMenuItemFlags()
        {
            Check(TrayMenuHelper.EscapeAmpersands(null) == String.Empty, "ampersand escape handles null");
            Check(TrayMenuHelper.EscapeAmpersands("Preview & transcript") == "Preview && transcript", "ampersand is escaped so it shows literally");
            Check(TrayMenuHelper.EscapeAmpersands("a && b") == "a &&&& b", "existing escapes are preserved");
            Check(TrayMenuHelper.EscapeAmpersands("plain") == "plain", "text without ampersand is unchanged");

            using (ToolStripMenuItem plain = new ToolStripMenuItem(TrayMenuHelper.EscapeAmpersands("Preview & transcript")))
            {
                TrayMenuHelper.ConfigureItem(plain);
                Check(plain.ToolTipText == String.Empty, "menu item clears per-item tooltip text");
                Check(plain.Text == "Preview && transcript", "menu item keeps & literally via escaping");
            }

            using (ToolStripMenuItem header = new ToolStripMenuItem("Murmur"))
            {
                TrayMenuHelper.ConfigureItem(header);
                Check(header.ToolTipText == String.Empty, "menu helper clears tooltips on headers too");
            }

            using (ContextMenuStrip owner = new ContextMenuStrip())
            {
                owner.ShowItemToolTips = false;
                Check(!owner.ShowItemToolTips, "menu owner disables item tooltips");
            }
        }

        private static List<TrayItem> BuildNestedMenu()
        {
            List<TrayItem> items = new List<TrayItem>();
            TrayItem header = new TrayItem();
            header.Text = "Murmur · Ready";
            header.IsHeader = true;
            header.Enabled = false;
            items.Add(header);

            TrayItem separator = new TrayItem();
            separator.IsSeparator = true;
            items.Add(separator);

            TrayItem recent = new TrayItem();
            recent.Text = "Recent";
            TrayItem copied = new TrayItem();
            copied.Text = "Copied transcript";
            copied.OnClick = delegate { };
            recent.Children.Add(copied);
            TrayItem more = new TrayItem();
            more.Text = "More";
            TrayItem deeper = new TrayItem();
            deeper.Text = "Deeper";
            more.Children.Add(deeper);
            recent.Children.Add(more);
            items.Add(recent);

            TrayItem historyOption = new TrayItem();
            historyOption.Text = "Keep recent history";
            historyOption.Checked = true;
            items.Add(historyOption);
            return items;
        }

        private static void CheckTrayIconConstruction()
        {
            // Construct the real NotifyIcon and ContextMenuStrip with the icon hidden. The old code
            // assigned RenderMode = Custom here and threw NotSupportedException before the window
            // ever appeared, which the model-only tests above could not catch.
            using (TrayIcon tray = new TrayIcon(new Func<List<TrayItem>>(BuildNestedMenu), false))
            {
                ContextMenuStrip menu = tray.MenuForTests;
                Check(menu != null, "tray icon builds a real context menu");
                Check(!tray.NotifyVisibleForTests, "test tray icon stays hidden");
                tray.RefreshRegistration();
                Check(!tray.NotifyVisibleForTests, "registration refresh never displays a hidden test icon");
                Check(menu.Items.Count == 4, "tray icon builds root items");
                Check(menu.RenderMode == ToolStripRenderMode.Custom, "renderer assignment puts the root menu in custom mode");
                Check(menu.Renderer != null, "root menu keeps the dark renderer");
                Check(menu.Items[0] is ToolStripMenuItem && !menu.Items[0].Enabled, "header is a disabled menu item");
                Check(menu.Items[1] is ToolStripSeparator, "separator is a real separator");

                ToolStripMenuItem recent = menu.Items[2] as ToolStripMenuItem;
                Check(recent != null, "nested menu root exists");
                Check(recent.Text == "Recent" && recent.DropDownItems.Count == 2, "nested menu keeps its children");
                Check(recent.DropDown.RenderMode == ToolStripRenderMode.Custom, "nested menu is in custom mode without direct assignment");
                Check(ReferenceEquals(recent.DropDown.Renderer, menu.Renderer), "nested menu shares the root renderer");
                Check(recent.DropDown.Font != null, "nested menu keeps the menu font");

                ToolStripMenuItem more = recent.DropDownItems[1] as ToolStripMenuItem;
                Check(more != null && more.DropDownItems.Count == 1, "second-level nested menu is built");
                Check(more.DropDown.RenderMode == ToolStripRenderMode.Custom, "second-level menu is in custom mode");
                Check(ReferenceEquals(more.DropDown.Renderer, menu.Renderer), "second-level menu shares the root renderer");

                ToolStripMenuItem historyOption = menu.Items[3] as ToolStripMenuItem;
                Check(historyOption != null && historyOption.Checked, "checked state reaches the real menu item");
            }
        }
    }
}

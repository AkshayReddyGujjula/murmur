using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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

        public static int Main()
        {
            try
            {
                CheckLogoIcons();
                CheckLogoIco();
                CheckLayout();
                CheckWaveHistory();
                CheckTrayItem();
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
    }
}

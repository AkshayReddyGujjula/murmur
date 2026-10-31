using System;
using System.Drawing;

namespace Murmur.App.Ui
{
    public enum OverlayMode { Hidden, Starting, Recording, Working, Done, Error }

    public static class OverlayLayout
    {
        public static Rectangle Place(Rectangle workingArea, Size size, bool top, int margin)
        {
            int inset = margin < 0 ? 0 : margin;
            int x = workingArea.Left + (workingArea.Width - size.Width) / 2;
            int y = top
                ? workingArea.Top + inset
                : workingArea.Bottom - inset - size.Height;

            if (x < workingArea.Left) x = workingArea.Left;
            if (x + size.Width > workingArea.Right) x = Math.Max(workingArea.Left, workingArea.Right - size.Width);
            if (y < workingArea.Top) y = workingArea.Top;
            if (y + size.Height > workingArea.Bottom) y = Math.Max(workingArea.Top, workingArea.Bottom - size.Height);

            return new Rectangle(x, y, size.Width, size.Height);
        }
    }
}

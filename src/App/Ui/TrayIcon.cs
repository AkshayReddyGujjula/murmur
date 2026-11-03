using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace Murmur.App.Ui
{
    public sealed class TrayIcon : IDisposable
    {
        private const string HeaderTag = "murmur-header";

        private readonly Func<List<TrayItem>> buildMenu;
        private readonly NotifyIcon notify;
        private readonly ContextMenuStrip menu;
        private readonly Font menuFont;
        private readonly Icon idleIcon;
        private readonly Icon recordingIcon;
        private bool disposed;
        private readonly bool visibleRequested;

        public TrayIcon(Func<List<TrayItem>> buildMenu)
            : this(buildMenu, true)
        {
        }

        // The icon is hidden for offline construction tests. Assigning Renderer puts WinForms into
        // Custom render mode itself; setting RenderMode = Custom throws NotSupportedException.
        internal TrayIcon(Func<List<TrayItem>> buildMenu, bool visible)
        {
            visibleRequested = visible;
            this.buildMenu = buildMenu;
            menuFont = new Font("Segoe UI", 9.75f, FontStyle.Regular, GraphicsUnit.Point);

            int iconSize = IconSize();
            idleIcon = Logo.CreateIcon(iconSize, false);
            recordingIcon = Logo.CreateIcon(iconSize, true);

            menu = new ContextMenuStrip();
            menu.Font = menuFont;
            menu.Renderer = new DarkRenderer();
            menu.BackColor = Color.Black;
            menu.ForeColor = Color.White;
            menu.ShowCheckMargin = true;
            menu.ShowImageMargin = false;
            menu.ShowItemToolTips = false;
            menu.Opening += OnMenuOpening;

            notify = new NotifyIcon();
            notify.Icon = idleIcon;
            notify.Text = "Murmur";
            notify.ContextMenuStrip = menu;
            notify.MouseUp += OnMouseUp;

            RebuildMenu();
            notify.Visible = visible;
        }

        internal ContextMenuStrip MenuForTests
        {
            get { return menu; }
        }

        internal bool NotifyVisibleForTests
        {
            get { return notify.Visible; }
        }

        public void SetState(bool recording, string tooltip)
        {
            if (disposed) return;
            notify.Icon = recording ? recordingIcon : idleIcon;
            notify.Text = TooltipText(tooltip);
        }

        public void RefreshRegistration()
        {
            if (disposed || !visibleRequested) return;
            // Force NIM_DELETE/NIM_ADD rather than another NIM_MODIFY. WinForms
            // otherwise retains its added flag after a failed shell registration.
            notify.Visible = false;
            notify.Visible = true;
        }

        public void Notify(string title, string text)
        {
            if (disposed) return;
            string caption = String.IsNullOrEmpty(title) ? "Murmur" : title;
            if (caption.Length > 63) caption = caption.Substring(0, 63);
            string body = text == null ? String.Empty : text;
            try
            {
                notify.ShowBalloonTip(3000, caption, body, ToolTipIcon.None);
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            try
            {
                notify.Visible = false;
            }
            catch (Exception)
            {
            }
            notify.MouseUp -= OnMouseUp;
            notify.ContextMenuStrip = null;
            notify.Dispose();

            menu.Opening -= OnMenuOpening;
            menu.Dispose();
            menuFont.Dispose();
            idleIcon.Dispose();
            recordingIcon.Dispose();
        }

        private void OnMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ShowMenu();
        }

        private void OnMenuOpening(object sender, CancelEventArgs e)
        {
            RebuildMenu();
            if (menu.Items.Count == 0) e.Cancel = true;
        }

        private void ShowMenu()
        {
            if (disposed) return;
            RebuildMenu();
            if (menu.Items.Count == 0) return;
            try
            {
                MethodInfo method = null;
                MethodInfo[] methods = typeof(NotifyIcon).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic);
                for (int index = 0; index < methods.Length; index++)
                {
                    if (methods[index].Name == "ShowContextMenu" && methods[index].GetParameters().Length == 0)
                    {
                        method = methods[index];
                        break;
                    }
                }
                if (method != null)
                {
                    method.Invoke(notify, null);
                    return;
                }
            }
            catch (Exception)
            {
            }
            menu.Show(Cursor.Position);
        }

        private void RebuildMenu()
        {
            List<TrayItem> items;
            if (!TrayMenuHelper.TryBuildMenu(buildMenu, out items)) return;

            while (menu.Items.Count > 0)
            {
                ToolStripItem existing = menu.Items[0];
                menu.Items.RemoveAt(0);
                existing.Dispose();
            }

            for (int index = 0; index < items.Count; index++)
            {
                ToolStripItem item = CreateItem(items[index]);
                if (item != null) menu.Items.Add(item);
            }
        }

        private ToolStripItem CreateItem(TrayItem source)
        {
            if (source == null) return null;
            if (source.IsSeparator) return new ToolStripSeparator();

            ToolStripMenuItem item = new ToolStripMenuItem(TrayMenuHelper.EscapeAmpersands(source.Text));
            TrayMenuHelper.ConfigureItem(item);
            if (source.IsHeader)
            {
                item.Enabled = false;
                item.Tag = HeaderTag;
                item.ForeColor = Color.FromArgb(110, 110, 110);
            }
            else
            {
                item.Enabled = source.Enabled;
                item.Checked = source.Checked;
                Action action = source.OnClick;
                if (action != null) item.Click += delegate { action(); };
            }

            for (int index = 0; index < source.Children.Count; index++)
            {
                ToolStripItem child = CreateItem(source.Children[index]);
                if (child != null) item.DropDownItems.Add(child);
            }
            if (item.DropDownItems.Count > 0)
            {
                item.DropDown.Renderer = menu.Renderer;
                item.DropDown.Font = menuFont;
                item.DropDown.BackColor = Color.Black;
                item.DropDown.ForeColor = Color.White;
                item.DropDown.ShowItemToolTips = false;
            }
            return item;
        }

        private static int IconSize()
        {
            int size = 16;
            try
            {
                size = SystemInformation.SmallIconSize.Width;
            }
            catch (Exception)
            {
                size = 16;
            }
            if (size < 16) size = 16;
            if (size > 64) size = 64;
            return size;
        }

        private static string TooltipText(string tooltip)
        {
            string value = String.IsNullOrEmpty(tooltip) ? "Murmur" : tooltip;
            if (value.Length > 63) value = value.Substring(0, 63);
            return value;
        }

        private sealed class DarkColorTable : ProfessionalColorTable
        {
            private static readonly Color Black = Color.Black;
            private static readonly Color Hover = Color.FromArgb(26, 26, 26);
            private static readonly Color Line = Color.FromArgb(42, 42, 42);

            public override Color ToolStripDropDownBackground { get { return Black; } }
            public override Color ImageMarginGradientBegin { get { return Black; } }
            public override Color ImageMarginGradientMiddle { get { return Black; } }
            public override Color ImageMarginGradientEnd { get { return Black; } }
            public override Color ImageMarginRevealedGradientBegin { get { return Black; } }
            public override Color ImageMarginRevealedGradientMiddle { get { return Black; } }
            public override Color ImageMarginRevealedGradientEnd { get { return Black; } }
            public override Color MenuBorder { get { return Line; } }
            public override Color MenuItemBorder { get { return Hover; } }
            public override Color MenuItemSelected { get { return Hover; } }
            public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
            public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
            public override Color MenuItemPressedGradientBegin { get { return Hover; } }
            public override Color MenuItemPressedGradientMiddle { get { return Hover; } }
            public override Color MenuItemPressedGradientEnd { get { return Hover; } }
            public override Color SeparatorDark { get { return Line; } }
            public override Color SeparatorLight { get { return Line; } }
            public override Color CheckBackground { get { return Hover; } }
            public override Color CheckSelectedBackground { get { return Hover; } }
            public override Color CheckPressedBackground { get { return Hover; } }
            public override Color ToolStripBorder { get { return Line; } }
        }

        private sealed class DarkRenderer : ToolStripProfessionalRenderer
        {
            private static readonly Color Hover = Color.FromArgb(26, 26, 26);
            private static readonly Color Line = Color.FromArgb(42, 42, 42);
            private static readonly Color Dim = Color.FromArgb(110, 110, 110);

            public DarkRenderer() : base(new DarkColorTable())
            {
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(Color.Black))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                using (SolidBrush brush = new SolidBrush(Color.Black))
                {
                    e.Graphics.FillRectangle(brush, e.AffectedBounds);
                }
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                if (!e.Item.Selected || !e.Item.Enabled) return;
                if (IsHeader(e.Item)) return;
                int width = e.Item.Width - 4;
                if (width <= 0) return;
                Rectangle bounds = new Rectangle(2, 0, width, e.Item.Height);
                using (SolidBrush brush = new SolidBrush(Hover))
                {
                    e.Graphics.FillRectangle(brush, bounds);
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Enabled && !IsHeader(e.Item) ? Color.White : Dim;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (Pen pen = new Pen(Line, 1f))
                {
                    if (e.Vertical)
                    {
                        int x = e.Item.Width / 2;
                        e.Graphics.DrawLine(pen, x, 4, x, e.Item.Height - 4);
                    }
                    else
                    {
                        int y = e.Item.Height / 2;
                        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
                    }
                }
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                Rectangle bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                using (Pen pen = new Pen(Line, 1f))
                {
                    e.Graphics.DrawRectangle(pen, bounds);
                }
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                ToolStripMenuItem item = e.Item as ToolStripMenuItem;
                if (item == null || !item.Checked) return;

                Rectangle bounds = e.ImageRectangle;
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    bounds = new Rectangle(4, 2, Math.Max(12, e.Item.Height - 4), Math.Max(12, e.Item.Height - 4));

                float left = bounds.Left + bounds.Width * 0.22f;
                float right = bounds.Left + bounds.Width * 0.78f;
                float middle = bounds.Left + bounds.Width * 0.46f;
                float top = bounds.Top + bounds.Height * 0.36f;
                float bottom = bounds.Top + bounds.Height * 0.68f;

                using (Pen pen = new Pen(Color.White, Math.Max(1.4f, e.Item.Height / 12f)))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    PointF[] points = new PointF[]
                    {
                        new PointF(left, (top + bottom) / 2f),
                        new PointF(middle, bottom),
                        new PointF(right, top)
                    };
                    e.Graphics.DrawLines(pen, points);
                }
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item.Enabled ? Color.White : Dim;
                base.OnRenderArrow(e);
            }

            private static bool IsHeader(ToolStripItem item)
            {
                string tag = item.Tag as string;
                return tag != null && tag == HeaderTag;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace Murmur.App.Ui
{
    public static class TrayMenuHelper
    {
        public static bool TryBuildMenu(Func<List<TrayItem>> buildMenu, out List<TrayItem> items)
        {
            items = null;
            if (buildMenu == null) return false;
            List<TrayItem> next = null;
            try
            {
                next = buildMenu();
            }
            catch (Exception)
            {
                items = null;
                return false;
            }
            if (next == null) return false;
            items = next;
            return true;
        }

        public static List<TrayItem> SafeBuild(Func<List<TrayItem>> buildMenu, List<TrayItem> previous)
        {
            List<TrayItem> next;
            if (TryBuildMenu(buildMenu, out next)) return next;
            return previous;
        }

        public static string EscapeAmpersands(string text)
        {
            if (text == null) return String.Empty;
            return text.Replace("&", "&&");
        }

        public static void ConfigureItem(ToolStripMenuItem item)
        {
            if (item == null) return;
            item.ToolTipText = String.Empty;
        }
    }
}

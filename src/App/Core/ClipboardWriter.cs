using System;
using System.Windows.Forms;

namespace Murmur.App.Core
{
    public static class ClipboardWriter
    {
        public static bool TrySetText(string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(text))
            {
                error = "Nothing to copy.";
                return false;
            }
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true, 10, 60);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}

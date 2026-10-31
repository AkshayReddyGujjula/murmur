using System;
using System.Collections.Generic;

namespace Murmur.App.Ui
{
    public sealed class TrayItem
    {
        public string Text;
        public bool Checked;
        public bool Enabled = true;
        public bool IsSeparator;
        public bool IsHeader;
        public Action OnClick;
        public List<TrayItem> Children = new List<TrayItem>();
    }
}

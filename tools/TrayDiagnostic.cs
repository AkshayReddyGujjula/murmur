using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Murmur.App.Ui;

// Temporary, explicitly opt-in UI diagnostic. Default/help never creates a window
// or icon. No provider, audio, clipboard, registry, process or keyboard API exists.
internal static class TrayDiagnostic
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr Balloon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Identifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint command, ref IconData data);
    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref Identifier id, out Rect rectangle);

    private sealed class Host : NativeWindow, IDisposable
    {
        public Host()
        {
            CreateParams p = new CreateParams();
            p.Caption = "Murmur tray diagnostic";
            CreateHandle(p);
        }
        public void Dispose() { DestroyHandle(); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || args[0] != "--show")
        {
            Console.WriteLine("Tray-only diagnostic. --show requires manual-test approval; adds three labelled icons for 15 seconds, then removes them. No audio/Claude/clipboard.");
            return args.Length == 0 || (args.Length == 1 && args[0] == "--help") ? 0 : 2;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using (Host host = new Host())
        using (Icon standard = (Icon)SystemIcons.Information.Clone())
        using (Icon waveform = Logo.CreateIcon(32, false))
        using (NotifyIcon forms = new NotifyIcon())
        using (Timer report = new Timer())
        using (Timer quit = new Timer())
        {
            List<IconData> native = new List<IconData>();
            try
            {
                forms.Icon = standard;
                forms.Text = "Murmur test A: WinForms / Windows icon";
                forms.Visible = true;
                native.Add(Add(host.Handle, 41, standard, "Murmur test B: native / Windows icon"));
                native.Add(Add(host.Handle, 42, waveform, "Murmur test C: native / waveform"));
                Report(forms, native);
                report.Interval = 3000;
                report.Tick += delegate { Report(forms, native); };
                report.Start();
                quit.Interval = 15000;
                quit.Tick += delegate { Application.ExitThread(); };
                quit.Start();
                Application.Run();
                return 0;
            }
            finally
            {
                report.Stop(); quit.Stop(); forms.Visible = false;
                foreach (IconData item in native)
                {
                    IconData d = item;
                    bool removed = Shell_NotifyIcon(2, ref d);
                    Console.WriteLine("DELETE " + item.Tip + ": " + removed);
                }
                Console.WriteLine("Diagnostic finished; temporary icons removed.");
            }
        }
    }

    private static IconData Add(IntPtr window, uint id, Icon icon, string label)
    {
        IconData d = new IconData();
        d.Size = (uint)Marshal.SizeOf(typeof(IconData)); d.Window = window;
        d.Id = id; d.Flags = 1 | 2 | 4 | 32 | 128; d.Callback = 0x801;
        d.Icon = icon.Handle; d.Tip = label; d.Guid = Guid.NewGuid();
        bool added = Shell_NotifyIcon(0, ref d);
        Console.WriteLine("ADD " + label + ": " + added + "; cbSize=" + d.Size);
        if (added)
        {
            d.Version = 4;
            Console.WriteLine("VERSION " + label + ": " + Shell_NotifyIcon(4, ref d));
        }
        return d;
    }

    private static void Report(NotifyIcon forms, List<IconData> native)
    {
        BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo windowField = typeof(NotifyIcon).GetField("window", f);
        FieldInfo idField = typeof(NotifyIcon).GetField("id", f);
        if (windowField != null && idField != null)
        {
            NativeWindow window = windowField.GetValue(forms) as NativeWindow;
            if (window != null) PrintRect("A WinForms", window.Handle, Convert.ToUInt32(idField.GetValue(forms)), Guid.Empty);
        }
        foreach (IconData d in native) PrintRect(d.Tip, d.Window, d.Id, d.Guid);
    }

    private static void PrintRect(string label, IntPtr window, uint id, Guid guid)
    {
        Identifier key = new Identifier(); key.Size = (uint)Marshal.SizeOf(typeof(Identifier));
        key.Window = window; key.Id = id; key.Guid = guid;
        Rect r; int status = Shell_NotifyIconGetRect(ref key, out r);
        Console.WriteLine("RECT " + label + ": 0x" + status.ToString("X8") +
            " [" + r.Left + "," + r.Top + "," + r.Right + "," + r.Bottom + "]");
    }
}

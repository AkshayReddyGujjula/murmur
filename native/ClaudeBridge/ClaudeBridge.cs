using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using RECT = Murmur.Native.NativeMethods.RECT;
using POINT = Murmur.Native.NativeMethods.POINT;
using INPUT = Murmur.Native.NativeMethods.INPUT;

namespace Murmur.Native
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                NativeMethods.EnableDpiAwareness();
                Console.InputEncoding = new UTF8Encoding(false);
                Console.OutputEncoding = new UTF8Encoding(false);
                Bridge bridge = new Bridge();
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (string.Equals(args[i], "--restore", StringComparison.OrdinalIgnoreCase))
                        {
                            bridge.RestoreFromDisk();
                            return 0;
                        }
                        if (string.Equals(args[i], "--probe", StringComparison.OrdinalIgnoreCase))
                        {
                            bridge.PrintWindowInfo();
                            return 0;
                        }
                    }
                }
                bridge.Run();
                return 0;
            }
            catch (Exception ex)
            {
                Bridge.WriteError("fatal", ex.Message, ex.ToString());
                return 1;
            }
        }
    }

    internal sealed class Bridge
    {
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int LWA_ALPHA = 0x00000002;
        private const int SW_HIDE = 0;
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9;
        private const int SW_SHOWNA = 8;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int MK_LBUTTON = 0x0001;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const byte VK_CONTROL = 0x11;
        private const byte VK_BACK = 0x08;
        private const byte VK_ESCAPE = 0x1B;
        private const byte VK_A = 0x41;
        private const byte VK_C = 0x43;
        private const string RestoreFile = "restore-state.txt";

        private IntPtr _hwnd = IntPtr.Zero;
        private IntPtr _previousForeground = IntPtr.Zero;
        private bool _active;
        private bool _wasIconic;
        private bool _wasVisible;
        private bool _layeredAdded;
        private int _originalExStyle;
        private string _hideMode = "alpha";
        private int _originalLeft;
        private int _originalTop;
        private int _originalWidth;
        private int _originalHeight;
        private double _micX;
        private double _micY;
        private double _focusX;
        private double _focusY;
        private bool _minimizeAfter;
        private static readonly bool DebugMode = string.Equals(Environment.GetEnvironmentVariable("MURMUR_DEBUG"), "1", StringComparison.Ordinal);

        public void Run()
        {
            string line;
            while ((line = Console.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                try
                {
                    Handle(line);
                }
                catch (Exception ex)
                {
                    WriteError("command-failed", ex.Message, ex.ToString());
                }
            }
            if (_active)
            {
                SendKey(VK_ESCAPE);
                Thread.Sleep(240);
                RestoreAll();
            }
        }

        private void Handle(string line)
        {
            int space = line.IndexOf(' ');
            string command = (space < 0 ? line : line.Substring(0, space)).ToUpperInvariant();
            string rest = space < 0 ? string.Empty : line.Substring(space + 1).Trim();

            switch (command)
            {
                case "PING":
                    Write("PONG version=1");
                    break;
                case "FIND":
                    FindAndReport();
                    break;
                case "BOUNDS":
                    ReportBounds();
                    break;
                case "CURSOR":
                    ReportCursor();
                    break;
                case "FOCUS":
                    FocusClaude();
                    break;
                case "ACTIVATE":
                    ActivateHandle(rest);
                    break;
                case "CAPTURE":
                    Capture(rest);
                    break;
                case "BEGIN":
                    Begin(rest);
                    break;
                case "END":
                    End(rest);
                    break;
                case "CANCEL":
                    Cancel();
                    break;
                case "CLICKTHROUGH":
                    ClickThrough(rest == "1");
                    break;
                case "RESTORE":
                    RestoreFromDisk();
                    break;
                case "QUIT":
                    if (_active)
                    {
                        SendKey(VK_ESCAPE);
                        Thread.Sleep(240);
                        RestoreAll();
                    }
                    Write("BYE");
                    Environment.Exit(0);
                    break;
                default:
                    WriteError("unknown-command", "Unknown command: " + command, null);
                    break;
            }
        }

        private void FindAndReport()
        {
            IntPtr hwnd = FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                WriteError("no-window", "No Claude window was found. Open Claude and leave its main window running.", null);
                return;
            }
            ReportWindow(hwnd);
        }

        public void PrintWindowInfo()
        {
            IntPtr hwnd = FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                Write("NOTFOUND");
                return;
            }
            ReportWindow(hwnd);
        }

        private void ReportWindow(IntPtr hwnd)
        {
            RECT wr;
            NativeMethods.GetWindowRect(hwnd, out wr);
            RECT cr;
            NativeMethods.GetClientRect(hwnd, out cr);
            POINT origin;
            origin.X = 0;
            origin.Y = 0;
            NativeMethods.ClientToScreen(hwnd, ref origin);
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
            string process = GetProcessName(pid);
            string title = GetWindowTitle(hwnd);
            bool iconic = NativeMethods.IsIconic(hwnd);
            Write(string.Format(CultureInfo.InvariantCulture,
                "WINDOW hwnd={0} pid={1} process={2} title={3} win={4},{5},{6},{7} client={8},{9},{10},{11} minimized={12} visible={13}",
                hwnd.ToInt64(), pid, B64(process), B64(title),
                wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top,
                origin.X, origin.Y, cr.Right, cr.Bottom, iconic ? "1" : "0", NativeMethods.IsWindowVisible(hwnd) ? "1" : "0"));
        }

        private void ReportBounds()
        {
            IntPtr hwnd = FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                WriteError("no-window", "No Claude window was found.", null);
                return;
            }
            ReportWindow(hwnd);
        }

        private void ReportCursor()
        {
            POINT p;
            if (!NativeMethods.GetCursorPos(out p))
            {
                WriteError("cursor-failed", "Could not read the cursor position.", null);
                return;
            }
            Write(string.Format(CultureInfo.InvariantCulture, "CURSOR x={0} y={1}", p.X, p.Y));
        }

        private void FocusClaude()
        {
            IntPtr hwnd = FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                WriteError("no-window", "No Claude window was found.", null);
                return;
            }
            // Calibration is a temporary reveal. Save the real state so the app can
            // put Claude back exactly where it was (usually minimized) afterward.
            if (!SaveState(hwnd))
            {
                WriteError("state-write-failed", "Murmur could not save Claude's window state, so it did not reveal the window.", null);
                return;
            }
            IntPtr previous = NativeMethods.GetForegroundWindow();
            ForceForeground(hwnd);
            Thread.Sleep(250);
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (!NativeMethods.IsWindowVisible(hwnd) || foreground != hwnd)
            {
                RestoreAll();
                WriteError(
                    "not-showable",
                    "Claude's window exists, but Windows would not bring it forward. Open Claude from the taskbar or tray, then try again.",
                    null);
                return;
            }
            ReportWindow(hwnd);
            Write(string.Format(CultureInfo.InvariantCulture, "FOCUSED hwnd={0} previous={1}", hwnd.ToInt64(), previous.ToInt64()));
        }

        private void ActivateHandle(string rest)
        {
            long value;
            if (!long.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                WriteError("bad-hwnd", "Invalid window handle.", null);
                return;
            }
            IntPtr hwnd = new IntPtr(value);
            if (!NativeMethods.IsWindow(hwnd))
            {
                Write("ACTIVATED stale=1");
                return;
            }
            ForceForeground(hwnd);
            Write("ACTIVATED");
        }

        private void Capture(string rest)
        {
            string path = FromB64(rest);
            if (string.IsNullOrWhiteSpace(path))
            {
                WriteError("bad-path", "Capture path was empty.", null);
                return;
            }
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            using (Bitmap bitmap = new Bitmap(virtualScreen.Width, virtualScreen.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(virtualScreen.Left, virtualScreen.Top, 0, 0, virtualScreen.Size, CopyPixelOperation.SourceCopy);
                }
                bitmap.Save(path, ImageFormat.Png);
            }
            Write(string.Format(CultureInfo.InvariantCulture,
                "CAPTURE path={0} x={1} y={2} width={3} height={4}",
                B64(path), virtualScreen.Left, virtualScreen.Top, virtualScreen.Width, virtualScreen.Height));
        }

        private void Begin(string rest)
        {
            string[] parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                WriteError("bad-arguments", "BEGIN requires mic x/y, focus x/y, and a hide mode.", null);
                return;
            }
            _micX = ParseDouble(parts[0]);
            _micY = ParseDouble(parts[1]);
            _focusX = ParseDouble(parts[2]);
            _focusY = ParseDouble(parts[3]);
            _hideMode = parts[4].ToLowerInvariant();
            bool fresh = parts.Length > 5 && parts[5] == "1";
            _minimizeAfter = parts.Length > 6 && parts[6] == "1";

            if (_micX < 0 || _micX > 1 || _micY < 0 || _micY > 1 ||
                _focusX < 0 || _focusX > 1 || _focusY < 0 || _focusY > 1)
            {
                WriteError("bad-points", "Calibration points are outside the Claude window.", null);
                return;
            }

            IntPtr hwnd = FindClaudeWindow();
            if (hwnd == IntPtr.Zero)
            {
                WriteError("no-window", "No Claude window was found. Open Claude and leave its main window running.", null);
                return;
            }

            if (!SaveState(hwnd))
            {
                WriteError("state-write-failed", "Murmur could not save Claude's window state, so nothing was changed.", null);
                return;
            }
            try
            {
                RECT client;
                NativeMethods.GetClientRect(hwnd, out client);
                Trace(string.Format(
                    CultureInfo.InvariantCulture,
                    "begin hwnd={0} client={1}x{2} mode={3} mic={4:F4},{5:F4} focus={6:F4},{7:F4}",
                    hwnd.ToInt64(), client.Right - client.Left, client.Bottom - client.Top,
                    _hideMode, _micX, _micY, _focusX, _focusY));

                if (_hideMode == "alpha")
                {
                    if (!HideWithAlpha())
                    {
                        _hideMode = "visible";
                    }
                }
                if (!WriteRestoreFile())
                {
                    throw new InvalidOperationException("Murmur could not save the restore record; Claude was not clicked.");
                }

                ForceForeground(hwnd);
                Thread.Sleep(240);
                IntPtr foreground = NativeMethods.GetForegroundWindow();
                Trace(string.Format(
                    CultureInfo.InvariantCulture,
                    "begin foreground expected={0} actual={1} visible={2} alpha={3}",
                    hwnd.ToInt64(), foreground.ToInt64(), NativeMethods.IsWindowVisible(hwnd), _layeredAdded));
                if (!NativeMethods.IsWindowVisible(hwnd) || foreground != hwnd)
                {
                    throw new InvalidOperationException(
                        "Claude's window is hidden or Windows would not bring it forward. Open Claude from the taskbar or tray, then try again.");
                }

                if (fresh)
                {
                    ClickClient(_focusX, _focusY);
                    Thread.Sleep(80);
                    SendCtrl(VK_A);
                    Thread.Sleep(60);
                    SendKey(VK_BACK);
                    Thread.Sleep(120);
                }

                ClickClient(_focusX, _focusY);
                Thread.Sleep(120);
                ClickClient(_micX, _micY);
                Thread.Sleep(420);

                if (_hideMode == "offscreen")
                {
                    RECT wr;
                    NativeMethods.GetWindowRect(hwnd, out wr);
                    _originalLeft = wr.Left;
                    _originalTop = wr.Top;
                    _originalWidth = wr.Right - wr.Left;
                    _originalHeight = wr.Bottom - wr.Top;
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, _originalWidth, _originalHeight,
                        NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }

                _active = true;
                Write(string.Format(CultureInfo.InvariantCulture, "RECORDING hwnd={0} mode={1} mic={2:F4},{3:F4}", hwnd.ToInt64(), _hideMode, _micX, _micY));
            }
            catch
            {
                RestoreAll();
                throw;
            }
        }

        private void End(string rest)
        {
            string[] parts = rest.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                WriteError("bad-arguments", "END requires mic x/y.", null);
                return;
            }
            double micX = ParseDouble(parts[0]);
            double micY = ParseDouble(parts[1]);
            bool clearAfter = parts.Length > 2 && parts[2] == "1";

            if (!_active || _hwnd == IntPtr.Zero)
            {
                WriteError("not-active", "There is no active Murmur recording to stop.", null);
                return;
            }

            string result = null;
            try
            {
                Trace(string.Format(CultureInfo.InvariantCulture, "end start mic={0:F4},{1:F4} clear={2}", micX, micY, clearAfter));
                ForceForeground(_hwnd);
                Thread.Sleep(200);
                Trace(string.Format(
                    CultureInfo.InvariantCulture,
                    "end foreground actual={0} expected={1}",
                    NativeMethods.GetForegroundWindow().ToInt64(), _hwnd.ToInt64()));
                ClickClient(micX, micY);
                Trace("end mic click sent");
                Thread.Sleep(900);
                Write("TRANSCRIBING");
                Thread.Sleep(900);
                ClickClient(_focusX, _focusY);
                Thread.Sleep(180);
                result = CopyComposer(clearAfter);
                Trace("end copy result=" + (result == null ? "EMPTY" : "len=" + result.Length));
            }
            finally
            {
                _active = false;
                RestoreAll();
            }

            if (result == null)
            {
                Write("EMPTY");
            }
            else
            {
                Write(string.Format(CultureInfo.InvariantCulture, "TEXT text={0} chars={1}", B64(result), result.Length));
            }
        }

        private void Cancel()
        {
            if (_active && _hwnd != IntPtr.Zero)
            {
                SendKey(VK_ESCAPE);
                Thread.Sleep(260);
            }
            _active = false;
            RestoreAll();
            Write("CANCELLED");
        }

        private void ClickThrough(bool enable)
        {
            if (_hwnd == IntPtr.Zero || !NativeMethods.IsWindow(_hwnd))
            {
                WriteError("not-active", "There is no active Claude window to make click-through.", null);
                return;
            }
            int exstyle = NativeMethods.GetWindowLong(_hwnd, GWL_EXSTYLE);
            int updated = enable
                ? (exstyle | WS_EX_TRANSPARENT)
                : (exstyle & ~WS_EX_TRANSPARENT);
            NativeMethods.SetWindowLong(_hwnd, GWL_EXSTYLE, updated);
            Write("CLICKTHROUGH enabled=" + (enable ? "1" : "0"));
        }

        private static void Trace(string message)
        {
            try { Write("LOG " + B64(message)); } catch { }
        }

        private string CopyComposer(bool clearAfter)
        {
            string original = SafeGetClipboard();
            string sentinel = "MURMUR_EMPTY_" + Guid.NewGuid().ToString("N");
            bool sentinelSet = false;
            try
            {
                try
                {
                    Clipboard.SetText(sentinel);
                    sentinelSet = true;
                }
                catch
                {
                    // Clipboard can be busy. The copy loop still works, just without
                    // the extra sentinel guarantee.
                }

                string last = string.Empty;
                int stable = 0;
                string candidate = null;
                for (int i = 0; i < 12; i++)
                {
                    SendCtrl(VK_A);
                    Thread.Sleep(45);
                    SendCtrl(VK_C);
                    Thread.Sleep(430);
                    string text = SafeGetClipboard();
                    Trace(string.Format(
                        CultureInfo.InvariantCulture,
                        "copy {0}: len={1} sentinel={2} empty={3} whitespace={4}",
                        i, text.Length, text == sentinel, text.Length == 0,
                        text.Trim().Length == 0));
                    if (text == sentinel || text.Trim().Length == 0)
                    {
                        stable = 0;
                        continue;
                    }
                    if (text == last && text.Trim().Length > 0)
                    {
                        stable++;
                        candidate = text;
                        if (stable >= 1) break;
                    }
                    else
                    {
                        stable = 0;
                        last = text;
                        candidate = text;
                    }
                }

                if (clearAfter && candidate != null && candidate.Trim().Length > 0)
                {
                    SendCtrl(VK_A);
                    Thread.Sleep(60);
                    SendKey(VK_BACK);
                    Thread.Sleep(120);
                }

                if (candidate != null && candidate.Trim().Length > 0)
                {
                    return candidate;
                }

                if (sentinelSet)
                {
                    if (original != null && original.Length > 0)
                    {
                        try { Clipboard.SetText(original); } catch { }
                    }
                    else
                    {
                        try { Clipboard.Clear(); } catch { }
                    }
                }
                return null;
            }
            catch
            {
                if (sentinelSet)
                {
                    try
                    {
                        if (original != null && original.Length > 0) Clipboard.SetText(original);
                        else Clipboard.Clear();
                    }
                    catch { }
                }
                throw;
            }
        }

        private static string SafeGetClipboard()
        {
            for (int i = 0; i < 3; i++)
            {
                try { return Clipboard.GetText(); }
                catch { Thread.Sleep(60); }
            }
            return string.Empty;
        }

        private bool HideWithAlpha()
        {
            _originalExStyle = NativeMethods.GetWindowLong(_hwnd, GWL_EXSTYLE);
            int style = _originalExStyle;
            if ((style & WS_EX_LAYERED) == 0)
            {
                style |= WS_EX_LAYERED;
                NativeMethods.SetWindowLong(_hwnd, GWL_EXSTYLE, style);
                _layeredAdded = true;
            }
            bool ok = NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);
            if (!ok && _layeredAdded)
            {
                NativeMethods.SetWindowLong(_hwnd, GWL_EXSTYLE, _originalExStyle);
                _layeredAdded = false;
            }
            Log("alpha hide=" + ok + " layeredAdded=" + _layeredAdded);
            return ok;
        }

        private bool SaveState(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _previousForeground = NativeMethods.GetForegroundWindow();
            _wasIconic = NativeMethods.IsIconic(hwnd);
            _wasVisible = NativeMethods.IsWindowVisible(hwnd);
            _originalExStyle = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
            _layeredAdded = false;
            RECT wr;
            NativeMethods.GetWindowRect(hwnd, out wr);
            _originalLeft = wr.Left;
            _originalTop = wr.Top;
            _originalWidth = wr.Right - wr.Left;
            _originalHeight = wr.Bottom - wr.Top;
            return WriteRestoreFile();
        }

        private bool WriteRestoreFile()
        {
            try
            {
                string dir = StateDirectory();
                Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("hwnd=" + _hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("previous=" + _previousForeground.ToInt64().ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("exstyle=" + _originalExStyle.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("iconic=" + (_wasIconic ? "1" : "0"));
                sb.AppendLine("visible=" + (_wasVisible ? "1" : "0"));
                sb.AppendLine("layered=" + (_layeredAdded ? "1" : "0"));
                sb.AppendLine("mode=" + _hideMode);
                sb.AppendLine("left=" + _originalLeft.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("top=" + _originalTop.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("width=" + _originalWidth.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("height=" + _originalHeight.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("minimize=" + (_minimizeAfter ? "1" : "0"));
                File.WriteAllText(Path.Combine(dir, RestoreFile), sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log("write restore failed: " + ex.Message);
                return false;
            }
        }

        private void RestoreAll()
        {
            try
            {
                RestoreWindow(_hwnd, _previousForeground, _originalExStyle, _wasIconic, _wasVisible, _layeredAdded, _hideMode, _minimizeAfter);
            }
            catch (Exception ex)
            {
                Log("restore failed: " + ex.Message);
            }
            finally
            {
                DeleteRestoreFile();
                _hwnd = IntPtr.Zero;
                _active = false;
            }
        }

        public void RestoreFromDisk()
        {
            try
            {
                string path = Path.Combine(StateDirectory(), RestoreFile);
                if (!File.Exists(path))
                {
                    Write("RESTORED stale=1");
                    return;
                }
                Dictionary<string, string> values = ReadKeyValues(path);
                IntPtr hwnd = new IntPtr(Long(values, "hwnd"));
                IntPtr previous = new IntPtr(Long(values, "previous"));
                int exstyle = (int)Long(values, "exstyle");
                bool iconic = values.ContainsKey("iconic") && values["iconic"] == "1";
                bool visible = values.ContainsKey("visible") && values["visible"] == "1";
                bool layered = values.ContainsKey("layered") && values["layered"] == "1";
                string mode = values.ContainsKey("mode") ? values["mode"] : "alpha";
                bool minimize = values.ContainsKey("minimize") && values["minimize"] == "1";
                _originalLeft = (int)Long(values, "left");
                _originalTop = (int)Long(values, "top");
                _originalWidth = (int)Long(values, "width");
                _originalHeight = (int)Long(values, "height");
                RestoreWindow(hwnd, previous, exstyle, iconic, visible, layered, mode, minimize);
                DeleteRestoreFile();
                Write("RESTORED");
            }
            catch (Exception ex)
            {
                WriteError("restore-failed", ex.Message, ex.ToString());
            }
        }

        private void RestoreWindow(IntPtr hwnd, IntPtr previousForeground, int originalExStyle, bool wasIconic, bool wasVisible, bool layeredAdded, string hideMode, bool minimizeAfter)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;

            if (hideMode == "offscreen")
            {
                RECT wr;
                NativeMethods.GetWindowRect(hwnd, out wr);
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, _originalLeft, _originalTop, wr.Right - wr.Left, wr.Bottom - wr.Top,
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            }

            bool willMinimize = wasIconic || minimizeAfter;
            if (willMinimize)
            {
                NativeMethods.ShowWindow(hwnd, SW_MINIMIZE);
            }
            else if (!wasVisible)
            {
                NativeMethods.ShowWindow(hwnd, SW_HIDE);
            }

            if (previousForeground != IntPtr.Zero && previousForeground != hwnd && NativeMethods.IsWindow(previousForeground))
            {
                Thread.Sleep(40);
                NativeMethods.SetForegroundWindow(previousForeground);
            }

            if (layeredAdded)
            {
                NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            }
            NativeMethods.SetWindowLong(hwnd, GWL_EXSTYLE, originalExStyle);

            if (!willMinimize && wasVisible)
            {
                NativeMethods.ShowWindow(hwnd, SW_SHOWNA);
            }
        }

        private void ForceForeground(IntPtr hwnd)
        {
            if (NativeMethods.IsIconic(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, SW_RESTORE);
            }
            else if (!NativeMethods.IsWindowVisible(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, SW_SHOWNA);
            }

            IntPtr foreground = NativeMethods.GetForegroundWindow();
            uint ignoredProcessId = 0;
            uint foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out ignoredProcessId);
            uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out ignoredProcessId);
            bool attached = false;
            try
            {
                if (foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread)
                {
                    attached = NativeMethods.AttachThreadInput(foregroundThread, targetThread, true);
                }
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    NativeMethods.AttachThreadInput(foregroundThread, targetThread, false);
                }
            }
        }

        private void ClickClient(double nx, double ny)
        {
            if (_hideMode == "offscreen")
            {
                PostClientClick(nx, ny);
                return;
            }

            RECT cr;
            if (!NativeMethods.GetClientRect(_hwnd, out cr)) return;
            POINT origin;
            origin.X = 0;
            origin.Y = 0;
            NativeMethods.ClientToScreen(_hwnd, ref origin);
            int x = origin.X + (int)Math.Round(nx * Math.Max(1, cr.Right - cr.Left));
            int y = origin.Y + (int)Math.Round(ny * Math.Max(1, cr.Bottom - cr.Top));
            Trace(string.Format(
                CultureInfo.InvariantCulture,
                "click {0:F4},{1:F4} client={2}x{3} origin={4},{5} screen={6},{7}",
                nx, ny, cr.Right - cr.Left, cr.Bottom - cr.Top, origin.X, origin.Y, x, y));

            POINT previous;
            NativeMethods.GetCursorPos(out previous);
            NativeMethods.SetCursorPos(x, y);
            Thread.Sleep(30);
            INPUT[] inputs = new INPUT[2];
            inputs[0].type = 0;
            inputs[0].mi.dwFlags = MOUSEEVENTF_LEFTDOWN;
            inputs[1].type = 0;
            inputs[1].mi.dwFlags = MOUSEEVENTF_LEFTUP;
            NativeMethods.SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            Thread.Sleep(35);
            NativeMethods.SetCursorPos(previous.X, previous.Y);
        }

        private void PostClientClick(double nx, double ny)
        {
            RECT cr;
            if (!NativeMethods.GetClientRect(_hwnd, out cr)) return;
            int x = (int)Math.Round(nx * Math.Max(1, cr.Right - cr.Left));
            int y = (int)Math.Round(ny * Math.Max(1, cr.Bottom - cr.Top));
            IntPtr lparam = new IntPtr((y << 16) | (x & 0xFFFF));
            NativeMethods.PostMessage(_hwnd, WM_LBUTTONDOWN, new IntPtr(MK_LBUTTON), lparam);
            Thread.Sleep(40);
            NativeMethods.PostMessage(_hwnd, WM_LBUTTONUP, IntPtr.Zero, lparam);
        }

        private static void SendCtrl(byte key)
        {
            NativeMethods.keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            Thread.Sleep(18);
            NativeMethods.keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(24);
            NativeMethods.keybd_event(key, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(18);
            NativeMethods.keybd_event(VK_CONTROL, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(30);
        }

        private static void SendKey(byte key)
        {
            NativeMethods.keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(24);
            NativeMethods.keybd_event(key, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(40);
        }

        private static IntPtr FindClaudeWindow()
        {
            List<IntPtr> handles = new List<IntPtr>();
            NativeMethods.EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
            {
                handles.Add(hwnd);
                return true;
            }, IntPtr.Zero);

            IntPtr best = IntPtr.Zero;
            long bestScore = -1;
            foreach (IntPtr hwnd in handles)
            {
                int exstyle = NativeMethods.GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exstyle & WS_EX_TOOLWINDOW) != 0) continue;
                string className = GetClassName(hwnd);
                if (!string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal)) continue;
                string title = GetWindowTitle(hwnd);
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
                string process = GetProcessName(pid);
                if (!string.Equals(process, "claude.exe", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(process, "claude", StringComparison.OrdinalIgnoreCase)) continue;

                RECT wr;
                NativeMethods.GetWindowRect(hwnd, out wr);
                long width = wr.Right - wr.Left;
                long height = wr.Bottom - wr.Top;
                long area = width * height;
                // Ignore small popovers, toasts and the tiny minimized quick surface.
                // The real composer needs enough room for the calibrated mic point.
                if (width < 500 || height < 380 || area < 240000) continue;

                bool visible = NativeMethods.IsWindowVisible(hwnd);
                bool iconic = NativeMethods.IsIconic(hwnd);
                long score = area;
                if (visible) score += 5000000;
                if (!iconic) score += 10000000;
                if (title.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0) score += 250000;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hwnd;
                }
            }
            return best;
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetClassName(IntPtr hwnd)
        {
            StringBuilder sb = new StringBuilder(256);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetProcessName(uint pid)
        {
            IntPtr handle = NativeMethods.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle != IntPtr.Zero)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size))
                    {
                        return Path.GetFileName(sb.ToString());
                    }
                }
                finally
                {
                    NativeMethods.CloseHandle(handle);
                }
            }
            try
            {
                Process process = Process.GetProcessById((int)pid);
                return process.ProcessName + ".exe";
            }
            catch
            {
                return string.Empty;
            }
        }

        private static double ParseDouble(string value)
        {
            double result;
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                throw new InvalidOperationException("Invalid number: " + value);
            }
            return result;
        }

        private static string B64(string value)
        {
            if (value == null) value = string.Empty;
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        private static string FromB64(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value));
            }
            catch
            {
                return value;
            }
        }

        private static string StateDirectory()
        {
            string custom = Environment.GetEnvironmentVariable("MURMUR_LOCAL_DIR");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "Murmur");
        }

        private static Dictionary<string, string> ReadKeyValues(string path)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                int index = line.IndexOf('=');
                if (index <= 0) continue;
                result[line.Substring(0, index)] = line.Substring(index + 1);
            }
            return result;
        }

        private static long Long(Dictionary<string, string> values, string key)
        {
            string value;
            if (!values.TryGetValue(key, out value)) return 0;
            long parsed;
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
            return parsed;
        }

        private static void DeleteRestoreFile()
        {
            try
            {
                string path = Path.Combine(StateDirectory(), RestoreFile);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        private static void Log(string message)
        {
            if (!DebugMode) return;
            try { Console.Error.WriteLine("[bridge] " + message); } catch { }
        }

        internal static void Write(string line)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }

        internal static void WriteError(string code, string message, string detail)
        {
            Write(string.Format(CultureInfo.InvariantCulture, "ERROR code={0} message={1} detail={2}",
                B64(code), B64(message), B64(detail ?? string.Empty)));
        }
    }

    internal static class NativeMethods
    {
        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint KEYEVENTF_KEYUP = 0x0002;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(4)] public MOUSEINPUT mi;
            [FieldOffset(4)] public KEYBDINPUT ki;
            [FieldOffset(4)] public HARDWAREINPUT hi;
        }

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        public static void EnableDpiAwareness()
        {
            try
            {
                // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return;
            }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }
    }
}

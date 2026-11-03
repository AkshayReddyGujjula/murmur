using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Murmur.App.Core;

namespace Murmur.Tests.App
{
    public static class CoreTests
    {
        private static void Check(bool ok, string name)
        {
            if (!ok)
            {
                throw new Exception(name);
            }
        }

        private static int pressedCount;
        private static int cancelCount;

        public static int Main()
        {
            try
            {
                string baseDir = Path.Combine(Path.GetTempPath(), "murmur-core-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(baseDir);
                TestSettings(baseDir);
                TestHistory(baseDir);
                TestLog(baseDir);
                TestDescribeLabel();
                TestCommandFor();
                Console.WriteLine("PASS: core settings, history, log, hotkey describe, autostart command.");
                TestInputSize();
                TestAutoPasteGuard();
                TestAutoPasteDelivery();
                TestDeleteFile(baseDir);
                TestHotkeyBothIds();
                Console.WriteLine("PASS: core autopaste input size, guarded target validation, history deletefile, hotkey dual ids.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAIL: " + error.Message);
                return 1;
            }
        }

        private static void TestSettings(string baseDir)
        {
            string path = Path.Combine(baseDir, "settings.ini");
            AppSettings settings = new AppSettings();
            settings.Hotkey = HotkeyChoice.CtrlAltSpace;
            settings.Engine = EngineMode.OnDemand;
            settings.IdleUnloadMinutes = 42;
            settings.Placement = OverlayPlacement.TopCenter;
            settings.KeepHistory = false;
            settings.HistoryLimit = 77;
            settings.AutoPaste = true;
            settings.PasteInto = PasteDestination.OriginalWindow;
            settings.EscapeCancels = false;
            settings.Save(path);
            Check(File.Exists(path), "settings file created");
            Check(!File.Exists(path + ".tmp"), "settings temp cleaned");
            AppSettings loaded = AppSettings.Load(path);
            Check(loaded.Hotkey == HotkeyChoice.CtrlAltSpace, "settings hotkey roundtrip");
            Check(loaded.Engine == EngineMode.OnDemand, "settings engine roundtrip");
            Check(loaded.IdleUnloadMinutes == 42, "settings idle roundtrip");
            Check(loaded.Placement == OverlayPlacement.TopCenter, "settings placement roundtrip");
            Check(loaded.KeepHistory == false, "settings keephistory roundtrip");
            Check(loaded.HistoryLimit == 77, "settings historylimit roundtrip");
            Check(loaded.AutoPaste == true, "settings autopaste roundtrip");
            Check(loaded.PasteInto == PasteDestination.OriginalWindow, "paste destination survives restart");
            Check(loaded.EscapeCancels == false, "settings escape roundtrip");

            AppSettings defaults = new AppSettings();
            Check(defaults.Hotkey == HotkeyChoice.CtrlSpace, "settings default hotkey");
            Check(defaults.Engine == EngineMode.AlwaysReady, "settings default engine");
            Check(defaults.IdleUnloadMinutes == 15, "settings default idle");
            Check(defaults.Placement == OverlayPlacement.BottomCenter, "settings default placement");
            Check(defaults.KeepHistory == true, "settings default keephistory");
            Check(defaults.HistoryLimit == 25, "settings default historylimit");
            Check(defaults.AutoPaste == false, "settings default autopaste");
            Check(defaults.PasteInto == PasteDestination.CurrentWindow, "paste destination defaults to completion focus");
            Check(defaults.EscapeCancels == true, "settings default escape");

            string corruptPath = Path.Combine(baseDir, "corrupt.ini");
            File.WriteAllText(corruptPath, "garbage line\nHotkey=!!!\nIdleUnloadMinutes=abc\n", new UTF8Encoding(false));
            AppSettings corrupt = AppSettings.Load(corruptPath);
            Check(corrupt.Hotkey == HotkeyChoice.CtrlSpace, "settings corrupt hotkey defaults");
            Check(corrupt.IdleUnloadMinutes == 15, "settings corrupt idle defaults");
            Check(corrupt.HistoryLimit == 25, "settings corrupt limit defaults");

            string missingPath = Path.Combine(baseDir, "missing-" + Guid.NewGuid().ToString("N") + ".ini");
            AppSettings missing = AppSettings.Load(missingPath);
            Check(missing.Hotkey == HotkeyChoice.CtrlSpace, "settings missing defaults");
            Check(missing.HistoryLimit == 25, "settings missing limit defaults");

            AppSettings nullLoad = AppSettings.Load(null);
            Check(nullLoad.Hotkey == HotkeyChoice.CtrlSpace, "settings null path defaults");

            string unknownPath = Path.Combine(baseDir, "unknown.ini");
            File.WriteAllText(unknownPath, "UnknownKey=123\nHotkey=CtrlSpace\nSounds=true\nFutureKey=abc\n", new UTF8Encoding(false));
            AppSettings unknown = AppSettings.Load(unknownPath);
            Check(unknown.Hotkey == HotkeyChoice.CtrlSpace, "settings unknown keys ignored");
            unknown.Save(unknownPath);
            Check(!File.ReadAllText(unknownPath).Contains("Sounds="), "legacy sound setting is ignored and removed on save");

            string clampPath = Path.Combine(baseDir, "clamp.ini");
            File.WriteAllText(clampPath, "IdleUnloadMinutes=0\nHistoryLimit=9999\n", new UTF8Encoding(false));
            AppSettings clamped = AppSettings.Load(clampPath);
            Check(clamped.IdleUnloadMinutes == 1, "settings clamp low idle");
            Check(clamped.HistoryLimit == 200, "settings clamp high history");

            File.WriteAllText(clampPath, "IdleUnloadMinutes=9999\nHistoryLimit=0\n", new UTF8Encoding(false));
            AppSettings clamped2 = AppSettings.Load(clampPath);
            Check(clamped2.IdleUnloadMinutes == 240, "settings clamp high idle");
            Check(clamped2.HistoryLimit == 1, "settings clamp low history");
        }

        private static void TestHistory(string baseDir)
        {
            string path = Path.Combine(baseDir, "history.dat");
            TranscriptHistory history = new TranscriptHistory(path, 3);
            Check(history.Items.Count == 0, "history empty start");
            Check(history.Limit == 3, "history limit ctor");
            history.Add("");
            Check(history.Items.Count == 0, "history ignore empty");
            history.Add("   ");
            Check(history.Items.Count == 0, "history ignore whitespace");
            history.Add(null);
            Check(history.Items.Count == 0, "history ignore null");
            history.Add("first");
            history.Add("second");
            history.Add("third");
            Check(history.Items.Count == 3, "history add 3");
            Check(history.Items[0].Text == "third", "history newest first");
            Check(history.Items[2].Text == "first", "history oldest last");
            history.Add("fourth");
            Check(history.Items.Count == 3, "history limit trims");
            Check(history.Items[0].Text == "fourth", "history newest after trim");
            Check(history.Items[2].Text == "second", "history oldest evicted");
            history.Limit = 2;
            Check(history.Items.Count == 2, "history limit setter trims");
            Check(history.Items[0].Text == "fourth", "history limit setter newest");
            Check(history.Limit == 2, "history limit getter");

            string persistPath = Path.Combine(baseDir, "history2.dat");
            TranscriptHistory a = new TranscriptHistory(persistPath, 10);
            a.Add("hello persist");
            a.Add("second persist");
            Check(File.Exists(persistPath), "history file persisted");
            byte[] raw = File.ReadAllBytes(persistPath);
            string decoded = Encoding.UTF8.GetString(raw);
            Check(decoded.IndexOf("hello persist") < 0, "history file encrypted");
            TranscriptHistory b = new TranscriptHistory(persistPath, 10);
            Check(b.Items.Count == 2, "history persist count");
            Check(b.Items[0].Text == "second persist", "history persist newest");
            Check(b.Items[1].Text == "hello persist", "history persist oldest");

            a.Clear();
            Check(a.Items.Count == 0, "history clear count");
            Check(!File.Exists(persistPath), "history clear deletes file");
            TranscriptHistory c = new TranscriptHistory(persistPath, 10);
            Check(c.Items.Count == 0, "history clear persists");

            File.WriteAllBytes(persistPath, new byte[] { 1, 2, 3, 4, 5 });
            TranscriptHistory d = new TranscriptHistory(persistPath, 10);
            Check(d.Items.Count == 0, "history corrupt empty");
            d.Add("after corrupt");
            Check(d.Items.Count == 1, "history add after corrupt");
            Check(d.Items[0].Text == "after corrupt", "history text after corrupt");
        }

        private static void TestLog(string baseDir)
        {
            string logPath = Path.Combine(baseDir, "app.log");
            AppLog log = new AppLog(logPath);
            log.Info("hello log");
            Check(File.Exists(logPath), "log file created");
            string content = File.ReadAllText(logPath, new UTF8Encoding(false));
            Check(content.IndexOf("INFO") >= 0, "log info level");
            Check(content.IndexOf("hello log") >= 0, "log info message");

            log.Info("line1\nline2\rline3");
            string content2 = File.ReadAllText(logPath, new UTF8Encoding(false));
            Check(content2.IndexOf("line1 line2 line3") >= 0, "log single line");

            log.Error("oops", new InvalidOperationException("bad thing"));
            string content3 = File.ReadAllText(logPath, new UTF8Encoding(false));
            Check(content3.IndexOf("ERROR") >= 0, "log error level");
            Check(content3.IndexOf("oops") >= 0, "log error message");
            Check(content3.IndexOf("bad thing") >= 0, "log error detail");

            string rollPath = Path.Combine(baseDir, "roll.log");
            string rollOld = Path.Combine(baseDir, "roll.old.log");
            string filler = new string('a', 600 * 1024);
            File.WriteAllText(rollPath, filler, new UTF8Encoding(false));
            AppLog rollLog = new AppLog(rollPath);
            rollLog.Info("rolled message");
            Check(File.Exists(rollOld), "log roll creates old log");
            Check(File.Exists(rollPath), "log roll new log exists");
            string newContent = File.ReadAllText(rollPath, new UTF8Encoding(false));
            Check(newContent.IndexOf("rolled message") >= 0, "log rolled message");
            Check(newContent.IndexOf("INFO") >= 0, "log rolled level");
        }

        private static void TestDescribeLabel()
        {
            uint modifiers;
            uint key;
            GlobalHotkeys.Describe(HotkeyChoice.CtrlSpace, out modifiers, out key);
            Check(modifiers == 0x0002 && key == 0x20, "describe ctrlspace");
            GlobalHotkeys.Describe(HotkeyChoice.CtrlShiftSpace, out modifiers, out key);
            Check(modifiers == (0x0002 | 0x0004) && key == 0x20, "describe ctrlshiftspace");
            GlobalHotkeys.Describe(HotkeyChoice.CtrlAltSpace, out modifiers, out key);
            Check(modifiers == (0x0002 | 0x0001) && key == 0x20, "describe ctrlaltspace");
            GlobalHotkeys.Describe(HotkeyChoice.AltShiftSpace, out modifiers, out key);
            Check(modifiers == (0x0001 | 0x0004) && key == 0x20, "describe altshiftspace");
            Check(GlobalHotkeys.Label(HotkeyChoice.CtrlSpace) == "Ctrl+Space", "label ctrlspace");
            Check(GlobalHotkeys.Label(HotkeyChoice.CtrlShiftSpace) == "Ctrl+Shift+Space", "label ctrlshiftspace");
            Check(GlobalHotkeys.Label(HotkeyChoice.CtrlAltSpace) == "Ctrl+Alt+Space", "label ctrlaltspace");
            Check(GlobalHotkeys.Label(HotkeyChoice.AltShiftSpace) == "Alt+Shift+Space", "label altshiftspace");
        }

        private static void TestCommandFor()
        {
            string command = Autostart.CommandFor("C:\\Tools\\Murmur.exe");
            Check(command == "\"C:\\Tools\\Murmur.exe\" --background", "autostart command quotes path and launches quietly");
            string empty = Autostart.CommandFor("");
            Check(empty == "\"\"", "commandfor empty");
        }

        private static void TestInputSize()
        {
            if (IntPtr.Size == 8)
            {
                Check(AutoPaste.InputSize == 40, "autopaste input size 40 on x64");
            }
            else
            {
                Check(AutoPaste.InputSize >= 28, "autopaste input size sane on x86");
            }
            System.Reflection.MethodInfo trySend = typeof(AutoPaste).GetMethod("TrySendCtrlV");
            Check(trySend != null, "autopaste trysend exists");
            Check(trySend.ReturnType == typeof(bool), "autopaste trysend returns bool");
            System.Reflection.ParameterInfo[] pars = trySend.GetParameters();
            Check(pars.Length == 1, "autopaste trysend one param");
            System.Reflection.MethodInfo send = typeof(AutoPaste).GetMethod("SendCtrlV");
            Check(send != null, "autopaste sendctrlv kept");
            Check(send.ReturnType == typeof(void), "autopaste sendctrlv void");
        }

        private static void TestAutoPasteGuard()
        {
            IntPtr window = new IntPtr(4242);
            AutoPasteTarget target = new AutoPasteTarget(window, 7);
            Check(target.IsCaptured, "autopaste captured target");
            Check(!new AutoPasteTarget(IntPtr.Zero, 7).IsCaptured, "autopaste zero hwnd not captured");

            string error;
            Check(AutoPaste.ValidateTarget(target, window, 7, false, out error), "autopaste valid snapshot accepted");
            Check(error == null, "autopaste valid snapshot no error");

            Check(!AutoPaste.ValidateTarget(new AutoPasteTarget(IntPtr.Zero, 7), IntPtr.Zero, 7, false, out error), "autopaste zero hwnd rejected");
            Check(error != null, "autopaste zero hwnd error");
            Check(!AutoPaste.ValidateTarget(target, new IntPtr(99), 7, false, out error), "autopaste changed window rejected");
            Check(error != null, "autopaste changed window error");
            Check(!AutoPaste.ValidateTarget(target, window, 8, false, out error), "autopaste changed clipboard rejected");
            Check(error != null, "autopaste changed clipboard error");
            Check(!AutoPaste.ValidateTarget(target, window, 7, true, out error), "autopaste held modifier rejected");
            Check(error != null, "autopaste held modifier error");

            bool injected;
            Check(RunGuardedAutoPaste(target, window, 7, false, out injected, out error), "autopaste guard delegate accepts valid snapshot");
            Check(injected, "autopaste guard delegate injects once");
            Check(error == null, "autopaste guard delegate valid no error");

            Check(!RunGuardedAutoPaste(new AutoPasteTarget(IntPtr.Zero, 7), IntPtr.Zero, 7, false, out injected, out error), "autopaste guard delegate zero hwnd");
            Check(!injected, "autopaste guard delegate zero hwnd no inject");
            Check(!RunGuardedAutoPaste(target, new IntPtr(99), 7, false, out injected, out error), "autopaste guard delegate changed window");
            Check(!injected, "autopaste guard delegate changed window no inject");
            Check(!RunGuardedAutoPaste(target, window, 8, false, out injected, out error), "autopaste guard delegate changed clipboard");
            Check(!injected, "autopaste guard delegate changed clipboard no inject");
            Check(!RunGuardedAutoPaste(target, window, 7, true, out injected, out error), "autopaste guard delegate held modifier");
            Check(!injected, "autopaste guard delegate held modifier no inject");

            bool injectorCalled = false;
            bool sent = AutoPaste.TrySendToTarget(
                target,
                delegate { return window; },
                delegate { return 7u; },
                delegate { return false; },
                delegate(out string injectError)
                {
                    injectorCalled = true;
                    injectError = "inject failed";
                    return false;
                },
                out error);
            Check(!sent, "autopaste injector failure returns false");
            Check(injectorCalled, "autopaste injector failure reached injector");
            Check(error == "inject failed", "autopaste injector failure error kept");

            injectorCalled = false;
            sent = AutoPaste.TrySendToTarget(
                target,
                ThrowForeground,
                delegate { return 7u; },
                delegate { return false; },
                delegate(out string injectError)
                {
                    injectorCalled = true;
                    injectError = null;
                    return true;
                },
                out error);
            Check(!sent, "autopaste provider failure returns false");
            Check(!injectorCalled, "autopaste provider failure no inject");
            Check(error != null, "autopaste provider failure error");

            Check(!AutoPaste.TrySendToTarget(target, null, null, null, null, out error), "autopaste guard null providers rejected");
            Check(error != null, "autopaste guard null providers error");

            System.Reflection.MethodInfo capture = typeof(AutoPaste).GetMethod("CaptureTarget");
            Check(capture != null, "autopaste capture target exists");
            Check(capture.ReturnType == typeof(AutoPasteTarget), "autopaste capture target returns target");
            Check(capture.GetParameters().Length == 0, "autopaste capture target takes no args");
            System.Reflection.MethodInfo guarded = typeof(AutoPaste).GetMethod(
                "TrySendToTarget",
                new Type[] { typeof(AutoPasteTarget), typeof(string).MakeByRefType() });
            Check(guarded != null, "autopaste guarded send exists");
            Check(guarded.ReturnType == typeof(bool), "autopaste guarded send returns bool");
        }

        private static void TestAutoPasteDelivery()
        {
            IntPtr original = new IntPtr(10), current = new IntPtr(20);
            AutoPasteTarget snapshot = new AutoPasteTarget(current, 77);
            AutoPasteTarget selected = AutoPaste.SelectTarget(PasteDestination.CurrentWindow, original, snapshot);
            Check(selected.WindowHandle == current && selected.ClipboardSequence == 77, "completion focus accepts switching windows during dictation");
            selected = AutoPaste.SelectTarget(PasteDestination.OriginalWindow, original, snapshot);
            Check(selected.WindowHandle == original && selected.ClipboardSequence == 77, "original window policy retains original target and new clipboard");

            AutoPasteDelivery delivery = new AutoPasteDelivery();
            int sends = 0;
            bool held = true;
            AutoPaste.Injector guarded = delegate(out string message) {
                return AutoPaste.TrySendToTarget(snapshot, delegate { return current; }, delegate { return 77u; }, delegate { return held; },
                    delegate(out string failure) { sends++; failure = null; return true; }, out message);
            };
            string error;
            Check(delivery.Attempt(0, guarded, out error) == PasteProgress.WaitingForKeys && sends == 0, "held Ctrl waits instead of dropping paste");
            held = false;
            Check(delivery.Attempt(25, guarded, out error) == PasteProgress.Sent && sends == 1, "paste sends as soon as modifier released");
            Check(delivery.Attempt(50, guarded, out error) == PasteProgress.Skipped && sends == 1, "completed paste never sends twice");

            delivery = new AutoPasteDelivery();
            held = true;
            Check(delivery.Attempt(1000, guarded, out error) == PasteProgress.Skipped && sends == 1, "held-key retry is bounded");
            held = false;
            Check(delivery.Attempt(1025, guarded, out error) == PasteProgress.Skipped && sends == 1, "expired paste cannot fire later");

            delivery = new AutoPasteDelivery();
            Check(delivery.Attempt(1500, guarded, out error) == PasteProgress.Skipped && sends == 1, "late UI timer cannot paste after deadline even when keys are released");

            delivery = new AutoPasteDelivery();
            current = new IntPtr(30);
            Check(delivery.Attempt(0, guarded, out error) == PasteProgress.Skipped && sends == 1, "changed focus cancels even inside retry period");
            current = snapshot.WindowHandle;
            Check(delivery.Attempt(25, guarded, out error) == PasteProgress.Skipped && sends == 1, "returning focus never resurrects cancelled paste");
        }

        private static bool RunGuardedAutoPaste(
            AutoPasteTarget target,
            IntPtr foregroundWindow,
            uint clipboardSequence,
            bool modifierKeyHeld,
            out bool injected,
            out string error)
        {
            bool called = false;
            bool sent = AutoPaste.TrySendToTarget(
                target,
                delegate { return foregroundWindow; },
                delegate { return clipboardSequence; },
                delegate { return modifierKeyHeld; },
                delegate(out string injectError)
                {
                    called = true;
                    injectError = null;
                    return true;
                },
                out error);
            injected = called;
            return sent;
        }

        private static IntPtr ThrowForeground()
        {
            throw new InvalidOperationException("foreground lookup failed");
        }

        private static void TestDeleteFile(string baseDir)
        {
            string path = Path.Combine(baseDir, "todelete.dat");
            File.WriteAllText(path, "junk", new UTF8Encoding(false));
            Check(File.Exists(path), "deletefile setup exists");
            TranscriptHistory.DeleteFile(path);
            Check(!File.Exists(path), "deletefile deletes");
            bool threw = false;
            try
            {
                TranscriptHistory.DeleteFile(path);
            }
            catch
            {
                threw = true;
            }
            Check(!threw, "deletefile missing no throw");
            threw = false;
            try
            {
                TranscriptHistory.DeleteFile(null);
                TranscriptHistory.DeleteFile("");
            }
            catch
            {
                threw = true;
            }
            Check(!threw, "deletefile null empty no throw");
            string nested = Path.Combine(baseDir, "nested-delete.dat");
            File.WriteAllText(nested, "data", new UTF8Encoding(false));
            TranscriptHistory.DeleteFile(nested);
            Check(!File.Exists(nested), "deletefile nested deletes");
            TranscriptHistory history = new TranscriptHistory(nested, 5);
            history.Add("still works after deletefile");
            Check(history.Items.Count == 1, "deletefile history usable");
            Check(history.Items[0].Text == "still works after deletefile", "deletefile history text");
        }

        private static void TestHotkeyBothIds()
        {
            GlobalHotkeys hotkeys = new GlobalHotkeys();
            try
            {
                pressedCount = 0;
                cancelCount = 0;
                hotkeys.Pressed += OnTestPressed;
                hotkeys.CancelPressed += OnTestCancel;
                SendHotkey(hotkeys, 1);
                Check(pressedCount == 1, "hotkey id 1 raises pressed");
                SendHotkey(hotkeys, 3);
                Check(pressedCount == 2, "hotkey id 3 raises pressed");
                Check(cancelCount == 0, "hotkey main ids do not raise cancel");
                SendHotkey(hotkeys, 2);
                Check(cancelCount == 1, "hotkey cancel id raises cancel");
                Check(pressedCount == 2, "hotkey cancel does not raise pressed");
            }
            finally
            {
                hotkeys.Dispose();
            }
            Check(!hotkeys.SetCancelActive(true), "disposed hotkey owner reports cancel registration failure");
        }

        private static void OnTestPressed()
        {
            pressedCount++;
        }

        private static void OnTestCancel()
        {
            cancelCount++;
        }

        private static void SendHotkey(GlobalHotkeys hotkeys, int id)
        {
            System.Reflection.MethodInfo method = typeof(GlobalHotkeys).GetMethod("WndProc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Check(method != null, "hotkey wndproc exists");
            Message m = new Message();
            m.Msg = 0x0312;
            m.WParam = new IntPtr(id);
            m.LParam = IntPtr.Zero;
            m.HWnd = IntPtr.Zero;
            object[] args = new object[1];
            args[0] = m;
            try
            {
                method.Invoke(hotkeys, args);
            }
            catch (System.Reflection.TargetInvocationException)
            {
            }
        }
    }
}

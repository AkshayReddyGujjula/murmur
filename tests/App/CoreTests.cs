using System;
using System.IO;
using System.Text;
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
                TestBuildWav();
                Console.WriteLine("PASS: core settings, history, log, hotkey describe, autostart command, chimes.");
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
            settings.Sounds = false;
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
            Check(loaded.Sounds == false, "settings sounds roundtrip");
            Check(loaded.EscapeCancels == false, "settings escape roundtrip");

            AppSettings defaults = new AppSettings();
            Check(defaults.Hotkey == HotkeyChoice.CtrlSpace, "settings default hotkey");
            Check(defaults.Engine == EngineMode.AlwaysReady, "settings default engine");
            Check(defaults.IdleUnloadMinutes == 15, "settings default idle");
            Check(defaults.Placement == OverlayPlacement.BottomCenter, "settings default placement");
            Check(defaults.KeepHistory == true, "settings default keephistory");
            Check(defaults.HistoryLimit == 25, "settings default historylimit");
            Check(defaults.AutoPaste == false, "settings default autopaste");
            Check(defaults.Sounds == true, "settings default sounds");
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
            File.WriteAllText(unknownPath, "UnknownKey=123\nHotkey=CtrlSpace\nFutureKey=abc\n", new UTF8Encoding(false));
            AppSettings unknown = AppSettings.Load(unknownPath);
            Check(unknown.Hotkey == HotkeyChoice.CtrlSpace, "settings unknown keys ignored");

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
            Check(command == "\"C:\\Tools\\Murmur.exe\"", "commandfor quoted");
            string empty = Autostart.CommandFor("");
            Check(empty == "\"\"", "commandfor empty");
        }

        private static void TestBuildWav()
        {
            byte[] wav = Chimes.BuildWav(880, 70, 0.25);
            Check(wav.Length > 44, "wav length");
            Check(wav[0] == (byte)'R' && wav[1] == (byte)'I' && wav[2] == (byte)'F' && wav[3] == (byte)'F', "wav RIFF");
            Check(wav[8] == (byte)'W' && wav[9] == (byte)'A' && wav[10] == (byte)'V' && wav[11] == (byte)'E', "wav WAVE");
            Check(wav[12] == (byte)'f' && wav[13] == (byte)'m' && wav[14] == (byte)'t' && wav[15] == (byte)' ', "wav fmt");
            Check(wav[36] == (byte)'d' && wav[37] == (byte)'a' && wav[38] == (byte)'t' && wav[39] == (byte)'a', "wav data");
            int audioFormat = wav[20] | (wav[21] << 8);
            Check(audioFormat == 1, "wav pcm");
            int channels = wav[22] | (wav[23] << 8);
            Check(channels == 1, "wav mono");
            int bits = wav[34] | (wav[35] << 8);
            Check(bits == 16, "wav 16bit");
            int chunkSize = wav[4] | (wav[5] << 8) | (wav[6] << 16) | (wav[7] << 24);
            Check(chunkSize == wav.Length - 8, "wav chunk size");
            int dataSize = wav[40] | (wav[41] << 8) | (wav[42] << 16) | (wav[43] << 24);
            Check(dataSize == wav.Length - 44, "wav data size");
            byte[] wavLong = Chimes.BuildWav(300, 160, 0.25);
            Check(wavLong.Length > wav.Length, "wav longer duration longer");
            byte[] wavStop = Chimes.BuildWav(660, 70, 0.25);
            Check(wavStop.Length == wav.Length, "wav same duration same length");
        }
    }
}

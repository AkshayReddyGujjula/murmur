using System;
using System.Collections.Generic;
using System.Threading;
using Murmur.Probe;

// Offline tests for TerminalScreen. Pure in-memory only: no native calls,
// no apps, no mic, no clipboard, no credentials, no timers, no log files.
// Compile: csc /nologo /langversion:5 /t:exe /out:TerminalScreenTests.exe
//   src\Probe\TerminalScreen.cs tests\TerminalScreenTests.cs
internal static class TerminalScreenTests
{
    private static int _passed;
    private static int _failed;

    public static int Main()
    {
        Run("ArgValidation", ArgValidation);
        Run("BasicText", BasicText);
        Run("SplitEscapeChunks", SplitEscapeChunks);
        Run("CursorMoves", CursorMoves);
        Run("Overwrite", Overwrite);
        Run("EraseLine", EraseLine);
        Run("EraseDisplay", EraseDisplay);
        Run("Scroll", Scroll);
        Run("CrlfBsTab", CrlfBsTab);
        Run("SaveRestore", SaveRestore);
        Run("Dsr6nIsolated", Dsr6nIsolated);
        Run("Dsr5nAndDa", Dsr5nAndDa);
        Run("KittyQuery", KittyQuery);
        Run("SgrModesIgnored", SgrModesIgnored);
        Run("OscLinkStripped", OscLinkStripped);
        Run("OscColorQueryBounded", OscColorQueryBounded);
        Run("BoundedMalformed", BoundedMalformed);
        Run("ThreadSmoke", ThreadSmoke);

        Console.WriteLine("passed=" + _passed + " failed=" + _failed);
        return _failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Func<string> test)
    {
        string error;
        try
        {
            error = test();
        }
        catch (Exception ex)
        {
            error = "threw " + ex.GetType().Name + ": " + ex.Message;
        }
        if (error == null)
        {
            _passed++;
            Console.WriteLine("PASS " + name);
        }
        else
        {
            _failed++;
            Console.WriteLine("FAIL " + name + ": " + error);
        }
    }

    private sealed class Sink
    {
        public readonly List<string> Replies = new List<string>();
        public void Reply(string s) { Replies.Add(s); }
    }

    private static TerminalScreen NewScreen(int cols, int rows, Sink sink)
    {
        return new TerminalScreen(cols, rows);
    }

    private static string FeedAll(TerminalScreen screen, Sink sink, params string[] chunks)
    {
        for (int i = 0; i < chunks.Length; i++)
        {
            screen.Feed(chunks[i], sink.Reply);
        }
        return screen.Snapshot();
    }

    private static string ArgValidation()
    {
        try { new TerminalScreen(0, 24); return "cols=0 accepted"; }
        catch (ArgumentOutOfRangeException) { }
        try { new TerminalScreen(241, 24); return "cols=241 accepted"; }
        catch (ArgumentOutOfRangeException) { }
        try { new TerminalScreen(80, 0); return "rows=0 accepted"; }
        catch (ArgumentOutOfRangeException) { }
        try { new TerminalScreen(80, 81); return "rows=81 accepted"; }
        catch (ArgumentOutOfRangeException) { }
        TerminalScreen s = new TerminalScreen(80, 24);
        Sink sink = new Sink();
        try { s.Feed(null, sink.Reply); return "null chunk accepted"; }
        catch (ArgumentNullException) { }
        try { s.Feed("x", null); return "null reply accepted"; }
        catch (ArgumentNullException) { }
        s.Feed(string.Empty, sink.Reply);
        if (sink.Replies.Count != 0) return "empty feed replied";
        if (s.Snapshot() != string.Empty) return "fresh screen not empty";
        TerminalScreen edge = new TerminalScreen(240, 80);
        if (edge.Snapshot() != string.Empty) return "max grid not empty";
        return null;
    }

    private static string BasicText()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "hello");
        if (snap != "hello") return "got [" + snap + "]";
        if (sink.Replies.Count != 0) return "unexpected replies";
        return null;
    }

    private static string SplitEscapeChunks()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        // CSI split across three chunks: CUP to row 2 col 5, then text.
        string snap = FeedAll(s, sink, "ab", "\x1b[2", ";5H", "X");
        string[] lines = snap.Split('\n');
        if (lines.Length != 2) return "lines=" + lines.Length + " [" + snap + "]";
        if (lines[0] != "ab") return "line0 [" + lines[0] + "]";
        if (lines[1] != "    X") return "line1 [" + lines[1] + "]";
        // DSR split across chunks must still reply exactly once.
        s = new TerminalScreen(20, 5);
        sink = new Sink();
        FeedAll(s, sink, "\x1b[", "6n");
        if (sink.Replies.Count != 1) return "replies=" + sink.Replies.Count;
        if (sink.Replies[0] != "\x1b[1;1R") return "reply [" + Esc(sink.Replies[0]) + "]";
        if (s.Snapshot() != string.Empty) return "query leaked into screen";
        // OSC split across chunks: link must be stripped.
        s = new TerminalScreen(30, 5);
        sink = new Sink();
        snap = FeedAll(s, sink, "\x1b]8;;http://e", "xample\x1b", "\\HI\x1b]8;;\x1b\\!");
        if (snap != "HI!") return "osc split got [" + snap + "]";
        return null;
    }

    private static string CursorMoves()
    {
        TerminalScreen s = new TerminalScreen(10, 5);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "\x1b[2;3H" + "X");
        string[] lines = snap.Split('\n');
        if (lines.Length != 2 || lines[1] != "  X") return "CUP [" + snap + "]";
        // A/B/C/D relative moves.
        s = new TerminalScreen(10, 5);
        sink = new Sink();
        snap = FeedAll(s, sink, "\x1b[3;3H" + "o" + "\x1b[A" + "U" + "\x1b[B\x1b[C" + "R" + "\x1b[D" + "L");
        // o at (3,3); U at (2,3); R at (3,5); L overwrites R? D moves left 1 then L at (3,4).
        string[] l = snap.Split('\n');
        if (l.Length < 3) return "rows " + l.Length + " [" + snap + "]";
        if (l[1] != "   U") return "row2 [" + l[1] + "]";
        if (l[2] != "  o  L") return "row3 [" + l[2] + "]";
        // G (CHA) and d (VPA), f alias of H.
        s = new TerminalScreen(10, 5);
        sink = new Sink();
        snap = FeedAll(s, sink, "abcdef" + "\x1b[2G" + "X" + "\x1b[4d" + "Y" + "\x1b[1;1f" + "Z");
        l = snap.Split('\n');
        if (l[0] != "ZXcdef") return "G [" + l[0] + "]";
        if (l.Length != 4 || l[3] != "  Y") return "d [" + snap + "]";
        if (sink.Replies.Count != 0) return "moves replied";
        return null;
    }

    private static string Overwrite()
    {
        TerminalScreen s = new TerminalScreen(10, 3);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "hello" + "\x1b[1;1H" + "J");
        if (snap != "Jello") return "got [" + snap + "]";
        return null;
    }

    private static string EraseLine()
    {
        TerminalScreen s = new TerminalScreen(10, 3);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "hello" + "\x1b[1;3H" + "\x1b[K");
        if (snap != "he") return "K0 [" + snap + "]";
        s = new TerminalScreen(10, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "hello" + "\x1b[1;3H" + "\x1b[1K");
        if (snap != "   lo") return "K1 [" + snap + "]";
        s = new TerminalScreen(10, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "hello" + "\x1b[2K");
        if (snap != string.Empty) return "K2 [" + snap + "]";
        return null;
    }

    private static string EraseDisplay()
    {
        TerminalScreen s = new TerminalScreen(10, 3);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "aaa\r\nbbb\r\nccc" + "\x1b[2;2H" + "\x1b[0J");
        string[] l = snap.Split('\n');
        if (l.Length != 2 || l[0] != "aaa" || l[1] != "b") return "J0 [" + snap + "]";
        s = new TerminalScreen(10, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "aaa\r\nbbb\r\nccc" + "\x1b[2;2H" + "\x1b[1J");
        l = snap.Split('\n');
        if (l.Length != 3 || l[0] != string.Empty || l[1] != "  b" || l[2] != "ccc")
            return "J1 [" + snap + "]";
        // J2 split across chunks clears all and homes cursor.
        s = new TerminalScreen(10, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "aaa\r\nbbb", "\x1b[", "2J", "Z");
        if (snap != "Z") return "J2 [" + snap + "]";
        return null;
    }

    private static string Scroll()
    {
        TerminalScreen s = new TerminalScreen(4, 3);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "1111\r\n2222\r\n3333\r\n4444");
        // 4 lines of width 4 on 3 rows: eager wrap scrolls; last row holds 4444.
        if (snap != "2222\n3333\n4444") return "got [" + snap + "]";
        // Reverse index at top pushes content down.
        s = new TerminalScreen(4, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "top" + "\x1bM");
        string[] l = snap.Split('\n');
        if (l.Length != 2 || l[0] != string.Empty || l[1] != "top") return "M [" + snap + "]";
        return null;
    }

    private static string CrlfBsTab()
    {
        TerminalScreen s = new TerminalScreen(16, 3);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "ab\rcd");
        if (snap != "cd") return "CR [" + snap + "]";
        s = new TerminalScreen(16, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "ab\bX");
        if (snap != "aX") return "BS [" + snap + "]";
        s = new TerminalScreen(16, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "a\tb");
        if (snap != "a       b") return "TAB [" + snap + "]";
        s = new TerminalScreen(16, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "a\vb\fc");
        string[] l = snap.Split('\n');
        if (l.Length != 3 || l[0] != "a" || l[1] != " b" || l[2] != "  c")
            return "VT/FF [" + snap + "]";
        return null;
    }

    private static string SaveRestore()
    {
        TerminalScreen s = new TerminalScreen(10, 3);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "12" + "\x1b[s" + "34" + "\x1b[u" + "AB");
        if (snap != "12AB") return "s/u [" + snap + "]";
        s = new TerminalScreen(10, 3);
        sink = new Sink();
        snap = FeedAll(s, sink, "12" + "\u001b7" + "34" + "\u001b8" + "AB");
        if (snap != "12AB") return "7/8 [" + snap + "]";
        return null;
    }

    private static string Dsr6nIsolated()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        FeedAll(s, sink, "hi\x1b[3;7H" + "\x1b[6n" + "!");
        if (sink.Replies.Count != 1) return "replies=" + sink.Replies.Count;
        if (sink.Replies[0] != "\x1b[3;7R") return "reply [" + Esc(sink.Replies[0]) + "]";
        string snap = s.Snapshot();
        if (snap.IndexOf('R') >= 0 || snap.IndexOf("[3;7") >= 0)
            return "reply leaked [" + snap + "]";
        string[] l = snap.Split('\n');
        if (l[0] != "hi" || l[2] != "      !") return "screen [" + snap + "]";
        return null;
    }

    private static string Dsr5nAndDa()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        FeedAll(s, sink, "\x1b[5n" + "\x1b[c" + "\x1b[>c");
        if (sink.Replies.Count != 3) return "replies=" + sink.Replies.Count;
        if (sink.Replies[0] != "\x1b[0n") return "5n [" + Esc(sink.Replies[0]) + "]";
        if (sink.Replies[1] != "\x1b[?62c") return "DA [" + Esc(sink.Replies[1]) + "]";
        if (sink.Replies[2] != "\x1b[>0;0;0c") return "2DA [" + Esc(sink.Replies[2]) + "]";
        if (s.Snapshot() != string.Empty) return "leaked [" + s.Snapshot() + "]";
        return null;
    }

    private static string KittyQuery()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        FeedAll(s, sink, "\x1b[?u");
        if (sink.Replies.Count != 1) return "replies=" + sink.Replies.Count;
        if (sink.Replies[0] != "\x1b[?0u") return "got [" + Esc(sink.Replies[0]) + "]";
        if (s.Snapshot() != string.Empty) return "leaked";
        return null;
    }

    private static string SgrModesIgnored()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink, "\x1b[1;31m" + "red" + "\x1b[0m" + "\x1b[?25l" + "x" + "\x1b[?25h");
        if (snap != "redx") return "got [" + snap + "]";
        if (sink.Replies.Count != 0) return "replied";
        // Unknown CSI final must also be swallowed, not rendered.
        s = new TerminalScreen(20, 5);
        sink = new Sink();
        snap = FeedAll(s, sink, "a\x1b[9qz");
        if (snap != "az") return "unknown final [" + snap + "]";
        return null;
    }

    private static string OscLinkStripped()
    {
        TerminalScreen s = new TerminalScreen(40, 5);
        Sink sink = new Sink();
        string snap = FeedAll(s, sink,
            "\x1b]8;;https://example.com\x1b\\LINK\x1b]8;;\x1b\\done" +
            "\x1b]0;window title\x07" + "!");
        if (snap.IndexOf("example.com") >= 0) return "URL leaked [" + snap + "]";
        if (snap.IndexOf("title") >= 0) return "title leaked [" + snap + "]";
        if (snap != "LINKdone!") return "got [" + snap + "]";
        if (sink.Replies.Count != 0) return "OSC replied";
        return null;
    }

    private static string OscColorQueryBounded()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        FeedAll(s, sink, "\x1b]10;?\x1b\\" + "\x1b]11;?\x07");
        if (sink.Replies.Count != 2) return "replies=" + sink.Replies.Count;
        if (sink.Replies[0] != "\x1b]10;rgb:0000/0000/0000\x1b\\")
            return "10 [" + Esc(sink.Replies[0]) + "]";
        if (sink.Replies[1] != "\x1b]11;rgb:0000/0000/0000\x1b\\")
            return "11 [" + Esc(sink.Replies[1]) + "]";
        if (s.Snapshot() != string.Empty) return "leaked";
        for (int i = 0; i < sink.Replies.Count; i++)
        {
            if (sink.Replies[i].Length > 64) return "reply too long";
        }
        return null;
    }

    private static string BoundedMalformed()
    {
        TerminalScreen s = new TerminalScreen(20, 5);
        Sink sink = new Sink();
        // Unfinished CSI far beyond MaxPendingLength, then plain text.
        string huge = new string('9', TerminalScreen.MaxPendingLength + 500);
        string snap = FeedAll(s, sink, "\x1b[" + huge, "mOK");
        if (snap.IndexOf("9") >= 0) return "payload visible [" + snap + "]";
        if (snap != "OK") return "recovery [" + snap + "]";
        // Overlong OSC dropped, later text renders.
        s = new TerminalScreen(20, 5);
        sink = new Sink();
        snap = FeedAll(s, sink, "\x1b]" + new string('y', TerminalScreen.MaxPendingLength + 100), "\x07TAIL");
        if (snap.IndexOf("y") >= 0) return "osc payload visible";
        if (snap != "TAIL") return "osc recovery [" + snap + "]";
        // Malformed CSI with junk then a valid sequence recovers.
        s = new TerminalScreen(20, 5);
        sink = new Sink();
        snap = FeedAll(s, sink, "\x1b[;;;$q" + "\x1b[1;1H" + "Z");
        if (snap != "Z") return "junk recovery [" + snap + "]";
        if (sink.Replies.Count != 0) return "junk replied";
        return null;
    }

    private static string ThreadSmoke()
    {
        TerminalScreen s = new TerminalScreen(40, 10);
        List<string> errors = new List<string>();
        Thread[] workers = new Thread[4];
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t] = new Thread(delegate()
            {
                try
                {
                    List<string> local = new List<string>();
                    for (int i = 0; i < 200; i++)
                    {
                        s.Feed("ab\x1b[6n\x1b[1;1H", delegate(string r) { local.Add(r); });
                        string snap = s.Snapshot();
                        if (snap == null)
                        {
                            throw new Exception("null snapshot");
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (errors)
                    {
                        errors.Add(ex.Message);
                    }
                }
            });
            workers[t].Start();
        }
        for (int t = 0; t < workers.Length; t++)
        {
            workers[t].Join();
        }
        if (errors.Count != 0) return errors[0];
        return null;
    }

    private static string Esc(string value)
    {
        return value.Replace("\x1b", "ESC");
    }
}

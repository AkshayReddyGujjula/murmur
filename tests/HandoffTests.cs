// Fixture-filesystem tests for Murmur.Handoff.PromptHandoff.
// Self-contained console runner: compile with the library and run, no packages.
//   csc /t:exe src/Handoff/PromptHandoff.cs tests/HandoffTests.cs
// Only touches GUID roots under %TEMP%; never user files, processes, network,
// credentials, clipboard, mic, or native/terminal apps. Cleanup deletes only
// owned files plus known-empty leaf dirs (never recursive).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Murmur.Handoff;

static class HandoffTests
{
    static int passed;
    static int failed;
    static int skipped;

    static int Main()
    {
        Run("RoundTripExactBytes", TestRoundTripExactBytes);
        Run("NoBomMixedEndings", TestNoBomMixedEndings);
        Run("EmptyFile", TestEmptyFile);
        Run("ExactlyOneMiB", TestExactlyOneMiB);
        Run("OneOverOneMiB", TestOneOverOneMiB);
        Run("InvalidUtf8", TestInvalidUtf8);
        Run("DuplicatePublish", TestDuplicatePublish);
        Run("ExistingOutput", TestExistingOutput);
        Run("SourceEqualsDest", TestSourceEqualsDest);
        Run("TraversalOutside", TestTraversalOutside);
        Run("SiblingPrefix", TestSiblingPrefix);
        Run("RelativeForms", TestRelativeForms);
        Run("AdsColon", TestAdsColon);
        Run("DevicePaths", TestDevicePaths);
        Run("InvalidChars", TestInvalidChars);
        Run("ReservedNames", TestReservedNames);
        Run("TrailingDotSpace", TestTrailingDotSpace);
        Run("MissingNodes", TestMissingNodes);
        Run("NullEmptyArgs", TestNullEmptyArgs);
        Run("DestParentMissing", TestDestParentMissing);
        SkipReparse();
        Console.WriteLine("handoff-tests passed=" + passed + " failed=" + failed + " skipped=" + skipped);
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Func<string> test)
    {
        string error;
        try
        {
            error = test();
        }
        catch (Exception ex)
        {
            error = "exception " + ex.GetType().Name + ": " + ex.Message;
        }
        if (error == null)
        {
            passed++;
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failed++;
            Console.WriteLine("FAIL " + name + ": " + error);
        }
    }

    // Junction/symlink creation needs mklink, native APIs, or processes, all
    // out of bounds for these tests, so live reparse rejection is untested.
    // The ChainHasReparse walk covers file, parents, and session inclusive.
    static void SkipReparse()
    {
        skipped++;
        Console.WriteLine("SKIP ReparseRejected: link creation unavailable without native/process launch; chain check by inspection");
    }

    sealed class Fixture
    {
        public readonly string Root;
        public readonly string Session;
        readonly List<string> files = new List<string>();
        readonly List<string> dirs = new List<string>();

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "murmur-handoff-" + Guid.NewGuid().ToString("N"));
            Session = Path.Combine(Root, "session");
            Directory.CreateDirectory(Session);
            dirs.Add(Root);
            dirs.Add(Session); // Parents first: cleanup deletes in reverse (leaves first).
        }

        public string Subdir(string name)
        {
            string d = Path.Combine(Session, name);
            Directory.CreateDirectory(d);
            dirs.Add(d);
            return d;
        }

        public string WriteBytes(string dir, string name, byte[] bytes)
        {
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            files.Add(p);
            return p;
        }

        public void Note(string path)
        {
            files.Add(path);
        }

        public int StagingLeftovers(string dir)
        {
            try
            {
                return Directory.GetFiles(dir, "*.staging").Length;
            }
            catch
            {
                return -1;
            }
        }

        public void Cleanup()
        {
            foreach (string f in files)
            {
                try
                {
                    if (File.Exists(f))
                        File.Delete(f);
                }
                catch
                {
                }
            }
            for (int i = dirs.Count - 1; i >= 0; i--)
            {
                try
                {
                    Directory.Delete(dirs[i], false);
                }
                catch
                {
                }
            }
        }
    }

    static string CheckBytes(string what, byte[] actual, byte[] expected)
    {
        if (actual.Length != expected.Length)
            return what + " length " + actual.Length + " != " + expected.Length;
        for (int i = 0; i < actual.Length; i++)
            if (actual[i] != expected[i])
                return what + " differs at byte " + i;
        return null;
    }

    static string TestRoundTripExactBytes()
    {
        Fixture fx = new Fixture();
        try
        {
            // BOM + non-ASCII + emoji + CRLF + trailing spaces/tabs.
            string text = "Hello w\u00f6rld \u4e2d\u6587 \U0001F600\r\nsecond line trailing spaces   \t \r\nlast-\u00e9";
            byte[] pre = Encoding.UTF8.GetPreamble(); // EF BB BF.
            byte[] encoded = new UTF8Encoding(false, true).GetBytes(text);
            byte[] body = new byte[pre.Length + encoded.Length];
            Array.Copy(pre, 0, body, 0, pre.Length);
            Array.Copy(encoded, 0, body, pre.Length, encoded.Length);
            if (body.Length < 3 || body[0] != 0xEF || body[1] != 0xBB || body[2] != 0xBF)
                return "test setup missing BOM";
            string prompt = fx.WriteBytes(fx.Session, "prompt.txt", body);
            string dest = Path.Combine(fx.Session, "out.txt");
            fx.Note(dest);
            int rc = PromptHandoff.Export(prompt, fx.Session, dest);
            if (rc != 0)
                return "rc=" + rc;
            string err = CheckBytes("dest", File.ReadAllBytes(dest), body);
            if (err != null)
                return err;
            err = CheckBytes("source preserved", File.ReadAllBytes(prompt), body);
            if (err != null)
                return err;
            if (fx.StagingLeftovers(fx.Session) != 0)
                return "staging leftover";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestNoBomMixedEndings()
    {
        Fixture fx = new Fixture();
        try
        {
            byte[] body = Encoding.UTF8.GetBytes("a\t \nLF above, CRLF below\r\nend with tabs\t\t");
            string prompt = fx.WriteBytes(fx.Session, "p.txt", body);
            string dest = Path.Combine(fx.Session, "d.txt");
            fx.Note(dest);
            int rc = PromptHandoff.Export(prompt, fx.Session, dest);
            if (rc != 0)
                return "rc=" + rc;
            return CheckBytes("dest", File.ReadAllBytes(dest), body);
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestEmptyFile()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "empty.txt", new byte[0]);
            string dest = Path.Combine(fx.Session, "empty-out.txt");
            fx.Note(dest);
            int rc = PromptHandoff.Export(prompt, fx.Session, dest);
            if (rc != 0)
                return "rc=" + rc;
            if (new FileInfo(dest).Length != 0)
                return "dest not empty";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestExactlyOneMiB()
    {
        Fixture fx = new Fixture();
        try
        {
            byte[] body = new byte[PromptHandoff.MaxInputBytes];
            for (int i = 0; i < body.Length; i++)
                body[i] = (byte)'a';
            string prompt = fx.WriteBytes(fx.Session, "max.txt", body);
            string dest = Path.Combine(fx.Session, "max-out.txt");
            fx.Note(dest);
            int rc = PromptHandoff.Export(prompt, fx.Session, dest);
            if (rc != 0)
                return "rc=" + rc;
            return CheckBytes("dest", File.ReadAllBytes(dest), body);
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestOneOverOneMiB()
    {
        Fixture fx = new Fixture();
        try
        {
            byte[] body = new byte[PromptHandoff.MaxInputBytes + 1];
            for (int i = 0; i < body.Length; i++)
                body[i] = (byte)'a';
            string prompt = fx.WriteBytes(fx.Session, "big.txt", body);
            string dest = Path.Combine(fx.Session, "big-out.txt");
            int rc = PromptHandoff.Export(prompt, fx.Session, dest);
            if (rc == 0)
                return "accepted oversize input";
            if (File.Exists(dest))
                return "dest created despite oversize rejection";
            if (fx.StagingLeftovers(fx.Session) != 0)
                return "staging leftover";
            return CheckBytes("source preserved", File.ReadAllBytes(prompt), body);
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestInvalidUtf8()
    {
        Fixture fx = new Fixture();
        try
        {
            byte[][] cases = new byte[][]
            {
                new byte[] { 0x41, 0xFF, 0x42 },
                new byte[] { 0xE2, 0x82, 0x41 }, // Truncated 3-byte sequence.
                new byte[] { 0xC0, 0xAF },       // Overlong encoding.
            };
            for (int i = 0; i < cases.Length; i++)
            {
                string prompt = fx.WriteBytes(fx.Session, "bad" + i + ".txt", cases[i]);
                string dest = Path.Combine(fx.Session, "bad" + i + "-out.txt");
                int rc = PromptHandoff.Export(prompt, fx.Session, dest);
                if (rc == 0)
                    return "case " + i + " accepted invalid UTF-8";
                if (File.Exists(dest))
                    return "case " + i + " dest created";
                string err = CheckBytes("case " + i + " source preserved", File.ReadAllBytes(prompt), cases[i]);
                if (err != null)
                    return err;
            }
            if (fx.StagingLeftovers(fx.Session) != 0)
                return "staging leftover";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestDuplicatePublish()
    {
        Fixture fx = new Fixture();
        try
        {
            byte[] first = Encoding.UTF8.GetBytes("first");
            byte[] second = Encoding.UTF8.GetBytes("second");
            string p1 = fx.WriteBytes(fx.Session, "p1.txt", first);
            string p2 = fx.WriteBytes(fx.Session, "p2.txt", second);
            string dest = Path.Combine(fx.Session, "dup.txt");
            fx.Note(dest);
            if (PromptHandoff.Export(p1, fx.Session, dest) != 0)
                return "first export failed";
            int rc = PromptHandoff.Export(p2, fx.Session, dest);
            if (rc == 0)
                return "duplicate publish overwrote";
            string err = CheckBytes("dest unchanged", File.ReadAllBytes(dest), first);
            if (err != null)
                return err;
            if (fx.StagingLeftovers(fx.Session) != 0)
                return "owned staging not removed after conflict";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestExistingOutput()
    {
        Fixture fx = new Fixture();
        try
        {
            byte[] old = Encoding.UTF8.GetBytes("old");
            byte[] cur = Encoding.UTF8.GetBytes("new");
            string prompt = fx.WriteBytes(fx.Session, "p.txt", cur);
            string dest = fx.WriteBytes(fx.Session, "taken.txt", old);
            int rc = PromptHandoff.Export(prompt, fx.Session, dest);
            if (rc == 0)
                return "exported over existing output";
            return CheckBytes("dest unchanged", File.ReadAllBytes(dest), old);
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestSourceEqualsDest()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "same.txt", Encoding.UTF8.GetBytes("x"));
            if (PromptHandoff.Export(prompt, fx.Session, prompt) == 0)
                return "same path accepted";
            if (PromptHandoff.Export(prompt, fx.Session, prompt.ToUpperInvariant()) == 0)
                return "case-variant same path accepted";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestTraversalOutside()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            string outsidePrompt = fx.Session + "\\..\\outside-prompt.txt"; // Nonexistent; containment must reject first.
            if (PromptHandoff.Export(outsidePrompt, fx.Session, Path.Combine(fx.Session, "o.txt")) == 0)
                return "outside prompt accepted";
            string outsideDest = fx.Session + "\\..\\outside-dest.txt";
            if (PromptHandoff.Export(prompt, fx.Session, outsideDest) == 0)
                return "outside dest accepted";
            if (File.Exists(Path.Combine(fx.Root, "outside-dest.txt")))
                return "escaped file created";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestSiblingPrefix()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            // fx.Session + "-evil" shares a string prefix but not a boundary.
            string evil = fx.Session + "-evil\\x.txt";
            if (PromptHandoff.Export(evil, fx.Session, Path.Combine(fx.Session, "o.txt")) == 0)
                return "sibling-prefix prompt accepted";
            if (PromptHandoff.Export(prompt, fx.Session, evil) == 0)
                return "sibling-prefix dest accepted";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestRelativeForms()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            string dest = Path.Combine(fx.Session, "o.txt");
            // Path.IsPathRooted returns true for "\x", yet it is not absolute.
            if (PromptHandoff.Export("\\murmur-root-relative.txt", fx.Session, dest) == 0)
                return "root-relative prompt accepted";
            if (PromptHandoff.Export(prompt, fx.Session, "\\murmur-root-relative.txt") == 0)
                return "root-relative dest accepted";
            string driveRelative = fx.Session.Substring(0, 2) + "murmur-drive-relative.txt";
            if (PromptHandoff.Export(driveRelative, fx.Session, dest) == 0)
                return "drive-relative prompt accepted";
            if (PromptHandoff.Export(prompt, fx.Session, driveRelative) == 0)
                return "drive-relative dest accepted";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestAdsColon()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            if (PromptHandoff.Export(prompt + ":stream", fx.Session, Path.Combine(fx.Session, "o.txt")) == 0)
                return "ADS prompt accepted";
            if (PromptHandoff.Export(prompt, fx.Session, Path.Combine(fx.Session, "o.txt:stream")) == 0)
                return "ADS dest accepted";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestDevicePaths()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            string dest = Path.Combine(fx.Session, "o.txt");
            string[] bad = new string[]
            {
                "\\\\?\\C:\\x.txt",
                "\\\\.\\C:\\x.txt",
                "\\??\\C:\\x.txt",
            };
            for (int i = 0; i < bad.Length; i++)
            {
                if (PromptHandoff.Export(bad[i], fx.Session, dest) == 0)
                    return "device prompt accepted: " + bad[i];
                if (PromptHandoff.Export(prompt, fx.Session, bad[i]) == 0)
                    return "device dest accepted: " + bad[i];
            }
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestInvalidChars()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            char[] bad = new char[] { '*', '?', '<', '>', '|', '"' };
            for (int i = 0; i < bad.Length; i++)
            {
                string dest = fx.Session + "\\bad" + bad[i] + ".txt";
                if (PromptHandoff.Export(prompt, fx.Session, dest) == 0)
                    return "invalid char accepted: " + bad[i];
            }
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestReservedNames()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            string[] names = new string[] { "CON.txt", "aux", "NUL", "com1.log", "LPT9.dat" };
            for (int i = 0; i < names.Length; i++)
            {
                if (PromptHandoff.Export(prompt, fx.Session, Path.Combine(fx.Session, names[i])) == 0)
                    return "reserved name accepted: " + names[i];
            }
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestTrailingDotSpace()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            string[] names = new string[] { "bad.", "bad ", "dir.\\f.txt" };
            for (int i = 0; i < names.Length; i++)
            {
                if (PromptHandoff.Export(prompt, fx.Session, Path.Combine(fx.Session, names[i])) == 0)
                    return "trailing dot/space accepted: " + names[i];
            }
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestMissingNodes()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            if (PromptHandoff.Export(Path.Combine(fx.Session, "nope.txt"), fx.Session, Path.Combine(fx.Session, "o.txt")) == 0)
                return "missing source accepted";
            string ghostSession = Path.Combine(fx.Root, "no-such-session-" + Guid.NewGuid().ToString("N"));
            if (PromptHandoff.Export(Path.Combine(ghostSession, "p.txt"), ghostSession, Path.Combine(ghostSession, "o.txt")) == 0)
                return "missing session accepted";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestNullEmptyArgs()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            string dest = Path.Combine(fx.Session, "o.txt");
            if (PromptHandoff.Export(null, fx.Session, dest) == 0)
                return "null prompt accepted";
            if (PromptHandoff.Export("", fx.Session, dest) == 0)
                return "empty prompt accepted";
            if (PromptHandoff.Export("   ", fx.Session, dest) == 0)
                return "blank prompt accepted";
            if (PromptHandoff.Export(prompt, null, dest) == 0)
                return "null session accepted";
            if (PromptHandoff.Export(prompt, fx.Session, null) == 0)
                return "null dest accepted";
            if (PromptHandoff.Export(prompt, "", dest) == 0)
                return "empty session accepted";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }

    static string TestDestParentMissing()
    {
        Fixture fx = new Fixture();
        try
        {
            string prompt = fx.WriteBytes(fx.Session, "p.txt", Encoding.UTF8.GetBytes("x"));
            string dest = Path.Combine(fx.Session, "no-such-dir", "o.txt");
            if (PromptHandoff.Export(prompt, fx.Session, dest) == 0)
                return "missing parent accepted";
            if (File.Exists(dest))
                return "dest created without parent";
            if (fx.StagingLeftovers(fx.Session) != 0)
                return "staging leftover";
            return null;
        }
        finally
        {
            fx.Cleanup();
        }
    }
}

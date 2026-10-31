// Offline tests for Murmur.Transport.OwnedTerminal.
//
// Compiled together with src/Transport/OwnedTerminal.cs by the in-box Framework64
// C# 5 compiler into one EXE. No terminal, provider, microphone, clipboard, window or
// network resources are touched: every test here is either a pure helper, argument
// validation that must reject before any native call, a struct layout check, or the
// disposal of an object that was never started.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Murmur.Transport.Tests
{
    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        internal static int Main()
        {
            Run("QuoteArgument", TestQuoteArgument);
            Run("QuoteArgument round trip", TestQuoteArgumentRoundTrip);
            Run("BuildCommandLine", TestBuildCommandLine);
            Run("EnvironmentBlock inheritance", TestEnvironmentBlockInheritance);
            Run("EnvironmentBlock overrides and removals", TestEnvironmentBlockOverrides);
            Run("EnvironmentBlock sorting and termination", TestEnvironmentBlockShape);
            Run("EnvironmentBlock validation", TestEnvironmentBlockValidation);
            Run("EnvironmentBlock size limit", TestEnvironmentBlockLimit);
            Run("Start validation before native", TestStartValidation);
            Run("Start rejects invalid environment", TestStartEnvironmentValidation);
            Run("Native struct layout", TestStructLayout);
            Run("Dispose never-started object", TestNeverStartedDispose);
            Run("TryWrite argument validation", TestTryWriteValidation);
            Run("Dispose after rejected Start", TestDisposeAfterRejectedStart);

            Console.WriteLine();
            Console.WriteLine("{0} checks, {1} failed.", _checks, _failures);
            return _failures == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            int before = _failures;
            try
            {
                test();
            }
            catch (Exception error)
            {
                Fail(name + " threw " + error.GetType().Name + ": " + error.Message);
                Console.Error.WriteLine(error.StackTrace);
            }
            Console.WriteLine("{0} {1}", _failures == before ? "ok  " : "FAIL", name);
        }

        private static void TestQuoteArgument()
        {
            Equal("\"\"", OwnedTerminal.QuoteArgument(string.Empty), "empty argument is quoted");
            Equal("abc", OwnedTerminal.QuoteArgument("abc"), "plain argument is unchanged");
            Equal("C:\\Windows\\notepad.exe", OwnedTerminal.QuoteArgument("C:\\Windows\\notepad.exe"), "path without spaces is unchanged");
            Equal("\"a b\"", OwnedTerminal.QuoteArgument("a b"), "space forces quotes");
            Equal("\"a\tb\"", OwnedTerminal.QuoteArgument("a\tb"), "tab forces quotes");
            Equal("\"a\\\"b\"", OwnedTerminal.QuoteArgument("a\"b"), "quote is escaped");
            Equal("a\\b", OwnedTerminal.QuoteArgument("a\\b"), "backslash without quotes is literal");
            Equal("a\\", OwnedTerminal.QuoteArgument("a\\"), "trailing backslash without quotes is literal");
            Equal("\"a b\\\\\"", OwnedTerminal.QuoteArgument("a b\\"), "trailing backslash is doubled when quoted");
            Equal("\"a b\\\\\\\\\"", OwnedTerminal.QuoteArgument("a b\\\\"), "trailing backslashes double when quoted");
            Equal("\"a b\\\\\\\"c\"", OwnedTerminal.QuoteArgument("a b\\\"c"), "backslash before quote is doubled");
            Equal("\"C:\\Program Files\\App\\\\\"", OwnedTerminal.QuoteArgument("C:\\Program Files\\App\\"), "quoted windows path");
            Equal("\"--x=a b\"", OwnedTerminal.QuoteArgument("--x=a b"), "switch with value is quoted");
            Equal("日本語", OwnedTerminal.QuoteArgument("日本語"), "unicode without spaces needs no quotes");
            Equal("\"日本 語\"", OwnedTerminal.QuoteArgument("日本 語"), "unicode with a space is quoted");
            Throws<ArgumentNullException>(delegate { OwnedTerminal.QuoteArgument(null); }, "null is rejected");
        }

        private static void TestQuoteArgumentRoundTrip()
        {
            string[] values = new string[]
            {
                "abc",
                "",
                "a b",
                " a ",
                "a\"b",
                "\"",
                "\"\"",
                "a\\b",
                "a\\",
                "a b\\",
                "a b\\\\",
                "a\\\"b",
                "a b\\\"c",
                "C:\\Program Files\\x y\\app.exe",
                "C:\\trailing space \\",
                "--flag=value with space",
                "😀 emoji",
                "tab\there",
                "line\nbreak"
            };

            StringBuilder line = new StringBuilder();
            for (int i = 0; i < values.Length; i++)
            {
                if (i > 0) line.Append(' ');
                line.Append(OwnedTerminal.QuoteArgument(values[i]));
            }

            string[] parsed = SplitCommandLine(line.ToString());
            Equal(values.Length, parsed.Length, "quoted argument count round trips");
            for (int i = 0; i < values.Length && i < parsed.Length; i++)
                Equal(values[i], parsed[i], "round trip argument " + i.ToString());
        }

        private static void TestBuildCommandLine()
        {
            string[] arguments = new string[] { "-m", "say \"hi\"", "C:\\a b\\", string.Empty, "x\\\"y" };
            string line = OwnedTerminal.BuildCommandLine("C:\\Program Files\\App\\app.exe", arguments);

            string[] parsed = SplitCommandLine(line);
            Equal(arguments.Length + 1, parsed.Length, "command line starts with argv[0] and every argument");
            Equal("C:\\Program Files\\App\\app.exe", parsed[0], "executable is argv[0]");
            for (int i = 0; i < arguments.Length; i++)
                Equal(arguments[i], parsed[i + 1], "command line argument " + i.ToString());

            Equal("app.exe", OwnedTerminal.BuildCommandLine("app.exe", null), "null arguments add nothing");
            Equal("app.exe", OwnedTerminal.BuildCommandLine("app.exe", new string[0]), "empty arguments add nothing");
            Throws<ArgumentNullException>(delegate { OwnedTerminal.BuildCommandLine(null, null); }, "null executable is rejected");
        }

        private static void TestEnvironmentBlockInheritance()
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            Check(path != null, "the test process has PATH to inherit");

            string block = OwnedTerminal.EnvironmentBlock(null);
            Dictionary<string, string> entries = ReadBlock(block);

            Check(entries.ContainsKey("PATH"), "inherited PATH is present");
            if (path != null)
                Equal(path, entries["PATH"], "inherited PATH value is preserved");

            Check(block.EndsWith("\0\0", StringComparison.Ordinal), "block ends with a double NUL");
            Check(block.IndexOf("\0\0", StringComparison.Ordinal) == block.Length - 2, "no NUL beyond the terminator");
        }

        private static void TestEnvironmentBlockOverrides()
        {
            IDictionary before = Environment.GetEnvironmentVariables();
            string pathBefore = Environment.GetEnvironmentVariable("PATH");

            Dictionary<string, string> overrides = new Dictionary<string, string>();
            overrides["MURMUR_BLOCK_PROBE"] = "v1";
            overrides["PATH"] = null;
            overrides["MURMUR_BLOCK_EMPTY"] = string.Empty;

            string block = OwnedTerminal.EnvironmentBlock(overrides);
            Dictionary<string, string> entries = ReadBlock(block);

            Equal("v1", entries["MURMUR_BLOCK_PROBE"], "override value is applied");
            Equal(string.Empty, entries["MURMUR_BLOCK_EMPTY"], "empty override value is applied");
            Check(!ContainsName(entries, "PATH"), "null override removes an inherited key");
            int expected = before.Count + 2 - (pathBefore == null ? 0 : 1);
            Equal(expected, entries.Count, "child block has two additions and one removal");

            Check(Environment.GetEnvironmentVariable("MURMUR_BLOCK_PROBE") == null, "override is not written to the parent");
            Equal(pathBefore, Environment.GetEnvironmentVariable("PATH"), "parent PATH is unchanged");
            Check(Environment.GetEnvironmentVariables().Count == before.Count, "parent environment keeps its size");

            Dictionary<string, string> again = new Dictionary<string, string>();
            again["murmur_block_probe"] = "v2";
            Dictionary<string, string> mixedCase = ReadBlock(OwnedTerminal.EnvironmentBlock(again));
            Equal("v2", mixedCase["MURMUR_BLOCK_PROBE"], "override names are case insensitive");
            Equal(1, CountName(mixedCase, "murmur_block_probe"), "case-insensitive keys are not duplicated");

            Dictionary<string, string> missing = new Dictionary<string, string>();
            missing["MURMUR_BLOCK_ABSENT"] = null;
            Check(ReadBlock(OwnedTerminal.EnvironmentBlock(missing)).Count == before.Count, "removing an absent key is a no-op");
        }

        private static void TestEnvironmentBlockShape()
        {
            Dictionary<string, string> overrides = new Dictionary<string, string>();
            overrides["MURMUR_SORT_B"] = "2";
            overrides["MURMUR_SORT_A"] = "1";
            overrides["MURMUR_SORT_C"] = "3";

            string block = OwnedTerminal.EnvironmentBlock(overrides);
            Check(block.EndsWith("\0\0", StringComparison.Ordinal), "block ends with a double NUL");
            Check(block.IndexOf("\0\0", StringComparison.Ordinal) == block.Length - 2, "exactly one double NUL terminator");

            string[] raw = block.Split('\0');
            Check(raw[raw.Length - 1].Length == 0 && raw[raw.Length - 2].Length == 0, "terminator is empty entries");

            List<string> names = new List<string>();
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i].Length == 0) continue;
                int split = raw[i].IndexOf('=');
                Check(split > 0, "entry has a name and '=' separator");
                names.Add(raw[i].Substring(0, split));
            }

            for (int i = 1; i < names.Count; i++)
                Check(StringComparer.OrdinalIgnoreCase.Compare(names[i - 1], names[i]) < 0, "names are sorted case insensitively");

            string relative = OwnedTerminal.EnvironmentBlock(new Dictionary<string, string>());
            Equal(block.Length - ("MURMUR_SORT_A=1\0MURMUR_SORT_B=2\0MURMUR_SORT_C=3\0".Length), relative.Length, "empty overrides only omit the added entries");
        }

        private static void TestEnvironmentBlockValidation()
        {
            Throws<ArgumentException>(delegate
            {
                OwnedTerminal.EnvironmentBlock(new Dictionary<string, string> { { string.Empty, "x" } });
            }, "empty name is rejected");

            Throws<ArgumentException>(delegate
            {
                OwnedTerminal.EnvironmentBlock(new Dictionary<string, string> { { "A=B", "x" } });
            }, "'=' in name is rejected");

            Throws<ArgumentException>(delegate
            {
                OwnedTerminal.EnvironmentBlock(new Dictionary<string, string> { { "A\0B", "x" } });
            }, "NUL in name is rejected");

            Throws<ArgumentException>(delegate
            {
                OwnedTerminal.EnvironmentBlock(new Dictionary<string, string> { { "A", "x\0y" } });
            }, "NUL in value is rejected");
        }

        private static void TestEnvironmentBlockLimit()
        {
            Throws<InvalidOperationException>(delegate
            {
                OwnedTerminal.EnvironmentBlock(new Dictionary<string, string> { { "MURMUR_HUGE", new string('x', 33000) } });
            }, "block over the CreateProcess limit is rejected");
        }

        private static void TestStartValidation()
        {
            string directory = CreateScratchDirectory();
            try
            {
                string existingExe = Path.Combine(directory, "fake.exe");
                File.WriteAllText(existingExe, "not a real program");

                OwnedTerminal terminal = new OwnedTerminal();
                Throws<ArgumentNullException>(delegate { terminal.Start(null, null, directory, null); }, "null executable");
                Throws<ArgumentException>(delegate { terminal.Start(string.Empty, null, directory, null); }, "empty executable");
                Throws<ArgumentException>(delegate { terminal.Start("   ", null, directory, null); }, "blank executable");
                Throws<ArgumentException>(delegate { terminal.Start("fake.exe", null, directory, null); }, "relative executable");
                Throws<ArgumentException>(delegate { terminal.Start(existingExe + "\0", null, directory, null); }, "NUL in executable");
                Throws<ArgumentException>(delegate { terminal.Start(Path.Combine(directory, "fake.exe.bat"), null, directory, null); }, "non-exe executable");
                Throws<FileNotFoundException>(delegate { terminal.Start(Path.Combine(directory, "missing.exe"), null, directory, null); }, "missing executable");

                Throws<ArgumentNullException>(delegate { terminal.Start(existingExe, null, null, null); }, "null working directory");
                Throws<ArgumentException>(delegate { terminal.Start(existingExe, null, string.Empty, null); }, "empty working directory");
                Throws<ArgumentException>(delegate { terminal.Start(existingExe, null, ".", null); }, "relative working directory");
                Throws<ArgumentException>(delegate { terminal.Start(existingExe, null, directory + "\0", null); }, "NUL in working directory");
                Throws<DirectoryNotFoundException>(delegate { terminal.Start(existingExe, null, Path.Combine(directory, "absent"), null); }, "missing working directory");

                Throws<ArgumentException>(delegate { terminal.Start(existingExe, new string[] { "ok", null }, directory, null); }, "null argument");
                Throws<ArgumentException>(delegate { terminal.Start(existingExe, new string[] { "a\0b" }, directory, null); }, "NUL in argument");

                // Every rejection above must happen before any native call: no Win32Exception,
                // no PlatformNotSupportedException, and the object stays inert.
                Check(!terminal.TryWrite("x"), "rejected object is not writable");
                terminal.Dispose();
                terminal.Dispose();
                Check(!terminal.TryWrite("x"), "rejected object stays inert after disposal");
            }
            finally
            {
                DeleteScratchDirectory(directory);
            }
        }

        private static void TestStartEnvironmentValidation()
        {
            string directory = CreateScratchDirectory();
            try
            {
                string existingExe = Path.Combine(directory, "fake.exe");
                File.WriteAllText(existingExe, "not a real program");

                OwnedTerminal terminal = new OwnedTerminal();
                Throws<ArgumentException>(delegate
                {
                    terminal.Start(existingExe, null, directory, new Dictionary<string, string> { { "A=B", "1" } });
                }, "'=' in override name");
                Throws<ArgumentException>(delegate
                {
                    terminal.Start(existingExe, null, directory, new Dictionary<string, string> { { "A", "1\0" } });
                }, "NUL in override value");
                Throws<ArgumentException>(delegate
                {
                    terminal.Start(existingExe, null, directory, new Dictionary<string, string> { { string.Empty, "1" } });
                }, "empty override name");

                terminal.Dispose();
                Check(!terminal.TryWrite("x"), "invalid environment keeps the object inert");
            }
            finally
            {
                DeleteScratchDirectory(directory);
            }
        }

        private static void TestStructLayout()
        {
            Equal(8, IntPtr.Size, "tests run as x64");

            Equal(4, Marshal.SizeOf(typeof(OwnedTerminal.Coord)), "COORD is two shorts");
            Equal(104, Marshal.SizeOf(typeof(OwnedTerminal.StartupInfo)), "STARTUPINFOW is 104 bytes on x64");
            Equal(112, Marshal.SizeOf(typeof(OwnedTerminal.StartupInfoEx)), "STARTUPINFOEXW piles one pointer on STARTUPINFOW");
            Equal(24, Marshal.SizeOf(typeof(OwnedTerminal.SecurityAttributes)), "SECURITY_ATTRIBUTES is 24 bytes on x64");
            Equal(24, Marshal.SizeOf(typeof(OwnedTerminal.ProcessInformation)), "PROCESS_INFORMATION is 24 bytes on x64");
            Equal(144, Marshal.SizeOf(typeof(OwnedTerminal.JobObjectExtendedLimitInformation)), "extended job limits are 144 bytes on x64");
        }

        private static void TestNeverStartedDispose()
        {
            OwnedTerminal terminal = new OwnedTerminal();
            int output = 0;
            int closed = 0;
            terminal.Output += delegate { output++; };
            terminal.Closed += delegate { closed++; };

            Check(!terminal.TryWrite("hello"), "unstarted object refuses writes");
            terminal.Dispose();
            terminal.Dispose();
            Check(!terminal.TryWrite("hello"), "disposed object refuses writes");
            Equal(0, output, "never-started disposal raises no output");
            Equal(0, closed, "never-started disposal raises no close notice");
        }

        private static void TestTryWriteValidation()
        {
            OwnedTerminal terminal = new OwnedTerminal();
            Throws<ArgumentNullException>(delegate { terminal.TryWrite(null); }, "null text is rejected before start");
            terminal.Dispose();
            Throws<ArgumentNullException>(delegate { terminal.TryWrite(null); }, "null text is rejected after disposal");
        }

        private static void TestDisposeAfterRejectedStart()
        {
            OwnedTerminal terminal = new OwnedTerminal();
            int closed = 0;
            terminal.Closed += delegate { closed++; };
            Throws<ArgumentException>(delegate { terminal.Start("relative.exe", null, Environment.CurrentDirectory, null); }, "relative executable rejected");
            Check(!terminal.TryWrite("x"), "rejected Start leaves the terminal inert");
            terminal.Dispose();
            Equal(0, closed, "rejected Start raises no close notice");
        }

        private static string CreateScratchDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "murmur-transport-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteScratchDirectory(string path)
        {
            try
            {
                string fixture = Path.Combine(path, "fake.exe");
                if (File.Exists(fixture)) File.Delete(fixture);
                if (Directory.Exists(path)) Directory.Delete(path, false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private static Dictionary<string, string> ReadBlock(string block)
        {
            Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] raw = block.Split('\0');
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i].Length == 0) continue;
                int split = raw[i].IndexOf('=');
                Check(split > 0, "environment entry contains '='");
                if (split <= 0) continue;
                entries[raw[i].Substring(0, split)] = raw[i].Substring(split + 1);
            }
            return entries;
        }

        private static bool ContainsName(Dictionary<string, string> entries, string name)
        {
            foreach (string key in entries.Keys)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static int CountName(Dictionary<string, string> entries, string name)
        {
            int count = 0;
            foreach (string key in entries.Keys)
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) count++;
            return count;
        }

        // Reference implementation of the CommandLineToArgvW/CRT argument splitting rules.
        private static string[] SplitCommandLine(string line)
        {
            List<string> arguments = new List<string>();
            int index = 0;

            while (index < line.Length)
            {
                while (index < line.Length && (line[index] == ' ' || line[index] == '\t')) index++;
                if (index >= line.Length) break;

                StringBuilder current = new StringBuilder();
                bool inQuotes = false;

                while (index < line.Length)
                {
                    char c = line[index];
                    if (!inQuotes && (c == ' ' || c == '\t')) break;

                    if (c == '\\')
                    {
                        int slashes = 0;
                        while (index < line.Length && line[index] == '\\')
                        {
                            slashes++;
                            index++;
                        }

                        if (index < line.Length && line[index] == '"')
                        {
                            current.Append('\\', slashes / 2);
                            if (slashes % 2 == 0) inQuotes = !inQuotes;
                            else current.Append('"');
                            index++;
                        }
                        else
                        {
                            current.Append('\\', slashes);
                        }
                    }
                    else if (c == '"')
                    {
                        inQuotes = !inQuotes;
                        index++;
                    }
                    else
                    {
                        current.Append(c);
                        index++;
                    }
                }

                arguments.Add(current.ToString());
            }

            return arguments.ToArray();
        }

        private static void Equal(object expected, object actual, string message)
        {
            _checks++;
            if (object.Equals(expected, actual)) return;
            Fail(message + " (expected <" + Describe(expected) + ">, got <" + Describe(actual) + ">)");
        }

        private static void Check(bool condition, string message)
        {
            _checks++;
            if (condition) return;
            Fail(message);
        }

        private static void Throws<T>(Action action, string message) where T : Exception
        {
            _checks++;
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            catch (Exception other)
            {
                Fail(message + " (expected " + typeof(T).Name + ", got " + other.GetType().Name + ")");
                return;
            }
            Fail(message + " (expected " + typeof(T).Name + ", nothing was thrown)");
        }

        private static void Fail(string message)
        {
            _failures++;
            Console.WriteLine("      failed: " + message);
        }

        private static string Describe(object value)
        {
            if (value == null) return "null";
            string text = value.ToString();
            if (text.Length > 80) text = text.Substring(0, 80) + "...";
            return text.Replace("\0", "\\0").Replace("\n", "\\n").Replace("\t", "\\t");
        }
    }
}

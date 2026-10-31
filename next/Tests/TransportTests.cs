using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Murmur.Next.Windows;

internal static class TransportTests
{
    private static int assertions;
    private static void Assert(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }

    private static string ParseWindowsQuotedArgument(string argument)
    {
        // Independent implementation of Windows' quote/backslash argument rules.
        StringBuilder result = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < argument.Length; )
        {
            int slashes = 0;
            while (i < argument.Length && argument[i] == '\\') { slashes++; i++; }
            if (i < argument.Length && argument[i] == '"')
            {
                result.Append('\\', slashes / 2);
                if (slashes % 2 == 1) result.Append('"'); else quoted = !quoted;
                i++;
            }
            else
            {
                result.Append('\\', slashes);
                if (i < argument.Length)
                {
                    if (!quoted && Char.IsWhiteSpace(argument[i])) throw new Exception("Argument split.");
                    result.Append(argument[i++]);
                }
            }
        }
        Assert(!quoted, "Unclosed argument quote.");
        return result.ToString();
    }

    private static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Assert(rejected, message); }

    public static int Main()
    {
        try
        {
            string[] inputs = { "", "simple", "two words", "C:\\a b\\", "a\"b", "\\\"", "a\\\\\" b\\", "\t\n", "英語 😀", "& calc.exe | $(bad) %PATH%" };
            foreach (string value in inputs)
                Assert(ParseWindowsQuotedArgument(PseudoTerminal.QuoteArgument(value)) == value, "Argument roundtrip failed.");
            Random random = new Random(107);
            const string alphabet = "abc \\\"\t&|%";
            for (int n = 0; n < 400; n++)
            {
                StringBuilder value = new StringBuilder();
                for (int j = random.Next(80); j > 0; j--) value.Append(alphabet[random.Next(alphabet.Length)]);
                Assert(ParseWindowsQuotedArgument(PseudoTerminal.QuoteArgument(value.ToString())) == value.ToString(), "Generated argument roundtrip failed.");
            }
            Reject(delegate { PseudoTerminal.QuoteArgument("bad\0arg"); }, "NUL argument accepted.");
            Reject(delegate { PseudoTerminal.QuoteArgument(null); }, "Null argument accepted.");
            string before = Environment.GetEnvironmentVariable("MURMUR_TRANSPORT_TEST");
            string block = PseudoTerminal.BuildEnvironment(new Dictionary<string, string> {
                { "MURMUR_TRANSPORT_TEST", "child only" }, { "MURMUR_REMOVED", null }
            });
            Assert(block.EndsWith("\0\0", StringComparison.Ordinal), "Missing environment terminator.");
            Assert(block.Contains("MURMUR_TRANSPORT_TEST=child only\0"), "Child environment missing.");
            Assert(Environment.GetEnvironmentVariable("MURMUR_TRANSPORT_TEST") == before, "Parent environment mutated.");
            Reject(delegate { PseudoTerminal.BuildEnvironment(new Dictionary<string, string> { { "BAD=NAME", "x" } }); }, "Invalid environment key accepted.");
            Reject(delegate { PseudoTerminal.BuildEnvironment(new Dictionary<string, string> { { "NAME", "x\0INJECTED=y" } }); }, "Environment value injection accepted.");
            Assert(IntPtr.Size == 8, "Tests must compile for x64.");
            Assert(Marshal.SizeOf(typeof(PseudoTerminal).GetNestedType("STARTUPINFOEX", BindingFlags.NonPublic)) == 112, "Incorrect STARTUPINFOEX ABI.");
            Assert(Marshal.SizeOf(typeof(PseudoTerminal).GetNestedType("JOBOBJECT_EXTENDED_LIMIT_INFORMATION", BindingFlags.NonPublic)) == 144, "Incorrect job limit ABI.");
            using (PseudoTerminal terminal = new PseudoTerminal())
            {
                Assert(!terminal.Send("x"), "Unstarted terminal accepted input.");
                terminal.Dispose();
                Assert(!terminal.Send("x"), "Disposed terminal accepted input.");
            }
            Console.WriteLine("PASS: " + assertions + " transport assertions; no native APIs or provider processes invoked.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error.Message); return 1; }
    }
}

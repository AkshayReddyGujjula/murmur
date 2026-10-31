using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Murmur.Next.Probe;

namespace Murmur.Next.Probe.Tests
{
    /// <summary>
    /// Dependency-free test runner for PromptExport. Each test creates its
    /// own isolated temp fixtures under generated paths and leaves them in
    /// place (no recursive deletion). Exit code 0 when every test passes
    /// or skips, 1 when any test fails. Test output names only outcomes,
    /// never transcript or file contents.
    /// </summary>
    public static class PromptExportTests
    {
        private static int passes;
        private static int failures;
        private static int skips;

        public static int Main(string[] args)
        {
            passes = 0;
            failures = 0;
            skips = 0;

            RunTest("UnicodeCrlfTrailingSpacesExactBytes", UnicodeCrlfTrailingSpacesExactBytes);
            RunTest("Utf8BomPreserved", Utf8BomPreserved);
            RunTest("SourceUnchanged", SourceUnchanged);
            RunTest("EmptyAccepted", EmptyAccepted);
            RunTest("ExactOneMiBAccepted", ExactOneMiBAccepted);
            RunTest("DuplicateExportRejected", DuplicateExportRejected);
            RunTest("SiblingPrefixRejected", SiblingPrefixRejected);
            RunTest("TraversalRejected", TraversalRejected);
            RunTest("ReparseRejected", ReparseRejected);
            RunTest("InvalidUtf8Rejected", InvalidUtf8Rejected);
            RunTest("OversizeRejected", OversizeRejected);
            RunTest("InterruptedFailureLeavesNoPartial", InterruptedFailureLeavesNoPartial);
            RunTest("MissingRootAndRelativeRejected", MissingRootAndRelativeRejected);

            Console.WriteLine("RESULT passes=" + passes + " failures=" + failures + " skips=" + skips);
            return failures == 0 ? 0 : 1;
        }

        private delegate bool TestBody();

        private static void RunTest(string name, TestBody body)
        {
            bool ok;
            try
            {
                ok = body();
            }
            catch (Exception)
            {
                ok = false;
            }
            if (ok)
            {
                passes++;
                Console.WriteLine("PASS " + name);
            }
            else
            {
                failures++;
                Console.WriteLine("FAIL " + name);
            }
        }

        private static void Skip(string name)
        {
            skips++;
            Console.WriteLine("SKIP " + name);
        }

        private static string CreateIsolatedRoot()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "murmur-next-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static bool UnicodeCrlfTrailingSpacesExactBytes()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            string text = "h\u00E9llo w\u00F6rld \u2014 emoji \uD83D\uDE00\r\nline2 with trailing spaces   \r\n\tend\t";
            byte[] raw = Encoding.UTF8.GetBytes(text);
            File.WriteAllBytes(input, raw);

            int rc = PromptExport.Run(input, root, export);
            if (rc != 0) return false;
            byte[] outBytes = File.ReadAllBytes(export);
            return BytesEqual(raw, outBytes);
        }

        private static bool Utf8BomPreserved()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            byte[] bom = new byte[] { 0xEF, 0xBB, 0xBF };
            byte[] body = Encoding.UTF8.GetBytes("bom s\u00E2ng \u2014 test\r\n");
            byte[] raw = new byte[bom.Length + body.Length];
            Buffer.BlockCopy(bom, 0, raw, 0, bom.Length);
            Buffer.BlockCopy(body, 0, raw, bom.Length, body.Length);
            File.WriteAllBytes(input, raw);

            int rc = PromptExport.Run(input, root, export);
            if (rc != 0) return false;
            byte[] outBytes = File.ReadAllBytes(export);
            return BytesEqual(raw, outBytes);
        }

        private static bool SourceUnchanged()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            byte[] raw = Encoding.UTF8.GetBytes("source stays \u00E9\r\n");
            File.WriteAllBytes(input, raw);

            int rc = PromptExport.Run(input, root, export);
            if (rc != 0) return false;
            byte[] after = File.ReadAllBytes(input);
            return BytesEqual(raw, after);
        }

        private static bool EmptyAccepted()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            File.WriteAllBytes(input, new byte[0]);

            int rc = PromptExport.Run(input, root, export);
            if (rc != 0) return false;
            FileInfo info = new FileInfo(export);
            return info.Exists && info.Length == 0;
        }

        private static bool ExactOneMiBAccepted()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            byte[] raw = new byte[1048576];
            for (int i = 0; i < raw.Length; i++) raw[i] = 0x61;
            File.WriteAllBytes(input, raw);

            int rc = PromptExport.Run(input, root, export);
            if (rc != 0) return false;
            byte[] outBytes = File.ReadAllBytes(export);
            return BytesEqual(raw, outBytes);
        }

        private static bool DuplicateExportRejected()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            byte[] raw = Encoding.UTF8.GetBytes("first\r\n");
            File.WriteAllBytes(input, raw);

            if (PromptExport.Run(input, root, export) != 0) return false;
            byte[] first = File.ReadAllBytes(export);

            if (PromptExport.Run(input, root, export) == 0) return false;
            byte[] kept = File.ReadAllBytes(export);
            if (!BytesEqual(first, kept)) return false;

            string other = Path.Combine(root, "other.txt");
            File.WriteAllBytes(other, Encoding.UTF8.GetBytes("other\r\n"));
            if (PromptExport.Run(other, root, export) == 0) return false;
            return BytesEqual(first, File.ReadAllBytes(export));
        }

        private static bool SiblingPrefixRejected()
        {
            string root = CreateIsolatedRoot();
            string sibling = root + "-sibling";
            Directory.CreateDirectory(sibling);

            string insideInput = Path.Combine(root, "prompt.txt");
            File.WriteAllBytes(insideInput, Encoding.UTF8.GetBytes("hi\r\n"));

            string siblingInput = Path.Combine(sibling, "prompt.txt");
            File.WriteAllBytes(siblingInput, Encoding.UTF8.GetBytes("hi\r\n"));
            string siblingExport = Path.Combine(root, "export.txt");
            if (PromptExport.Run(siblingInput, root, siblingExport) == 0) return false;

            string siblingTarget = Path.Combine(sibling, "export.txt");
            if (PromptExport.Run(insideInput, root, siblingTarget) == 0) return false;
            return true;
        }

        private static bool TraversalRejected()
        {
            string root = CreateIsolatedRoot();
            string insideInput = Path.Combine(root, "prompt.txt");
            File.WriteAllBytes(insideInput, Encoding.UTF8.GetBytes("hi\r\n"));

            string outsideDir = Path.Combine(
                Path.GetTempPath(),
                "murmur-next-export-outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outsideDir);
            string outsideInput = Path.Combine(outsideDir, "prompt.txt");
            File.WriteAllBytes(outsideInput, Encoding.UTF8.GetBytes("hi\r\n"));

            string traversalInput = Path.Combine(root, "..", new DirectoryInfo(outsideDir).Name, "prompt.txt");
            if (PromptExport.Run(traversalInput, root, Path.Combine(root, "export1.txt")) == 0) return false;

            string traversalExport = Path.Combine(root, "..", new DirectoryInfo(outsideDir).Name, "export.txt");
            if (PromptExport.Run(insideInput, root, traversalExport) == 0) return false;
            return true;
        }

        private static bool ReparseRejected()
        {
            string root = CreateIsolatedRoot();
            string outside = CreateIsolatedRoot();
            string link = Path.Combine(root, "link-" + Guid.NewGuid().ToString("N"));
            if (!TryCreateDirLink(link, outside))
            {
                Skip("ReparseRejected");
                return true;
            }

            string realFile = Path.Combine(outside, "prompt.txt");
            File.WriteAllBytes(realFile, Encoding.UTF8.GetBytes("via link\r\n"));
            string linkInput = Path.Combine(link, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            if (PromptExport.Run(linkInput, root, export) == 0) return false;
            if (File.Exists(export)) return false;

            string insideInput = Path.Combine(root, "prompt.txt");
            File.WriteAllBytes(insideInput, Encoding.UTF8.GetBytes("hi\r\n"));
            string linkExport = Path.Combine(link, "export.txt");
            if (PromptExport.Run(insideInput, root, linkExport) == 0) return false;
            return true;
        }

        private static bool InvalidUtf8Rejected()
        {
            string root = CreateIsolatedRoot();
            byte[][] cases = new byte[][]
            {
                new byte[] { 0x48, 0xFF, 0x69 },
                new byte[] { 0xE2, 0x82 },
                new byte[] { 0xC0, 0xAF },
                new byte[] { 0xED, 0xA0, 0x80 }
            };
            for (int i = 0; i < cases.Length; i++)
            {
                string input = Path.Combine(root, "bad" + i + ".txt");
                string export = Path.Combine(root, "export" + i + ".txt");
                File.WriteAllBytes(input, cases[i]);
                if (PromptExport.Run(input, root, export) == 0) return false;
                if (File.Exists(export) || Directory.Exists(export)) return false;
            }
            return true;
        }

        private static bool OversizeRejected()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            string export = Path.Combine(root, "export.txt");
            byte[] raw = new byte[1048577];
            for (int i = 0; i < raw.Length; i++) raw[i] = 0x61;
            File.WriteAllBytes(input, raw);

            if (PromptExport.Run(input, root, export) == 0) return false;
            return !File.Exists(export) && !Directory.Exists(export);
        }

        private static bool InterruptedFailureLeavesNoPartial()
        {
            string root = CreateIsolatedRoot();
            string badInput = Path.Combine(root, "bad.txt");
            string export = Path.Combine(root, "export.txt");
            File.WriteAllBytes(badInput, new byte[] { 0xFF, 0xFE, 0x41 });

            if (PromptExport.Run(badInput, root, export) == 0) return false;
            if (File.Exists(export) || Directory.Exists(export)) return false;
            if (StagingLeftovers(root, "export.txt")) return false;

            string goodInput = Path.Combine(root, "good.txt");
            byte[] raw = Encoding.UTF8.GetBytes("kept\r\n");
            File.WriteAllBytes(goodInput, raw);
            if (PromptExport.Run(goodInput, root, export) != 0) return false;
            byte[] kept = File.ReadAllBytes(export);
            if (PromptExport.Run(badInput, root, export) == 0) return false;
            if (!BytesEqual(kept, File.ReadAllBytes(export))) return false;
            return !StagingLeftovers(root, "export.txt");
        }

        private static bool StagingLeftovers(string directory, string exportName)
        {
            string[] leftovers;
            try
            {
                leftovers = Directory.GetFiles(directory, exportName + ".stg-*.tmp");
            }
            catch
            {
                return true;
            }
            return leftovers.Length != 0;
        }

        private static bool MissingRootAndRelativeRejected()
        {
            string root = CreateIsolatedRoot();
            string input = Path.Combine(root, "prompt.txt");
            File.WriteAllBytes(input, Encoding.UTF8.GetBytes("hi\r\n"));
            string export = Path.Combine(root, "export.txt");

            string missingRoot = Path.Combine(root, "no-such-root-" + Guid.NewGuid().ToString("N"));
            if (PromptExport.Run(input, missingRoot, export) == 0) return false;
            if (PromptExport.Run("relative\\prompt.txt", root, export) == 0) return false;
            if (PromptExport.Run(input, root, "relative\\export.txt") == 0) return false;
            return true;
        }

        private static bool TryCreateDirLink(string linkPath, string targetDir)
        {
            try
            {
                if (CreateSymbolicLink(linkPath, targetDir, 3)) return true;
            }
            catch
            {
            }
            try
            {
                return TryCreateJunction(linkPath, targetDir);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCreateJunction(string junctionPath, string targetDir)
        {
            Directory.CreateDirectory(junctionPath);

            IntPtr handle = CreateFile(
                junctionPath,
                0x40000000u,
                0u,
                IntPtr.Zero,
                3u,
                0x02000000u | 0x00200000u,
                IntPtr.Zero);
            if (handle == new IntPtr(-1)) return false;

            try
            {
                string substitute = "\\??\\" + targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string print = targetDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                byte[] substituteBytes = Encoding.Unicode.GetBytes(substitute + "\0");
                byte[] printBytes = Encoding.Unicode.GetBytes(print + "\0");
                int dataLength = 8 + substituteBytes.Length + printBytes.Length;
                int totalLength = 8 + dataLength;
                IntPtr buffer = Marshal.AllocHGlobal(totalLength);
                try
                {
                    Marshal.WriteInt32(buffer, 0, unchecked((int)0xA0000003));
                    Marshal.WriteInt16(buffer, 4, (short)dataLength);
                    Marshal.WriteInt16(buffer, 6, 0);
                    Marshal.WriteInt16(buffer, 8, 0);
                    Marshal.WriteInt16(buffer, 10, (short)substituteBytes.Length);
                    Marshal.WriteInt16(buffer, 12, (short)substituteBytes.Length);
                    Marshal.WriteInt16(buffer, 14, (short)printBytes.Length);
                    Marshal.Copy(substituteBytes, 0, IntPtr.Add(buffer, 16), substituteBytes.Length);
                    Marshal.Copy(printBytes, 0, IntPtr.Add(buffer, 16 + substituteBytes.Length), printBytes.Length);
                    int bytesReturned;
                    bool ok = DeviceIoControl(
                        handle,
                        0x000900A4u,
                        buffer,
                        totalLength,
                        IntPtr.Zero,
                        0,
                        out bytesReturned,
                        IntPtr.Zero);
                    return ok;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            int nInBufferSize,
            IntPtr lpOutBuffer,
            int nOutBufferSize,
            out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}

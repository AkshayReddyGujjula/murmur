using System;
using System.IO;
using System.Text;

namespace Murmur.Handoff
{
    // Copies a finalized prompt file to a destination inside the owned session
    // directory. Byte-preserving (BOM, Unicode, CRLF, trailing whitespace).
    // Never edits or deletes the source. Never logs transcript content.
    //
    // Status: 0 success, nonzero failure. No content in diagnostics.
    //   10/11/12  prompt/session/destination path malformed (not strict
    //             absolute, invalid chars, ADS colon, device path, reserved name)
    //   13        session directory missing
    //   14        prompt or destination escapes the session (traversal, sibling)
    //   15        source and destination are identical
    //   16        reparse point in the accepted chain (fail closed, incl. missing)
    //   17        destination already exists / publish conflict
    //   18        source open or read failed (missing chain nodes use 16)
    //   19        input larger than 1 MiB (hard allocation cap)
    //   20        input is not strict UTF-8
    //   21        staging write or publish failure
    public static class PromptHandoff
    {
        public const int MaxInputBytes = 1048576; // 1 MiB hard cap, single allocation.

        public static int Export(string promptPath, string sessionDirectory, string destination)
        {
            string promptFull;
            if (!TryCanonicalize(promptPath, out promptFull))
                return 10;
            string sessionFull;
            if (!TryCanonicalize(sessionDirectory, out sessionFull))
                return 11;
            string destFull;
            if (!TryCanonicalize(destination, out destFull))
                return 12;

            bool sessionExists;
            try
            {
                sessionExists = Directory.Exists(sessionFull);
            }
            catch
            {
                return 13;
            }
            if (!sessionExists)
                return 13;

            if (!IsStrictlyInside(promptFull, sessionFull))
                return 14;
            if (!IsStrictlyInside(destFull, sessionFull))
                return 14;

            if (string.Equals(promptFull, destFull, StringComparison.OrdinalIgnoreCase))
                return 15;

            if (ChainHasReparse(sessionFull, promptFull, true))
                return 16;
            if (ChainHasReparse(sessionFull, destFull, false))
                return 16;

            try
            {
                if (File.Exists(destFull) || Directory.Exists(destFull))
                    return 17;
            }
            catch
            {
                return 17;
            }

            byte[] raw;
            try
            {
                // FileShare.Read denies writers: a concurrent write fails closed
                // here instead of yielding torn bytes.
                using (FileStream input = new FileStream(promptFull, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    long length = input.Length;
                    if (length < 0 || length > MaxInputBytes)
                        return 19;
                    raw = new byte[(int)length];
                    int offset = 0;
                    while (offset < raw.Length)
                    {
                        int read = input.Read(raw, offset, raw.Length - offset);
                        if (read == 0)
                            break;
                        offset += read;
                    }
                    if (offset < raw.Length)
                        return 18; // Never publish a truncated source as successful.
                    else if (input.ReadByte() != -1)
                        return 19; // Grew past the cap after Length was taken.
                }
            }
            catch (IOException)
            {
                return 18;
            }
            catch (UnauthorizedAccessException)
            {
                return 18;
            }
            catch (ArgumentException)
            {
                return 18;
            }
            catch (NotSupportedException)
            {
                return 18;
            }

            // Strict validation only; raw bytes (incl. BOM) are written verbatim.
            try
            {
                new UTF8Encoding(false, true).GetString(raw, 0, raw.Length);
            }
            catch (DecoderFallbackException)
            {
                return 20;
            }
            catch (ArgumentException)
            {
                return 20;
            }

            string parent;
            try
            {
                parent = Path.GetDirectoryName(destFull);
            }
            catch
            {
                return 21;
            }
            if (parent == null)
                return 21;

            // Unique sibling staging (same directory => same volume => atomic
            // rename). CreateNew never overwrites; Move throws if dest appeared.
            string stagingFull = parent + "\\" + Path.GetFileName(destFull) + "." + Guid.NewGuid().ToString("N") + ".staging";
            bool published = false;
            bool created = false;
            try
            {
                using (FileStream output = new FileStream(stagingFull, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    if (raw.Length > 0)
                        output.Write(raw, 0, raw.Length);
                    output.Flush();
                    output.Flush(true);
                }
                File.Move(stagingFull, destFull);
                published = true;
                return 0;
            }
            catch (IOException)
            {
                int code = 21;
                try
                {
                    if (File.Exists(destFull) || Directory.Exists(destFull))
                        code = 17;
                }
                catch
                {
                }
                return code;
            }
            catch (UnauthorizedAccessException)
            {
                return 21;
            }
            catch (ArgumentException)
            {
                return 21;
            }
            catch (NotSupportedException)
            {
                return 21;
            }
            finally
            {
                // Remove only the exact staging file this call created.
                if (created && !published && stagingFull != null)
                {
                    try
                    {
                        if (File.Exists(stagingFull))
                            File.Delete(stagingFull);
                    }
                    catch
                    {
                    }
                }
            }
        }

        static bool IsStrictlyInside(string full, string sessionFull)
        {
            string prefix = sessionFull + "\\";
            return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        // Fail-closed walk from the target (or its parent) up to the session
        // root inclusive. Missing nodes and attribute errors reject.
        static bool ChainHasReparse(string sessionFull, string targetFull, bool includeTarget)
        {
            string node;
            try
            {
                node = includeTarget ? targetFull : Path.GetDirectoryName(targetFull);
            }
            catch
            {
                return true;
            }
            if (node == null)
                return true;
            while (true)
            {
                bool atSession = string.Equals(node, sessionFull, StringComparison.OrdinalIgnoreCase);
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(node);
                }
                catch
                {
                    return true;
                }
                if ((attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                    return true;
                if (atSession)
                    return false;
                string parent;
                try
                {
                    parent = Path.GetDirectoryName(node);
                }
                catch
                {
                    return true;
                }
                if (parent == null || parent.Length < sessionFull.Length)
                    return true;
                node = parent;
            }
        }

        // Strict absolute Windows path: drive letter (X:\) or UNC (\\s\s).
        // Rejects drive-relative (C:x), root-relative (\x), device paths
        // (\\?\, \\.\, \??\), ADS colons, wildcards/quotes, trailing dots or
        // spaces, and reserved device names, despite Path.IsPathRooted quirks.
        static bool TryCanonicalize(string path, out string full)
        {
            full = null;
            if (string.IsNullOrEmpty(path))
                return false;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c < 0x20 || c == 0x7F)
                    return false;
            }
            string p = path.Replace('/', '\\');
            while (p.Length > 3 && p[p.Length - 1] == '\\')
                p = p.Substring(0, p.Length - 1);
            if (!IsStrictForm(p))
                return false;
            string canon;
            try
            {
                canon = Path.GetFullPath(p);
            }
            catch
            {
                return false;
            }
            canon = canon.Replace('/', '\\');
            while (canon.Length > 3 && canon[canon.Length - 1] == '\\')
                canon = canon.Substring(0, canon.Length - 1);
            if (!IsStrictForm(canon))
                return false;
            full = canon;
            return true;
        }

        static bool IsStrictForm(string p)
        {
            if (p == null || p.Length == 0)
                return false;
            bool isDrive = p.Length >= 3 && IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';
            bool isUnc = false;
            if (!isDrive)
            {
                if (!(p.Length >= 5 && p[0] == '\\' && p[1] == '\\'))
                    return false;
                if (p[2] == '\\' || p[2] == '?' || p[2] == '.')
                    return false;
                int sep = p.IndexOf('\\', 3);
                if (sep < 3 || sep >= p.Length - 1)
                    return false;
                isUnc = true;
            }
            string[] segs = p.Split('\\');
            int i = isDrive ? 1 : 2;
            if (isDrive)
            {
                if (segs[0].Length != 2 || segs[0][1] != ':' || !IsAsciiLetter(segs[0][0]))
                    return false;
            }
            else
            {
                if (segs[0].Length != 0 || segs[1].Length != 0)
                    return false;
            }
            for (; i < segs.Length; i++)
            {
                string s = segs[i];
                if (s.Length == 0)
                    return false;
                if (s[s.Length - 1] == ' ' || s[s.Length - 1] == '.')
                    return false;
                if (s == "." || s == "..")
                    continue; // Resolved by GetFullPath; containment decides.
                for (int j = 0; j < s.Length; j++)
                {
                    char c = s[j];
                    if (c == '<' || c == '>' || c == '"' || c == '|' || c == '?' || c == '*' || c == ':')
                        return false;
                }
                string stem = s;
                int dot = s.IndexOf('.');
                if (dot >= 0)
                    stem = s.Substring(0, dot);
                if (IsReservedName(stem))
                    return false;
            }
            return isDrive || isUnc;
        }

        static bool IsAsciiLetter(char c)
        {
            return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        }

        static bool IsReservedName(string stem)
        {
            if (stem.Length == 3)
            {
                string u = stem.ToUpperInvariant();
                return u == "CON" || u == "PRN" || u == "AUX" || u == "NUL";
            }
            if (stem.Length == 4)
            {
                string u = stem.ToUpperInvariant();
                if (u[0] == 'C' && u[1] == 'O' && u[2] == 'M' && u[3] >= '1' && u[3] <= '9')
                    return true;
                if (u[0] == 'L' && u[1] == 'P' && u[2] == 'T' && u[3] >= '1' && u[3] <= '9')
                    return true;
            }
            return false;
        }
    }
}

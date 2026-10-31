using System;
using System.Text;

namespace Murmur.App.Engine
{
    public enum ScreenKind { Unknown, Ready, Listening, NeedsSignIn, NeedsSetup }

    public sealed class ScreenReading
    {
        public ScreenKind Kind;
        public bool HasComposer;
        public string ComposerText;
    }

    public static class ScreenParser
    {
        private const char PromptGlyph = '\u276f';   // ❯
        private const char RuleGlyph = '\u2500';     // ─
        private const char CursorGlyph = '\u2581';   // ▁

        public static ScreenReading Read(string snapshot)
        {
            ScreenReading reading = new ScreenReading();
            reading.Kind = ScreenKind.Unknown;
            reading.HasComposer = false;
            reading.ComposerText = "";
            if (snapshot == null)
            {
                return reading;
            }

            string[] lines = snapshot.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = TrimLine(lines[i]);
            }

            ParseComposer(lines, reading);
            reading.Kind = DetectKind(lines, reading.HasComposer);
            return reading;
        }

        private static string TrimLine(string line)
        {
            if (line == null)
            {
                return "";
            }
            int end = line.Length;
            while (end > 0 && (line[end - 1] == '\r' || line[end - 1] == ' ' || line[end - 1] == '\t'))
            {
                end--;
            }
            return line.Substring(0, end);
        }

        private static bool IsRule(string line)
        {
            string value = line.Trim();
            if (value.Length == 0)
            {
                return false;
            }
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != RuleGlyph)
                {
                    return false;
                }
            }
            return true;
        }

        private static int PromptIndex(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == ' ' || line[i] == '\t')
                {
                    continue;
                }
                return line[i] == PromptGlyph ? i : -1;
            }
            return -1;
        }

        private static void ParseComposer(string[] lines, ScreenReading reading)
        {
            // Prefer the lowest composer on screen: it is the actual input area.
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                int prompt = PromptIndex(lines[i]);
                if (prompt < 0)
                {
                    continue;
                }

                bool hasRuleAbove = false;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (IsRule(lines[j]))
                    {
                        hasRuleAbove = true;
                        break;
                    }
                }
                if (!hasRuleAbove)
                {
                    continue;
                }

                int ruleBelow = -1;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (IsRule(lines[j]))
                    {
                        ruleBelow = j;
                        break;
                    }
                }
                if (ruleBelow < 0)
                {
                    continue;
                }

                StringBuilder builder = new StringBuilder();
                AppendComposerLine(builder, lines[i].Substring(prompt + 1));
                for (int j = i + 1; j < ruleBelow; j++)
                {
                    AppendComposerLine(builder, lines[j]);
                }

                string text = builder.ToString().Trim();
                if (text.Length == 1 && text[0] == CursorGlyph)
                {
                    text = "";
                }
                reading.HasComposer = true;
                reading.ComposerText = text;
                return;
            }
        }

        private static void AppendComposerLine(StringBuilder builder, string line)
        {
            string value = line.Trim();
            if (value.Length == 0)
            {
                return;
            }
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(value);
        }

        private static ScreenKind DetectKind(string[] lines, bool hasComposer)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (Contains(lines[i], "listening\u2026") || Contains(lines[i], "listening..."))
                {
                    return ScreenKind.Listening;
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (Contains(line, "/login") || Contains(line, "Not logged in") ||
                    Contains(line, "Invalid API key") || Contains(line, "Please run"))
                {
                    return ScreenKind.NeedsSignIn;
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (Contains(line, "Do you trust") || Contains(line, "Yes, proceed") ||
                    Contains(line, "Choose the text style") || Contains(line, "Press Enter to continue"))
                {
                    return ScreenKind.NeedsSetup;
                }
            }

            return hasComposer ? ScreenKind.Ready : ScreenKind.Unknown;
        }

        private static bool Contains(string value, string phrase)
        {
            return value.IndexOf(phrase, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}

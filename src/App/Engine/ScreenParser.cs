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
        // Labels on the composer's borders when Claude scrolls a long prompt
        // (e.g. "↑ 3 more"); "" when the prompt fits. ComposerText then holds
        // only the visible window, so callers compare both to detect change.
        public string ComposerScrollMarker;
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
            reading.ComposerScrollMarker = "";
            if (snapshot == null)
            {
                return reading;
            }

            string[] lines = snapshot.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = TrimLine(lines[i]);
            }

            int composerStart;
            int composerEnd;
            ParseComposer(lines, reading, out composerStart, out composerEnd);
            reading.Kind = DetectKind(lines, reading.HasComposer, composerStart, composerEnd);
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

        // Claude Code 2.1.326+ labels the composer borders of a long, scrolled
        // prompt: "─── ↑ 3 more ─────…" on top, "─── ↓ 2 more ───…" below.
        // IsLabeledRule matches the shape (rule run, short label, rule run).
        // Only labels carrying the scroll arrow and a count are composer
        // borders, so titled dialog separators ("── Sign in ──") never are.
        private const int MinLabelRuleRun = 2;
        private const int MaxBorderLabelLength = 40;
        private const char ScrollUpGlyph = '\u2191';     // ↑
        private const char ScrollDownGlyph = '\u2193';   // ↓
        // ComposerText when a scrolled composer shows no visible words: text
        // is still out of view, so the composer is not empty. Never delivered.
        internal const string HiddenTextPlaceholder = "\u2026";

        private static bool IsLabeledRule(string line, out string label)
        {
            label = "";
            string value = line.Trim();
            int lead = 0;
            while (lead < value.Length && value[lead] == RuleGlyph)
            {
                lead++;
            }
            int trail = 0;
            while (trail < value.Length - lead && value[value.Length - 1 - trail] == RuleGlyph)
            {
                trail++;
            }
            if (lead < MinLabelRuleRun || trail < MinLabelRuleRun)
            {
                return false;
            }
            string middle = value.Substring(lead, value.Length - lead - trail).Trim();
            if (middle.Length == 0 || middle.Length > MaxBorderLabelLength || middle.IndexOf(RuleGlyph) >= 0)
            {
                return false;
            }
            label = middle;
            return true;
        }

        private static bool IsScrollBorder(string line, char arrow)
        {
            string label;
            if (!IsLabeledRule(line, out label) || label.IndexOf(arrow) < 0)
            {
                return false;
            }
            for (int i = 0; i < label.Length; i++)
            {
                if (char.IsDigit(label[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // Composer borders start at column 0 in every captured layout; an
        // indented row of rule glyphs is dictated text in the body.
        private static bool StartsAtColumnZero(string line)
        {
            return line.Length > 0 && line[0] == RuleGlyph;
        }

        private static bool IsTopBorder(string line)
        {
            return StartsAtColumnZero(line) && (IsRule(line) || IsScrollBorder(line, ScrollUpGlyph));
        }

        private static bool IsBottomBorder(string line)
        {
            return StartsAtColumnZero(line) && (IsRule(line) || IsScrollBorder(line, ScrollDownGlyph));
        }

        private static string BorderLabel(string line)
        {
            string label;
            return IsLabeledRule(line, out label) ? label : "";
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

        private static void ParseComposer(string[] lines, ScreenReading reading, out int composerStart, out int composerEnd)
        {
            composerStart = -1;
            composerEnd = -1;

            // Prefer the lowest composer on screen: it is the actual input area.
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                int top;
                int prompt = PromptIndex(lines[i]);
                if (prompt >= 0)
                {
                    top = FindLine(lines, i - 1, -1, true);
                    if (top < 0)
                    {
                        continue;
                    }
                    if (!IsScrollBorder(lines[top], ScrollUpGlyph))
                    {
                        int bottom = FindLine(lines, i + 1, 1, false);
                        if (bottom < 0)
                        {
                            continue;
                        }
                        SetComposer(reading, lines, lines[i].Substring(prompt + 1), i + 1, bottom,
                            ScrollMarker(lines[top], lines[bottom]));
                        composerStart = i;
                        composerEnd = bottom - 1;
                        return;
                    }
                    // Under a scrolled top border the real prompt line is out
                    // of view: this glyph is dictated text in the body.
                }
                else if (IsScrollBorder(lines[i], ScrollUpGlyph))
                {
                    top = i;
                }
                else
                {
                    continue;
                }

                // A scrolled long prompt: the whole body between the labeled
                // top border and the next bottom border is composer text.
                int scrolledBottom = FindLine(lines, top + 1, 1, false);
                if (scrolledBottom < 0)
                {
                    continue;
                }
                SetComposer(reading, lines, "", top + 1, scrolledBottom,
                    ScrollMarker(lines[top], lines[scrolledBottom]));
                composerStart = top + 1;
                composerEnd = scrolledBottom - 1;
                return;
            }
        }

        private static int FindLine(string[] lines, int start, int step, bool topBorder)
        {
            for (int j = start; j >= 0 && j < lines.Length; j += step)
            {
                if (topBorder ? IsTopBorder(lines[j]) : IsBottomBorder(lines[j]))
                {
                    return j;
                }
            }
            return -1;
        }

        private static string ScrollMarker(string top, string bottom)
        {
            string above = BorderLabel(top);
            string below = BorderLabel(bottom);
            return above.Length == 0 && below.Length == 0 ? "" : above + "|" + below;
        }

        private static void SetComposer(ScreenReading reading, string[] lines, string first,
            int bodyStart, int bodyEnd, string marker)
        {
            StringBuilder builder = new StringBuilder();
            AppendComposerLine(builder, first);
            for (int j = bodyStart; j < bodyEnd; j++)
            {
                AppendComposerLine(builder, lines[j]);
            }

            string text = builder.ToString().Trim();
            if (text.Length == 1 && text[0] == CursorGlyph)
            {
                text = "";
            }
            if (text.Length == 0 && marker.Length > 0)
            {
                text = HiddenTextPlaceholder;
            }
            reading.HasComposer = true;
            reading.ComposerText = text;
            reading.ComposerScrollMarker = marker;
        }

        // Version from the welcome banner, or null. Diagnostic only. Searches
        // only above the first border or prompt so dictated text never counts.
        public static string ClaudeVersion(string snapshot)
        {
            if (snapshot == null)
            {
                return null;
            }
            const string Marker = "Claude Code v";
            string[] lines = snapshot.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = TrimLine(lines[i]);
                string label;
                if (IsRule(line) || IsLabeledRule(line, out label) || PromptIndex(line) >= 0)
                {
                    return null;
                }
                int at = line.IndexOf(Marker, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }
                int start = at + Marker.Length;
                int end = start;
                while (end < line.Length && end - start < 24 && (char.IsDigit(line[end]) || line[end] == '.'))
                {
                    end++;
                }
                return end > start ? line.Substring(start, end - start) : null;
            }
            return null;
        }

        // Structural summary for logs when the composer cannot be found. Lists
        // row numbers of borders and prompt glyphs only; never screen text.
        public static string Describe(string snapshot)
        {
            if (snapshot == null)
            {
                return "no screen";
            }
            string[] lines = snapshot.Split('\n');
            StringBuilder rules = new StringBuilder();
            StringBuilder labeled = new StringBuilder();
            StringBuilder prompts = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = TrimLine(lines[i]);
                string label;
                if (IsRule(line)) AppendRow(rules, i);
                else if (IsLabeledRule(line, out label)) AppendRow(labeled, i);
                if (PromptIndex(line) >= 0) AppendRow(prompts, i);
            }
            return "rows=" + lines.Length + " rules=[" + rules + "] labeled=[" + labeled +
                "] prompts=[" + prompts + "]";
        }

        private static void AppendRow(StringBuilder builder, int row)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }
            builder.Append(row);
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

        private static ScreenKind DetectKind(string[] lines, bool hasComposer, int composerStart, int composerEnd)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                // Dictated text can contain the word "listening". Only status
                // lines outside the composer block may confirm listening.
                if (composerStart >= 0 && i >= composerStart && i <= composerEnd)
                {
                    continue;
                }
                if (Contains(lines[i], "listening\u2026") || Contains(lines[i], "listening..."))
                {
                    return ScreenKind.Listening;
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (composerStart >= 0 && i >= composerStart && i <= composerEnd)
                {
                    continue;
                }
                string line = lines[i];
                if (Contains(line, "/login") || Contains(line, "Not logged in") ||
                    Contains(line, "Invalid API key") || Contains(line, "Please run"))
                {
                    return ScreenKind.NeedsSignIn;
                }
            }

            for (int i = 0; i < lines.Length; i++)
            {
                if (composerStart >= 0 && i >= composerStart && i <= composerEnd)
                {
                    continue;
                }
                string line = lines[i];
                if (Contains(line, "Do you trust") || Contains(line, "Yes, proceed") ||
                    Contains(line, "Yes, I trust this folder") ||
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

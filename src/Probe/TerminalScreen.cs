using System;
using System.Collections.Generic;
using System.Text;

// Bounded in-memory VT screen for the feasibility probe.
//
// Purpose: host a hidden child pseudoterminal's output stream in memory so
// terminal escape queries (DSR, DA, ...) can be answered from the grid state
// without ever reaching the real user console. Snapshot() is a diagnostic
// view of the visible grid only; it is NOT transcript extraction and MUST NOT
// be used as prompt-completion evidence.
//
// Supported (documented minimum for the probe):
//   Printable characters, CR, LF (FF/VT treated as LF), BS, TAB.
//   Cursor: CUU/CUD/CUF/CUB (A/B/C/D), CUP/H-V (H/f), CHA (G), VPA (d).
//   Erase: ED (J with 0/1/2; 3 treated as 2), EL (K with 0/1/2).
//   Save/restore cursor: CSI s/u and ESC 7/8. Scroll on line feed past the
//   bottom margin and on reverse index (ESC M).
//   Replies (via the Feed reply callback, never written into the grid):
//     CSI 6 n cursor position report, CSI 5 n status OK,
//     primary DA (CSI c) and secondary DA (CSI > c) minimal static replies,
//     kitty keyboard query (CSI ? u) reporting no enhanced support,
//     OSC 10/11 color queries with a bounded static reply.
//   Ignored (never rendered, never executed): SGR and other attribute/mode
//   sequences (m, h, l and unknown CSI finals), all OSC payloads including
//   hyperlinks (OSC 8), window titles, clipboard (OSC 52), APC/DCS/SOS/PM,
//   charset selections (ESC ( X etc.), SS3 (ESC O X), BEL, SO/SI.
//
// Line wrap is deferred (wrap-pending flag): printing in the last column
// sets the flag without moving; the next printable wraps first. CR cancels
// the flag, LF consumes it with a single line advance, so full-width lines
// terminated by CR/LF advance exactly one row.
//
// Explicitly NOT supported (honest gaps, out of scope for the probe):
//   No scrollback buffer, no alternate screen, no scroll regions/margins,
//   no wide/combining character cells (every char is one cell), no UTF-8
//   decoding (Feed takes decoded string chunks), no line insert/delete
//   (L/M), no character insert/delete (@/P/X), fixed tab stops of 8,
//   no colors/attributes stored, no mouse/keyboard encoding, no focus or
//   bracketed-paste reports, no sixel/graphics, no true device emulation.
//
// Boundedness: the grid is capped at 240 columns x 80 rows. Unfinished
// escape-sequence state is bounded (MaxPendingLength); an overlong CSI/OSC
// is swallowed (never rendered) up to its terminator and then dropped, and
// APC/DCS/SOS/PM payloads are swallowed without storing anything, so a
// hostile child cannot grow memory unboundedly or leave escape payload
// visible as transcript text.
namespace Murmur.Probe
{
    public sealed class TerminalScreen
    {
        public const int MaxColumns = 240;
        public const int MaxRows = 80;

        // Maximum buffered length of one unfinished escape sequence
        // (CSI parameter bytes or OSC payload) before it is dropped.
        public const int MaxPendingLength = 1024;

        private const int StateGround = 0;
        private const int StateEsc = 1;
        private const int StateCsi = 2;
        private const int StateOsc = 3;
        private const int StateOscEsc = 4;
        private const int StateSkipSt = 5;
        private const int StateSkipStEsc = 6;

        private readonly object _lock = new object();
        private readonly int _columns;
        private readonly int _rows;
        private readonly char[,] _grid;
        private int _cursorRow;
        private int _cursorCol;
        private int _savedRow;
        private int _savedCol;
        private bool _wrapPending;
        private bool _savedWrap;
        private int _state = StateGround;
        private readonly StringBuilder _csi = new StringBuilder();
        private readonly StringBuilder _osc = new StringBuilder();
        private bool _csiOverflow;
        private bool _oscOverflow;
        // ESC followed by one of ()#% : one charset byte still to swallow.
        private bool _escCharsetPending;

        public TerminalScreen(int columns, int rows)
        {
            if (columns < 1 || columns > MaxColumns)
            {
                throw new ArgumentOutOfRangeException("columns");
            }
            if (rows < 1 || rows > MaxRows)
            {
                throw new ArgumentOutOfRangeException("rows");
            }
            _columns = columns;
            _rows = rows;
            _grid = new char[rows, columns];
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < columns; c++)
                {
                    _grid[r, c] = ' ';
                }
            }
        }

        // Feeds one decoded chunk of child output. Parser state persists
        // across calls, so CSI/OSC sequences split across chunks are handled.
        // Query replies are collected under the lock but delivered through
        // reply AFTER the lock is released, so a re-entrant receiver cannot
        // deadlock the screen.
        public void Feed(string chunk, Action<string> reply)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException("chunk");
            }
            if (reply == null)
            {
                throw new ArgumentNullException("reply");
            }
            List<string> replies = null;
            lock (_lock)
            {
                for (int i = 0; i < chunk.Length; i++)
                {
                    char ch = chunk[i];
                    string answer = ConsumeLocked(ch);
                    if (answer != null)
                    {
                        if (replies == null)
                        {
                            replies = new List<string>();
                        }
                        replies.Add(answer);
                    }
                }
            }
            if (replies != null)
            {
                for (int i = 0; i < replies.Count; i++)
                {
                    reply(replies[i]);
                }
            }
        }

        // Diagnostic snapshot of the visible grid: rows joined with "\n",
        // trailing spaces trimmed per line, trailing blank lines trimmed.
        public string Snapshot()
        {
            lock (_lock)
            {
                StringBuilder sb = new StringBuilder(_rows * (_columns + 1));
                int lastUsed = -1;
                string[] lines = new string[_rows];
                for (int r = 0; r < _rows; r++)
                {
                    int end = _columns;
                    while (end > 0 && _grid[r, end - 1] == ' ')
                    {
                        end--;
                    }
                    if (end > 0)
                    {
                        lastUsed = r;
                    }
                    lines[r] = new string(RowSlice(r, end));
                }
                for (int r = 0; r <= lastUsed; r++)
                {
                    if (r > 0)
                    {
                        sb.Append('\n');
                    }
                    sb.Append(lines[r]);
                }
                return sb.ToString();
            }
        }

        private char[] RowSlice(int row, int length)
        {
            char[] slice = new char[length];
            for (int c = 0; c < length; c++)
            {
                slice[c] = _grid[row, c];
            }
            return slice;
        }

        // Returns a reply string to queue, or null. Caller holds _lock.
        private string ConsumeLocked(char ch)
        {
            switch (_state)
            {
                case StateEsc:
                    return ConsumeEscLocked(ch);
                case StateCsi:
                    return ConsumeCsiLocked(ch);
                case StateOsc:
                    return ConsumeOscLocked(ch);
                case StateOscEsc:
                    return ConsumeOscEscLocked(ch);
                case StateSkipSt:
                    return ConsumeSkipStLocked(ch, false);
                case StateSkipStEsc:
                    return ConsumeSkipStLocked(ch, true);
                default:
                    return ConsumeGroundLocked(ch);
            }
        }

        private string ConsumeGroundLocked(char ch)
        {
            switch (ch)
            {
                case '\x1b':
                    _state = StateEsc;
                    _escCharsetPending = false;
                    return null;
                case '\x9b':
                    _state = StateCsi;
                    _csi.Length = 0;
                    return null;
                case '\x9d':
                    _state = StateOsc;
                    _osc.Length = 0;
                    return null;
                case '\r':
                    _cursorCol = 0;
                    _wrapPending = false;
                    return null;
                case '\n':
                case '\v':
                case '\f':
                    _wrapPending = false;
                    LineFeedLocked();
                    return null;
                case '\b':
                    if (_wrapPending)
                    {
                        _wrapPending = false;
                    }
                    else if (_cursorCol > 0)
                    {
                        _cursorCol--;
                    }
                    return null;
                case '\t':
                    if (_wrapPending)
                    {
                        _wrapPending = false;
                        _cursorCol = 0;
                        LineFeedLocked();
                    }
                    _cursorCol = ((_cursorCol / 8) + 1) * 8;
                    if (_cursorCol >= _columns)
                    {
                        _cursorCol = _columns - 1;
                        _wrapPending = true;
                    }
                    return null;
                case '\a':
                case '\x0e':
                case '\x0f':
                    // BEL / SO / SI: no visible effect for the probe.
                    return null;
                default:
                    if (ch < ' ')
                    {
                        return null;
                    }
                    PutLocked(ch);
                    return null;
            }
        }

        private string ConsumeEscLocked(char ch)
        {
            if (_escCharsetPending)
            {
                _escCharsetPending = false;
                _state = StateGround;
                return null;
            }
            switch (ch)
            {
                case '[':
                    _state = StateCsi;
                    _csi.Length = 0;
                    return null;
                case ']':
                    _state = StateOsc;
                    _osc.Length = 0;
                    return null;
                case 'P':
                case 'X':
                case '^':
                case '_':
                    _state = StateSkipSt;
                    _osc.Length = 0;
                    return null;
                case '7':
                    _savedRow = _cursorRow;
                    _savedCol = _cursorCol;
                    _savedWrap = _wrapPending;
                    _state = StateGround;
                    return null;
                case '8':
                    _cursorRow = Clamp(_savedRow, 0, _rows - 1);
                    _cursorCol = Clamp(_savedCol, 0, _columns - 1);
                    _wrapPending = _savedWrap;
                    _state = StateGround;
                    return null;
                case 'M':
                    _wrapPending = false;
                    if (_cursorRow > 0)
                    {
                        _cursorRow--;
                    }
                    else
                    {
                        ScrollDownLocked();
                    }
                    _state = StateGround;
                    return null;
                case 'c':
                    ClearAllLocked();
                    _state = StateGround;
                    return null;
                case '(':
                case ')':
                case '#':
                case '%':
                    _escCharsetPending = true;
                    return null;
                case 'O':
                    // SS3: single-char follow-up swallowed in ground state
                    // marker; reuse charset path would need two states, so
                    // handle by staying in ESC for exactly one more char.
                    _escCharsetPending = true;
                    return null;
                case '\\':
                    // Lone ST outside a sequence: ignore.
                    _state = StateGround;
                    return null;
                default:
                    // Unknown ESC letter (incl. '=' '>' keypad modes):
                    // drop silently so payload never becomes transcript.
                    _state = StateGround;
                    return null;
            }
        }

        private string ConsumeCsiLocked(char ch)
        {
            if (ch >= '@' && ch <= '~')
            {
                bool drop = _csiOverflow;
                string seq = drop ? string.Empty : _csi.ToString();
                _csi.Length = 0;
                _csiOverflow = false;
                _state = StateGround;
                if (drop)
                {
                    return null;
                }
                return DispatchCsiLocked(seq, ch);
            }
            if (!_csiOverflow)
            {
                // Buffer is capped; an overlong CSI is swallowed (never
                // rendered) up to its final byte, then dropped.
                if (_csi.Length >= MaxPendingLength)
                {
                    _csiOverflow = true;
                }
                else
                {
                    _csi.Append(ch);
                }
            }
            return null;
        }

        private string ConsumeOscLocked(char ch)
        {
            if (ch == '\a')
            {
                return FinishOscLocked();
            }
            if (ch == '\x1b')
            {
                _state = StateOscEsc;
                return null;
            }
            if (!_oscOverflow)
            {
                if (_osc.Length >= MaxPendingLength)
                {
                    _oscOverflow = true;
                }
                else
                {
                    _osc.Append(ch);
                }
            }
            return null;
        }

        private string FinishOscLocked()
        {
            bool drop = _oscOverflow;
            string payload = drop ? string.Empty : _osc.ToString();
            _osc.Length = 0;
            _oscOverflow = false;
            _state = StateGround;
            if (drop)
            {
                return null;
            }
            return DispatchOscLocked(payload);
        }

        private string ConsumeOscEscLocked(char ch)
        {
            if (ch == '\\')
            {
                return FinishOscLocked();
            }
            // Not an ST: the ESC starts a fresh escape sequence; the partial
            // OSC payload is dropped rather than rendered.
            _osc.Length = 0;
            _oscOverflow = false;
            _state = StateEsc;
            _escCharsetPending = false;
            return ConsumeEscLocked(ch);
        }

        private string ConsumeSkipStLocked(char ch, bool sawEsc)
        {
            // APC/DCS/SOS/PM payload: swallowed without storing anything,
            // so no length accounting is needed; trivially bounded.
            if (sawEsc)
            {
                _state = ch == '\\' ? StateGround : StateSkipSt;
                return null;
            }
            if (ch == '\x1b')
            {
                _state = StateSkipStEsc;
                return null;
            }
            if (ch == '\a')
            {
                _state = StateGround;
            }
            return null;
        }

        private string DispatchCsiLocked(string seq, char final)
        {
            bool privateMark = seq.Length > 0 && (seq[0] == '?' || seq[0] == '>');
            string args = privateMark ? seq.Substring(1) : seq;
            switch (final)
            {
                case 'A':
                    MoveLocked(-Arg(args, 0, 1), 0);
                    return null;
                case 'B':
                    MoveLocked(Arg(args, 0, 1), 0);
                    return null;
                case 'C':
                    MoveLocked(0, Arg(args, 0, 1));
                    return null;
                case 'D':
                    MoveLocked(0, -Arg(args, 0, 1));
                    return null;
                case 'H':
                case 'f':
                    {
                        int row = Arg(args, 0, 1);
                        int col = Arg(args, 1, 1);
                        _wrapPending = false;
                        _cursorRow = Clamp(row - 1, 0, _rows - 1);
                        _cursorCol = Clamp(col - 1, 0, _columns - 1);
                        return null;
                    }
                case 'G':
                    _wrapPending = false;
                    _cursorCol = Clamp(Arg(args, 0, 1) - 1, 0, _columns - 1);
                    return null;
                case 'd':
                    _wrapPending = false;
                    _cursorRow = Clamp(Arg(args, 0, 1) - 1, 0, _rows - 1);
                    return null;
                case 'J':
                    _wrapPending = false;
                    EraseDisplayLocked(Arg(args, 0, 0));
                    return null;
                case 'K':
                    _wrapPending = false;
                    EraseLineLocked(Arg(args, 0, 0));
                    return null;
                case 's':
                    _savedRow = _cursorRow;
                    _savedCol = _cursorCol;
                    _savedWrap = _wrapPending;
                    return null;
                case 'u':
                    if (privateMark && seq[0] == '?')
                    {
                        // Kitty keyboard enhancement query: report none.
                        return "\x1b[?0u";
                    }
                    _cursorRow = Clamp(_savedRow, 0, _rows - 1);
                    _cursorCol = Clamp(_savedCol, 0, _columns - 1);
                    _wrapPending = _savedWrap;
                    return null;
                case 'n':
                    {
                        int which = Arg(args, 0, 0);
                        if (which == 5)
                        {
                            return "\x1b[0n";
                        }
                        if (which == 6)
                        {
                            return "\x1b[" + (_cursorRow + 1) + ";" + (_cursorCol + 1) + "R";
                        }
                        return null;
                    }
                case 'c':
                    if (privateMark && seq[0] == '>')
                    {
                        return "\x1b[>0;0;0c";
                    }
                    return "\x1b[?62c";
                case 'm':
                case 'h':
                case 'l':
                case 'r':
                    // SGR, modes, scroll-region setup: accepted, ignored.
                    return null;
                default:
                    return null;
            }
        }

        private string DispatchOscLocked(string payload)
        {
            if (payload == "10;?" || payload == "10; ?")
            {
                return "\x1b]10;rgb:0000/0000/0000\x1b\\";
            }
            if (payload == "11;?" || payload == "11; ?")
            {
                return "\x1b]11;rgb:0000/0000/0000\x1b\\";
            }
            // Titles, hyperlinks (OSC 8), clipboard (OSC 52) and every other
            // OSC payload: swallowed, never rendered, never executed.
            return null;
        }

        private void PutLocked(char ch)
        {
            if (_wrapPending)
            {
                _wrapPending = false;
                _cursorCol = 0;
                LineFeedLocked();
            }
            _grid[_cursorRow, _cursorCol] = ch;
            if (_cursorCol == _columns - 1)
            {
                _wrapPending = true;
            }
            else
            {
                _cursorCol++;
            }
        }

        private void LineFeedLocked()
        {
            if (_cursorRow < _rows - 1)
            {
                _cursorRow++;
            }
            else
            {
                for (int r = 0; r < _rows - 1; r++)
                {
                    for (int c = 0; c < _columns; c++)
                    {
                        _grid[r, c] = _grid[r + 1, c];
                    }
                }
                for (int c = 0; c < _columns; c++)
                {
                    _grid[_rows - 1, c] = ' ';
                }
            }
        }

        private void ScrollDownLocked()
        {
            for (int r = _rows - 1; r > 0; r--)
            {
                for (int c = 0; c < _columns; c++)
                {
                    _grid[r, c] = _grid[r - 1, c];
                }
            }
            for (int c = 0; c < _columns; c++)
            {
                _grid[0, c] = ' ';
            }
        }

        private void MoveLocked(int dRow, int dCol)
        {
            _wrapPending = false;
            _cursorRow = Clamp(_cursorRow + dRow, 0, _rows - 1);
            _cursorCol = Clamp(_cursorCol + dCol, 0, _columns - 1);
        }

        private void EraseDisplayLocked(int mode)
        {
            switch (mode)
            {
                case 1:
                    for (int r = 0; r < _cursorRow; r++)
                    {
                        ClearRow(r, 0, _columns);
                    }
                    ClearRow(_cursorRow, 0, _cursorCol + 1);
                    break;
                case 2:
                case 3:
                    ClearAllLocked();
                    break;
                default:
                    ClearRow(_cursorRow, _cursorCol, _columns);
                    for (int r = _cursorRow + 1; r < _rows; r++)
                    {
                        ClearRow(r, 0, _columns);
                    }
                    break;
            }
        }

        private void EraseLineLocked(int mode)
        {
            switch (mode)
            {
                case 1:
                    ClearRow(_cursorRow, 0, _cursorCol + 1);
                    break;
                case 2:
                    ClearRow(_cursorRow, 0, _columns);
                    break;
                default:
                    ClearRow(_cursorRow, _cursorCol, _columns);
                    break;
            }
        }

        private void ClearRow(int row, int from, int to)
        {
            for (int c = from; c < to; c++)
            {
                _grid[row, c] = ' ';
            }
        }

        private void ClearAllLocked()
        {
            for (int r = 0; r < _rows; r++)
            {
                ClearRow(r, 0, _columns);
            }
            _cursorRow = 0;
            _cursorCol = 0;
            _wrapPending = false;
        }

        // Parses the nth ';'-separated CSI argument; non-numeric or missing
        // segments yield defaultValue. Bounded: stops scanning past
        // MaxPendingLength characters (buffer already enforces this).
        private static int Arg(string args, int index, int defaultValue)
        {
            int current = 0;
            int start = 0;
            for (int i = 0; i <= args.Length; i++)
            {
                bool end = i == args.Length || args[i] == ';' || args[i] == ':';
                if (!end)
                {
                    continue;
                }
                if (current == index)
                {
                    if (i == start)
                    {
                        return defaultValue;
                    }
                    int value = 0;
                    for (int k = start; k < i; k++)
                    {
                        char d = args[k];
                        if (d < '0' || d > '9')
                        {
                            return defaultValue;
                        }
                        value = value * 10 + (d - '0');
                        if (value > 9999)
                        {
                            return 9999;
                        }
                    }
                    return value == 0 ? defaultValue : value;
                }
                current++;
                start = i + 1;
            }
            return defaultValue;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }
    }
}

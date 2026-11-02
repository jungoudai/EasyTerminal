using System.Text;

namespace EasyTerminal.Core.Terminal;

public enum MouseTrackingMode { None, X10, Normal, ButtonEvent, AnyEvent }
public enum MouseEncodingMode { Default, Utf8, Sgr }
public enum CursorShape { Block, Underline, Bar }

public sealed class TerminalLine
{
    public Cell[] Cells;
    public bool Wrapped;

    public TerminalLine(int cols) => Cells = new Cell[cols];

    public TerminalLine Clone()
    {
        var copy = new TerminalLine(Cells.Length) { Wrapped = Wrapped };
        Array.Copy(Cells, copy.Cells, Cells.Length);
        return copy;
    }
}

/// <summary>A ring of lines: the visible screen is the newest <c>rows</c> entries, older ones are scrollback.</summary>
internal sealed class LineRing
{
    private TerminalLine[] _lines;
    private int _start;

    public int Count { get; private set; }
    public int Capacity => _lines.Length;
    /// <summary>Lines permanently discarded from the top, used to keep absolute row numbers stable.</summary>
    public long Dropped { get; private set; }

    public LineRing(int capacity) => _lines = new TerminalLine[Math.Max(capacity, 1)];

    public TerminalLine this[int index]
    {
        get => _lines[(_start + index) % _lines.Length];
        set => _lines[(_start + index) % _lines.Length] = value;
    }

    /// <summary>Appends a line, returning the evicted line for reuse when the ring is full.</summary>
    public TerminalLine? Append(TerminalLine? line, int cols)
    {
        if (Count < _lines.Length)
        {
            _lines[(_start + Count) % _lines.Length] = line ?? new TerminalLine(cols);
            Count++;
            return null;
        }
        var evicted = _lines[_start];
        _start = (_start + 1) % _lines.Length;
        _lines[(_start + Count - 1) % _lines.Length] = line ?? evicted;
        Dropped++;
        return evicted;
    }

    public void RemoveLast()
    {
        if (Count > 0) Count--;
    }

    public void Reset(int capacity)
    {
        _lines = new TerminalLine[Math.Max(capacity, 1)];
        _start = 0;
        Count = 0;
    }

    public void AddDropped(long count) => Dropped += count;

    public LineRing Clone()
    {
        var copy = new LineRing(_lines.Length) { Dropped = Dropped };
        for (int i = 0; i < Count; i++) copy._lines[i] = this[i].Clone();
        copy.Count = Count;
        return copy;
    }
}

internal struct SavedCursor
{
    public int Col, Row, Fg, Bg;
    public CellFlags Flags;
    public bool Origin, AutoWrap, PendingWrap;
    public int ActiveCharset;
    public bool G0Special, G1Special;
}

/// <summary>
/// Terminal state: primary and alternate screens, modes and scrollback. Every read or write must hold
/// <see cref="SyncRoot"/>; a reader thread parses into it while the render thread snapshots frames.
/// </summary>
public sealed class TerminalBuffer : IVtHandler
{
    public readonly object SyncRoot = new();
    public event Action<byte[]>? Response;
    public event Action? TitleChanged;
    public event Action? Bell;

    private readonly VtParser _parser;
    private LineRing _primary;
    private LineRing _alternate;
    private LineRing _active;
    private int _scrollback;
    private int _scrollTop, _scrollBottom;
    private int _fg, _bg;
    private CellFlags _flags;
    private bool _pendingWrap;
    private bool[] _tabStops;
    private SavedCursor _savedPrimary, _savedAlternate;
    private int _lastPrinted;
    private bool _g0Special, _g1Special;
    private int _activeCharset;
    private readonly int[] _paletteOverride = new int[256];
    private bool _lastWasJoiner;

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorCol { get; private set; }
    public int CursorRow { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public CursorShape CursorShape { get; private set; } = CursorShape.Block;
    public bool CursorBlink { get; private set; } = true;
    public bool AltScreenActive => _active == _alternate;
    public bool AppCursorKeys { get; private set; }
    public bool AppKeypad { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool OriginMode { get; private set; }
    public bool InsertMode { get; private set; }
    public bool ReverseVideo { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool FocusReporting { get; private set; }
    public bool SynchronizedOutput { get; private set; }
    public MouseTrackingMode MouseTracking { get; private set; }
    public MouseEncodingMode MouseEncoding { get; private set; }
    public string Title { get; private set; } = "";
    public string? WorkingDirectory { get; private set; }

    /// <summary>Lines scrolled back from the bottom of the live screen; 0 follows output.</summary>
    public int ScrollOffset { get; private set; }
    /// <summary>Bumped on every mutation so the renderer can skip identical frames.</summary>
    public long Version { get; private set; }

    public int ScrollbackLines => _active.Count - Rows;

    public TerminalBuffer(int cols, int rows, int scrollback = 10000)
    {
        Cols = Math.Max(cols, 1);
        Rows = Math.Max(rows, 1);
        _scrollback = Math.Max(scrollback, 0);
        _parser = new VtParser(this);
        _primary = new LineRing(_scrollback + Rows);
        _alternate = new LineRing(Rows);
        Fill(_primary, Rows);
        Fill(_alternate, Rows);
        _active = _primary;
        _scrollBottom = Rows - 1;
        _tabStops = NewTabStops(Cols);
    }

    private void Fill(LineRing ring, int count)
    {
        for (int i = 0; i < count; i++) ring.Append(new TerminalLine(Cols), Cols);
    }

    private static bool[] NewTabStops(int cols)
    {
        var stops = new bool[cols];
        for (int i = 8; i < cols; i += 8) stops[i] = true;
        return stops;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (SyncRoot)
        {
            _parser.Write(data);
            Version++;
        }
    }

    public void Write(string text) => Write(Encoding.UTF8.GetBytes(text));

    // ---- Reading ----------------------------------------------------------------------------

    private TerminalLine ScreenLine(int row) => _active[_active.Count - Rows + row];

    /// <summary>Line shown at a viewport row, taking the scroll offset into account.</summary>
    public TerminalLine ViewportLine(int row) => _active[_active.Count - Rows - ScrollOffset + row];

    public long AbsoluteViewportRow(int row) => _active.Dropped + _active.Count - Rows - ScrollOffset + row;

    public int ResolvePalette(int index) => _paletteOverride[index & 0xFF];

    public void ScrollViewport(int delta)
    {
        lock (SyncRoot)
        {
            ScrollOffset = Math.Clamp(ScrollOffset + delta, 0, Math.Max(0, _active.Count - Rows));
            Version++;
        }
    }

    public void ScrollToBottom()
    {
        lock (SyncRoot)
        {
            if (ScrollOffset == 0) return;
            ScrollOffset = 0;
            Version++;
        }
    }

    /// <summary>Text between two absolute positions; soft-wrapped rows are joined without a line break.</summary>
    public string GetText(long startRow, int startCol, long endRow, int endCol)
    {
        lock (SyncRoot)
        {
            if (startRow > endRow || (startRow == endRow && startCol > endCol))
            {
                (startRow, endRow) = (endRow, startRow);
                (startCol, endCol) = (endCol, startCol);
            }
            var sb = new StringBuilder();
            long first = _active.Dropped;
            long last = _active.Dropped + _active.Count - 1;
            for (long r = Math.Max(startRow, first); r <= Math.Min(endRow, last); r++)
            {
                var line = _active[(int)(r - first)];
                int from = r == startRow ? startCol : 0;
                int to = r == endRow ? Math.Min(endCol, line.Cells.Length - 1) : line.Cells.Length - 1;
                int mark = sb.Length;
                for (int c = Math.Max(from, 0); c <= to; c++)
                {
                    if ((line.Cells[c].Flags & CellFlags.WideTail) != 0) continue;
                    ClusterTable.Append(sb, line.Cells[c].Codepoint);
                }
                if (!line.Wrapped || r == endRow)
                {
                    int trimmed = sb.Length;
                    while (trimmed > mark && sb[trimmed - 1] == ' ') trimmed--;
                    sb.Length = trimmed;
                }
                if (r < endRow && !line.Wrapped) sb.Append("\r\n");
            }
            return sb.ToString();
        }
    }

    /// <summary>Column span of the word under a viewport position, for double-click selection.</summary>
    public (int Start, int End) WordAt(int viewportRow, int col)
    {
        lock (SyncRoot)
        {
            var cells = ViewportLine(viewportRow).Cells;
            col = Math.Clamp(col, 0, cells.Length - 1);
            bool IsWord(int c)
            {
                int cp = cells[c].Codepoint;
                if (cp <= 0 || cp == ' ') return false;
                if (cp >= 0x80) return true;
                return char.IsLetterOrDigit((char)cp) || "_-./~:@%+=".IndexOf((char)cp) >= 0;
            }
            if (!IsWord(col)) return (col, col);
            int start = col, end = col;
            while (start > 0 && IsWord(start - 1)) start--;
            while (end < cells.Length - 1 && IsWord(end + 1)) end++;
            return (start, end);
        }
    }

    /// <summary>Replaces this buffer's state with a deep copy of another; used to display Mosh framebuffers.</summary>
    public void CopyFrom(TerminalBuffer source)
    {
        lock (SyncRoot)
        {
            Cols = source.Cols;
            Rows = source.Rows;
            _scrollback = source._scrollback;
            _primary = source._primary.Clone();
            _alternate = source._alternate.Clone();
            _active = source._active == source._alternate ? _alternate : _primary;
            _scrollTop = source._scrollTop;
            _scrollBottom = source._scrollBottom;
            _fg = source._fg;
            _bg = source._bg;
            _flags = source._flags;
            _pendingWrap = source._pendingWrap;
            _tabStops = (bool[])source._tabStops.Clone();
            _savedPrimary = source._savedPrimary;
            _savedAlternate = source._savedAlternate;
            _lastPrinted = source._lastPrinted;
            _g0Special = source._g0Special;
            _g1Special = source._g1Special;
            _activeCharset = source._activeCharset;
            Array.Copy(source._paletteOverride, _paletteOverride, 256);
            CursorCol = source.CursorCol;
            CursorRow = source.CursorRow;
            CursorVisible = source.CursorVisible;
            CursorShape = source.CursorShape;
            CursorBlink = source.CursorBlink;
            AppCursorKeys = source.AppCursorKeys;
            AppKeypad = source.AppKeypad;
            AutoWrap = source.AutoWrap;
            OriginMode = source.OriginMode;
            InsertMode = source.InsertMode;
            ReverseVideo = source.ReverseVideo;
            BracketedPaste = source.BracketedPaste;
            FocusReporting = source.FocusReporting;
            MouseTracking = source.MouseTracking;
            MouseEncoding = source.MouseEncoding;
            if (Title != source.Title)
            {
                Title = source.Title;
                TitleChanged?.Invoke();
            }
            ScrollOffset = 0;
            Version++;
        }
    }

    public TerminalBuffer Clone()
    {
        var copy = new TerminalBuffer(Cols, Rows, _scrollback);
        copy.CopyFrom(this);
        return copy;
    }

    // ---- Resize -----------------------------------------------------------------------------

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(cols, 1);
        rows = Math.Max(rows, 1);
        lock (SyncRoot)
        {
            if (cols == Cols && rows == Rows) return;
            bool wasAlternate = AltScreenActive;
            int primaryCol = wasAlternate ? _savedPrimary.Col : CursorCol;
            int primaryRow = wasAlternate ? _savedPrimary.Row : CursorRow;
            ReflowPrimary(cols, rows, ref primaryCol, ref primaryRow);
            ResizeAlternate(cols, rows);

            Cols = cols;
            Rows = rows;
            _active = wasAlternate ? _alternate : _primary;
            if (wasAlternate)
            {
                _savedPrimary.Col = primaryCol;
                _savedPrimary.Row = primaryRow;
                CursorCol = Math.Min(CursorCol, cols - 1);
                CursorRow = Math.Min(CursorRow, rows - 1);
            }
            else
            {
                CursorCol = primaryCol;
                CursorRow = primaryRow;
            }
            _scrollTop = 0;
            _scrollBottom = rows - 1;
            _tabStops = NewTabStops(cols);
            _pendingWrap = false;
            ScrollOffset = 0;
            Version++;
        }
    }

    private void ResizeAlternate(int cols, int rows)
    {
        var old = _alternate;
        var fresh = new LineRing(rows);
        for (int r = 0; r < rows; r++)
        {
            var line = new TerminalLine(cols);
            int source = old.Count - Rows + r;
            if (r < Rows && source >= 0 && source < old.Count)
                Array.Copy(old[source].Cells, line.Cells, Math.Min(cols, old[source].Cells.Length));
            fresh.Append(line, cols);
        }
        _alternate = fresh;
    }

    private void ReflowPrimary(int cols, int rows, ref int cursorCol, ref int cursorRow)
    {
        var old = _primary;
        int cursorAbs = old.Count - Rows + cursorRow;
        var result = new List<TerminalLine>(old.Count + rows);
        int newCursorRow = -1, newCursorCol = 0;
        var logical = new List<Cell>();

        int index = 0;
        while (index < old.Count)
        {
            logical.Clear();
            int cursorOffset = -1;
            while (true)
            {
                var line = old[index];
                if (index == cursorAbs) cursorOffset = logical.Count + cursorCol;
                int length = line.Cells.Length;
                if (!line.Wrapped)
                {
                    while (length > 0 && line.Cells[length - 1].IsBlank) length--;
                }
                for (int c = 0; c < length; c++) logical.Add(line.Cells[c]);
                bool wrapped = line.Wrapped;
                index++;
                if (!wrapped || index >= old.Count) break;
            }

            int position = 0;
            var current = new TerminalLine(cols);
            int col = 0;
            while (position < logical.Count)
            {
                var cell = logical[position];
                bool isWide = position + 1 < logical.Count && (logical[position + 1].Flags & CellFlags.WideTail) != 0;
                if (col + (isWide ? 2 : 1) > cols)
                {
                    current.Wrapped = true;
                    result.Add(current);
                    current = new TerminalLine(cols);
                    col = 0;
                }
                // The cursor may sit on the tail half of a wide character.
                if (cursorOffset == position || (isWide && cursorOffset == position + 1))
                {
                    newCursorRow = result.Count;
                    newCursorCol = col;
                }
                current.Cells[col++] = cell;
                position++;
                if (isWide)
                {
                    current.Cells[col++] = logical[position++];
                }
            }
            if (cursorOffset >= logical.Count)
            {
                // The cursor sits past the text: keep its column relative to the end of the line.
                int extra = cursorOffset - logical.Count;
                int targetCol = col + extra;
                newCursorRow = result.Count;
                newCursorCol = Math.Min(targetCol, cols - 1);
            }
            result.Add(current);
        }

        // Trim empty rows below the cursor so a shrink does not push content into scrollback.
        while (result.Count > Math.Max(newCursorRow + 1, 1) && IsEmpty(result[^1])) result.RemoveAt(result.Count - 1);
        while (result.Count < rows) result.Add(new TerminalLine(cols));

        var fresh = new LineRing(_scrollback + rows);
        fresh.AddDropped(old.Dropped + Math.Max(0, result.Count - fresh.Capacity));
        for (int i = Math.Max(0, result.Count - fresh.Capacity); i < result.Count; i++) fresh.Append(result[i], cols);
        _primary = fresh;

        int top = result.Count - rows;
        cursorRow = Math.Clamp(newCursorRow - top, 0, rows - 1);
        cursorCol = Math.Clamp(newCursorCol, 0, cols - 1);
    }

    private static bool IsEmpty(TerminalLine line)
    {
        if (line.Wrapped) return false;
        foreach (ref readonly var cell in line.Cells.AsSpan())
            if (!cell.IsBlank) return false;
        return true;
    }

    // ---- Printing ---------------------------------------------------------------------------

    public void Print(ReadOnlySpan<int> codepoints)
    {
        for (int index = 0; index < codepoints.Length; index++)
        {
            int cp = codepoints[index];
            if (cp < 0x80)
            {
                if (!InsertMode && !_pendingWrap && !(_activeCharset == 0 ? _g0Special : _g1Special))
                {
                    index += PrintAsciiRun(codepoints[index..]) - 1;
                    _lastWasJoiner = false;
                    continue;
                }
                if ((_activeCharset == 0 ? _g0Special : _g1Special) && cp is >= 0x60 and <= 0x7E)
                    cp = DecSpecial[cp - 0x60];
                _lastWasJoiner = false;
                PutCell(cp, 1);
                continue;
            }

            int width = CharWidth.Of(cp);
            if (width == 0 || _lastWasJoiner)
            {
                bool wasJoiner = _lastWasJoiner;
                AttachToPrevious(cp);
                _lastWasJoiner = cp == 0x200D;
                if (width == 0 || wasJoiner) continue;
            }
            _lastWasJoiner = false;
            PutCell(cp, width);
        }
    }

    /// <summary>Writes consecutive ASCII on the current row in one pass, the overwhelmingly common case.</summary>
    private int PrintAsciiRun(ReadOnlySpan<int> codepoints)
    {
        var line = ScreenLine(CursorRow);
        var cells = line.Cells;
        int col = CursorCol;
        int available = Math.Min(Cols - col, codepoints.Length);
        int count = 0;
        var template = new Cell { Foreground = _fg, Background = _bg, Flags = _flags };
        if ((cells[col].Flags & CellFlags.WideTail) != 0 && col > 0) cells[col - 1] = default;
        while (count < available)
        {
            int cp = codepoints[count];
            if (cp >= 0x80) break;
            template.Codepoint = cp;
            cells[col + count] = template;
            count++;
        }
        int end = col + count;
        if (end < Cols && (cells[end].Flags & CellFlags.WideTail) != 0) cells[end] = default;
        _lastPrinted = codepoints[count - 1];
        if (end >= Cols)
        {
            CursorCol = Cols - 1;
            _pendingWrap = true;
        }
        else
        {
            CursorCol = end;
        }
        return count;
    }

    private static readonly int[] DecSpecial =
    {
        0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514, 0x253C,
        0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260, 0x00A3, 0x00B7,
    };

    private void PutCell(int cp, int width)
    {
        if (_pendingWrap)
        {
            _pendingWrap = false;
            if (AutoWrap)
            {
                ScreenLine(CursorRow).Wrapped = true;
                CursorCol = 0;
                LineFeedInternal();
            }
        }

        if (width == 2 && CursorCol == Cols - 1)
        {
            if (!AutoWrap) return;
            ClearCell(ScreenLine(CursorRow), CursorCol);
            ScreenLine(CursorRow).Wrapped = true;
            CursorCol = 0;
            LineFeedInternal();
        }

        if (width > Cols) return;
        var line = ScreenLine(CursorRow);
        var cells = line.Cells;

        if (InsertMode)
        {
            int move = Cols - CursorCol - width;
            if (move > 0) Array.Copy(cells, CursorCol, cells, CursorCol + width, move);
            for (int i = 0; i < width; i++) cells[CursorCol + i] = default;
        }

        // Overwriting half of a wide character leaves its other half blank.
        if ((cells[CursorCol].Flags & CellFlags.WideTail) != 0 && CursorCol > 0) ClearCell(line, CursorCol - 1);
        int afterEnd = CursorCol + width;
        if (afterEnd < Cols && (cells[afterEnd].Flags & CellFlags.WideTail) != 0) ClearCell(line, afterEnd);

        ref var cell = ref cells[CursorCol];
        cell.Codepoint = cp;
        cell.Foreground = _fg;
        cell.Background = _bg;
        cell.Flags = _flags;
        if (width == 2)
        {
            ref var tail = ref cells[CursorCol + 1];
            tail.Codepoint = 0;
            tail.Foreground = _fg;
            tail.Background = _bg;
            tail.Flags = _flags | CellFlags.WideTail;
        }
        _lastPrinted = cp;

        int next = CursorCol + width;
        if (next >= Cols)
        {
            CursorCol = Cols - 1;
            _pendingWrap = true;
        }
        else
        {
            CursorCol = next;
        }
    }

    private static void ClearCell(TerminalLine line, int col) => line.Cells[col] = default;

    private void AttachToPrevious(int cp)
    {
        var line = ScreenLine(CursorRow);
        int col = _pendingWrap ? CursorCol : CursorCol - 1;
        if (col < 0) return;
        if ((line.Cells[col].Flags & CellFlags.WideTail) != 0 && col > 0) col--;
        ref var cell = ref line.Cells[col];
        if (cell.Codepoint <= 0) return;
        cell.Codepoint = ClusterTable.Intern(ClusterTable.ToText(cell.Codepoint) + char.ConvertFromUtf32(cp));
    }

    // ---- C0 controls ------------------------------------------------------------------------

    public void Execute(byte control)
    {
        switch (control)
        {
            case 0x07: Bell?.Invoke(); break;
            case 0x08:
                _pendingWrap = false;
                if (CursorCol > 0) CursorCol--;
                break;
            case 0x09: TabForward(1); break;
            case 0x0A or 0x0B or 0x0C: LineFeed(); break;
            case 0x0D:
                _pendingWrap = false;
                CursorCol = 0;
                break;
            case 0x0E: _activeCharset = 1; break;
            case 0x0F: _activeCharset = 0; break;
        }
    }

    private void TabForward(int count)
    {
        _pendingWrap = false;
        for (int n = 0; n < count && CursorCol < Cols - 1; n++)
        {
            CursorCol++;
            while (CursorCol < Cols - 1 && !_tabStops[CursorCol]) CursorCol++;
        }
    }

    private void TabBackward(int count)
    {
        _pendingWrap = false;
        for (int n = 0; n < count && CursorCol > 0; n++)
        {
            CursorCol--;
            while (CursorCol > 0 && !_tabStops[CursorCol]) CursorCol--;
        }
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        LineFeedInternal();
    }

    private void LineFeedInternal()
    {
        if (CursorRow == _scrollBottom) ScrollUp(1);
        else if (CursorRow < Rows - 1) CursorRow++;
    }

    private void ReverseIndex()
    {
        _pendingWrap = false;
        if (CursorRow == _scrollTop) ScrollDown(1);
        else if (CursorRow > 0) CursorRow--;
    }

    private void ScrollUp(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        bool fullScreen = _scrollTop == 0 && _scrollBottom == Rows - 1;
        if (fullScreen && _active == _primary)
        {
            for (int i = 0; i < count; i++)
            {
                // Once the ring is full the evicted line is reused, so a long-running stream stops allocating.
                _primary.Append(null, Cols);
                BlankLine(_primary[_primary.Count - 1]);
                if (ScrollOffset > 0) ScrollOffset = Math.Min(ScrollOffset + 1, _primary.Count - Rows);
            }
            return;
        }
        RotateRegion(_scrollTop, _scrollBottom, count);
    }

    private void ScrollDown(int count)
    {
        count = Math.Min(count, _scrollBottom - _scrollTop + 1);
        RotateRegion(_scrollTop, _scrollBottom, -count);
    }

    /// <summary>Rotates rows inside [top, bottom]; positive moves content up. Vacated rows become blank.</summary>
    private void RotateRegion(int top, int bottom, int amount)
    {
        int height = bottom - top + 1;
        int offset = _active.Count - Rows + top;
        int count = Math.Abs(amount);
        if (count == 0 || height <= 0) return;
        var moved = new TerminalLine[count];
        if (amount > 0)
        {
            for (int i = 0; i < count; i++) moved[i] = _active[offset + i];
            for (int i = 0; i < height - count; i++) _active[offset + i] = _active[offset + i + count];
            for (int i = 0; i < count; i++)
            {
                BlankLine(moved[i]);
                _active[offset + height - count + i] = moved[i];
            }
        }
        else
        {
            for (int i = 0; i < count; i++) moved[i] = _active[offset + height - count + i];
            for (int i = height - count - 1; i >= 0; i--) _active[offset + i + count] = _active[offset + i];
            for (int i = 0; i < count; i++)
            {
                BlankLine(moved[i]);
                _active[offset + i] = moved[i];
            }
        }
    }

    private void BlankLine(TerminalLine line)
    {
        line.Wrapped = false;
        if (line.Cells.Length != Cols) line.Cells = new Cell[Cols];
        if (_bg == 0)
        {
            Array.Clear(line.Cells);
            return;
        }
        var blank = new Cell { Background = _bg };
        line.Cells.AsSpan().Fill(blank);
    }

    // ---- Escape sequences -------------------------------------------------------------------

    public void EscDispatch(byte intermediate, byte final)
    {
        if (intermediate == (byte)'(') { _g0Special = final == (byte)'0'; return; }
        if (intermediate == (byte)')') { _g1Special = final == (byte)'0'; return; }
        if (intermediate == (byte)'#')
        {
            if (final == (byte)'8') AlignmentTest();
            return;
        }
        if (intermediate != 0) return;

        switch ((char)final)
        {
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': LineFeed(); break;
            case 'E':
                CursorCol = 0;
                LineFeed();
                break;
            case 'H': if (CursorCol < _tabStops.Length) _tabStops[CursorCol] = true; break;
            case 'M': ReverseIndex(); break;
            case 'c': FullReset(); break;
            case '=': AppKeypad = true; break;
            case '>': AppKeypad = false; break;
        }
    }

    private void AlignmentTest()
    {
        for (int r = 0; r < Rows; r++)
        {
            var cells = ScreenLine(r).Cells;
            for (int c = 0; c < Cols; c++) cells[c] = new Cell { Codepoint = 'E' };
        }
        CursorCol = CursorRow = 0;
        _pendingWrap = false;
    }

    private void FullReset()
    {
        _primary = new LineRing(_scrollback + Rows);
        _alternate = new LineRing(Rows);
        Fill(_primary, Rows);
        Fill(_alternate, Rows);
        _active = _primary;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _fg = _bg = 0;
        _flags = 0;
        _pendingWrap = false;
        _tabStops = NewTabStops(Cols);
        _g0Special = _g1Special = false;
        _activeCharset = 0;
        Array.Clear(_paletteOverride);
        CursorCol = CursorRow = 0;
        CursorVisible = true;
        CursorShape = CursorShape.Block;
        CursorBlink = true;
        AppCursorKeys = AppKeypad = OriginMode = InsertMode = ReverseVideo = BracketedPaste = FocusReporting = SynchronizedOutput = false;
        AutoWrap = true;
        MouseTracking = MouseTrackingMode.None;
        MouseEncoding = MouseEncodingMode.Default;
        ScrollOffset = 0;
    }

    private void SaveCursor()
    {
        var saved = new SavedCursor
        {
            Col = CursorCol, Row = CursorRow, Fg = _fg, Bg = _bg, Flags = _flags, Origin = OriginMode,
            AutoWrap = AutoWrap, PendingWrap = _pendingWrap, ActiveCharset = _activeCharset,
            G0Special = _g0Special, G1Special = _g1Special,
        };
        if (AltScreenActive) _savedAlternate = saved;
        else _savedPrimary = saved;
    }

    private void RestoreCursor()
    {
        var saved = AltScreenActive ? _savedAlternate : _savedPrimary;
        CursorCol = Math.Min(saved.Col, Cols - 1);
        CursorRow = Math.Min(saved.Row, Rows - 1);
        _fg = saved.Fg;
        _bg = saved.Bg;
        _flags = saved.Flags;
        OriginMode = saved.Origin;
        AutoWrap = saved.AutoWrap;
        _pendingWrap = saved.PendingWrap;
        _activeCharset = saved.ActiveCharset;
        _g0Special = saved.G0Special;
        _g1Special = saved.G1Special;
    }

    // ---- CSI --------------------------------------------------------------------------------

    public void CsiDispatch(ReadOnlySpan<int> parameters, ReadOnlySpan<bool> isSub, byte privateMarker, byte intermediate, byte final)
    {
        int Arg(ReadOnlySpan<int> p, int index, int fallback)
        {
            if (index >= p.Length || p[index] == 0) return fallback;
            return p[index];
        }

        if (privateMarker == (byte)'?')
        {
            switch ((char)final)
            {
                case 'h': foreach (int mode in parameters) SetDecMode(mode, true); break;
                case 'l': foreach (int mode in parameters) SetDecMode(mode, false); break;
                case 'n':
                    if (Arg(parameters, 0, 0) == 6) Respond($"\x1b[?{CursorRow + 1};{CursorCol + 1}R");
                    break;
                case 'p' when intermediate == (byte)'$':
                    int query = Arg(parameters, 0, 0);
                    Respond($"\x1b[?{query};{DecModeState(query)}$y");
                    break;
            }
            return;
        }

        if (privateMarker == (byte)'>')
        {
            if (final == (byte)'c') Respond("\x1b[>1;10;0c");
            else if (final == (byte)'q') Respond("\x1bP>|EasyTerminal 1.0\x1b\\");
            return;
        }

        if (privateMarker != 0) return;

        if (intermediate == (byte)' ')
        {
            if (final == (byte)'q') SetCursorStyle(Arg(parameters, 0, 0));
            return;
        }
        if (intermediate == (byte)'!')
        {
            if (final == (byte)'p') SoftReset();
            return;
        }
        if (intermediate != 0) return;

        switch ((char)final)
        {
            case 'A': MoveCursorRelative(-Arg(parameters, 0, 1), 0, true); break;
            case 'B' or 'e': MoveCursorRelative(Arg(parameters, 0, 1), 0, true); break;
            case 'C' or 'a': MoveCursorRelative(0, Arg(parameters, 0, 1), false); break;
            case 'D': MoveCursorRelative(0, -Arg(parameters, 0, 1), false); break;
            case 'E':
                CursorCol = 0;
                MoveCursorRelative(Arg(parameters, 0, 1), 0, true);
                break;
            case 'F':
                CursorCol = 0;
                MoveCursorRelative(-Arg(parameters, 0, 1), 0, true);
                break;
            case 'G' or '`':
                _pendingWrap = false;
                CursorCol = Math.Clamp(Arg(parameters, 0, 1) - 1, 0, Cols - 1);
                break;
            case 'H' or 'f': SetCursor(Arg(parameters, 0, 1) - 1, Arg(parameters, 1, 1) - 1); break;
            case 'd': SetCursor(Arg(parameters, 0, 1) - 1, CursorCol); break;
            case 'I': TabForward(Arg(parameters, 0, 1)); break;
            case 'Z': TabBackward(Arg(parameters, 0, 1)); break;
            case 'J': EraseDisplay(Arg(parameters, 0, 0)); break;
            case 'K': EraseLine(Arg(parameters, 0, 0)); break;
            case 'L': InsertLines(Arg(parameters, 0, 1)); break;
            case 'M': DeleteLines(Arg(parameters, 0, 1)); break;
            case '@': InsertChars(Arg(parameters, 0, 1)); break;
            case 'P': DeleteChars(Arg(parameters, 0, 1)); break;
            case 'X': EraseChars(Arg(parameters, 0, 1)); break;
            case 'S': ScrollUp(Arg(parameters, 0, 1)); break;
            case 'T': ScrollDown(Arg(parameters, 0, 1)); break;
            case 'b':
                if (_lastPrinted != 0)
                    for (int i = Math.Min(Arg(parameters, 0, 1), 65535); i > 0; i--) PutCell(_lastPrinted, CharWidth.Of(_lastPrinted) == 2 ? 2 : 1);
                break;
            case 'c':
                if (Arg(parameters, 0, 0) == 0) Respond("\x1b[?62;22;c");
                break;
            case 'g':
                int clear = Arg(parameters, 0, 0);
                if (clear == 0 && CursorCol < _tabStops.Length) _tabStops[CursorCol] = false;
                else if (clear == 3) Array.Clear(_tabStops);
                break;
            case 'h': if (Arg(parameters, 0, 0) == 4) InsertMode = true; break;
            case 'l': if (Arg(parameters, 0, 0) == 4) InsertMode = false; break;
            case 'm': SelectGraphicRendition(parameters, isSub); break;
            case 'n':
                int report = Arg(parameters, 0, 0);
                if (report == 5) Respond("\x1b[0n");
                else if (report == 6)
                {
                    int row = OriginMode ? CursorRow - _scrollTop : CursorRow;
                    Respond($"\x1b[{row + 1};{CursorCol + 1}R");
                }
                break;
            case 'r':
                int top = Arg(parameters, 0, 1) - 1;
                int bottom = Arg(parameters, 1, Rows) - 1;
                if (top < bottom && bottom < Rows)
                {
                    _scrollTop = top;
                    _scrollBottom = bottom;
                    SetCursor(0, 0);
                }
                break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't':
                if (Arg(parameters, 0, 0) == 18) Respond($"\x1b[8;{Rows};{Cols}t");
                break;
        }
    }

    private void SoftReset()
    {
        CursorVisible = true;
        OriginMode = InsertMode = AppCursorKeys = AppKeypad = false;
        AutoWrap = true;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
        _fg = _bg = 0;
        _flags = 0;
        _pendingWrap = false;
    }

    private void SetCursorStyle(int style)
    {
        CursorBlink = style is 0 or 1 or 3 or 5;
        CursorShape = style switch
        {
            3 or 4 => CursorShape.Underline,
            5 or 6 => CursorShape.Bar,
            _ => CursorShape.Block,
        };
    }

    private void MoveCursorRelative(int rows, int cols, bool vertical)
    {
        _pendingWrap = false;
        if (vertical)
        {
            int min = CursorRow >= _scrollTop ? _scrollTop : 0;
            int max = CursorRow <= _scrollBottom ? _scrollBottom : Rows - 1;
            CursorRow = Math.Clamp(CursorRow + rows, min, max);
        }
        else
        {
            CursorCol = Math.Clamp(CursorCol + cols, 0, Cols - 1);
        }
    }

    private void SetCursor(int row, int col)
    {
        _pendingWrap = false;
        if (OriginMode) row += _scrollTop;
        int maxRow = OriginMode ? _scrollBottom : Rows - 1;
        CursorRow = Math.Clamp(row, OriginMode ? _scrollTop : 0, maxRow);
        CursorCol = Math.Clamp(col, 0, Cols - 1);
    }

    private void EraseDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (int r = CursorRow + 1; r < Rows; r++) BlankLine(ScreenLine(r));
                break;
            case 1:
                EraseLine(1);
                for (int r = 0; r < CursorRow; r++) BlankLine(ScreenLine(r));
                break;
            case 2:
                for (int r = 0; r < Rows; r++) BlankLine(ScreenLine(r));
                break;
            case 3:
                if (_active == _primary)
                {
                    var keep = new TerminalLine[Rows];
                    for (int r = 0; r < Rows; r++) keep[r] = ScreenLine(r);
                    long dropped = _primary.Count - Rows;
                    _primary.Reset(_scrollback + Rows);
                    _primary.AddDropped(dropped);
                    foreach (var line in keep) _primary.Append(line, Cols);
                    ScrollOffset = 0;
                }
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var line = ScreenLine(CursorRow);
        int start = mode == 0 ? CursorCol : 0;
        int end = mode == 1 ? CursorCol + 1 : Cols;
        if (mode != 1) line.Wrapped = false;
        FillBlank(line, start, end);
    }

    private void FillBlank(TerminalLine line, int start, int end)
    {
        end = Math.Min(end, line.Cells.Length);
        if (start >= end) return;
        // Erasing half of a wide character blanks the other half too.
        if ((line.Cells[start].Flags & CellFlags.WideTail) != 0 && start > 0) line.Cells[start - 1] = new Cell { Background = _bg };
        if (end < line.Cells.Length && (line.Cells[end].Flags & CellFlags.WideTail) != 0) line.Cells[end] = new Cell { Background = _bg };
        line.Cells.AsSpan(start, end - start).Fill(new Cell { Background = _bg });
    }

    private void EraseChars(int count)
    {
        _pendingWrap = false;
        FillBlank(ScreenLine(CursorRow), CursorCol, CursorCol + count);
    }

    private void InsertChars(int count)
    {
        _pendingWrap = false;
        var line = ScreenLine(CursorRow);
        count = Math.Min(count, Cols - CursorCol);
        Array.Copy(line.Cells, CursorCol, line.Cells, CursorCol + count, Cols - CursorCol - count);
        FillBlank(line, CursorCol, CursorCol + count);
    }

    private void DeleteChars(int count)
    {
        _pendingWrap = false;
        var line = ScreenLine(CursorRow);
        count = Math.Min(count, Cols - CursorCol);
        Array.Copy(line.Cells, CursorCol + count, line.Cells, CursorCol, Cols - CursorCol - count);
        FillBlank(line, Cols - count, Cols);
    }

    private void InsertLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        _pendingWrap = false;
        CursorCol = 0;
        RotateRegion(CursorRow, _scrollBottom, -Math.Min(count, _scrollBottom - CursorRow + 1));
    }

    private void DeleteLines(int count)
    {
        if (CursorRow < _scrollTop || CursorRow > _scrollBottom) return;
        _pendingWrap = false;
        CursorCol = 0;
        RotateRegion(CursorRow, _scrollBottom, Math.Min(count, _scrollBottom - CursorRow + 1));
    }

    // ---- Modes ------------------------------------------------------------------------------

    private void SetDecMode(int mode, bool on)
    {
        switch (mode)
        {
            case 1: AppCursorKeys = on; break;
            case 5: ReverseVideo = on; break;
            case 6:
                OriginMode = on;
                SetCursor(0, 0);
                break;
            case 7:
                AutoWrap = on;
                if (!on) _pendingWrap = false;
                break;
            case 9: MouseTracking = on ? MouseTrackingMode.X10 : MouseTrackingMode.None; break;
            case 12: CursorBlink = on; break;
            case 25: CursorVisible = on; break;
            case 47: SwitchScreen(on, false, false); break;
            case 1000: MouseTracking = on ? MouseTrackingMode.Normal : MouseTrackingMode.None; break;
            case 1002: MouseTracking = on ? MouseTrackingMode.ButtonEvent : MouseTrackingMode.None; break;
            case 1003: MouseTracking = on ? MouseTrackingMode.AnyEvent : MouseTrackingMode.None; break;
            case 1004: FocusReporting = on; break;
            case 1005: MouseEncoding = on ? MouseEncodingMode.Utf8 : MouseEncodingMode.Default; break;
            case 1006: MouseEncoding = on ? MouseEncodingMode.Sgr : MouseEncodingMode.Default; break;
            case 1047: SwitchScreen(on, true, false); break;
            case 1048:
                if (on) SaveCursor();
                else RestoreCursor();
                break;
            case 1049: SwitchScreen(on, true, true); break;
            case 2004: BracketedPaste = on; break;
            case 2026: SynchronizedOutput = on; break;
        }
    }

    private int DecModeState(int mode)
    {
        bool? value = mode switch
        {
            1 => AppCursorKeys,
            5 => ReverseVideo,
            6 => OriginMode,
            7 => AutoWrap,
            25 => CursorVisible,
            1000 => MouseTracking == MouseTrackingMode.Normal,
            1002 => MouseTracking == MouseTrackingMode.ButtonEvent,
            1003 => MouseTracking == MouseTrackingMode.AnyEvent,
            1004 => FocusReporting,
            1006 => MouseEncoding == MouseEncodingMode.Sgr,
            1049 => AltScreenActive,
            2004 => BracketedPaste,
            2026 => SynchronizedOutput,
            _ => null,
        };
        return value switch { null => 0, true => 1, false => 2 };
    }

    private void SwitchScreen(bool alternate, bool clearAlternate, bool saveCursor)
    {
        if (alternate == AltScreenActive) return;
        if (alternate)
        {
            if (saveCursor) SaveCursor();
            _active = _alternate;
            if (clearAlternate)
                for (int r = 0; r < Rows; r++) BlankLine(ScreenLine(r));
            if (saveCursor)
            {
                CursorCol = 0;
                CursorRow = 0;
            }
            ScrollOffset = 0;
        }
        else
        {
            if (clearAlternate)
                for (int r = 0; r < Rows; r++) BlankLine(ScreenLine(r));
            _active = _primary;
            if (saveCursor) RestoreCursor();
        }
        _pendingWrap = false;
        _scrollTop = 0;
        _scrollBottom = Rows - 1;
    }

    // ---- SGR --------------------------------------------------------------------------------

    private void SelectGraphicRendition(ReadOnlySpan<int> p, ReadOnlySpan<bool> sub)
    {
        if (p.Length == 0)
        {
            _fg = _bg = 0;
            _flags = 0;
            return;
        }

        for (int i = 0; i < p.Length; i++)
        {
            int code = p[i];
            switch (code)
            {
                case 0:
                    _fg = _bg = 0;
                    _flags = 0;
                    break;
                case 1: _flags |= CellFlags.Bold; break;
                case 2: _flags |= CellFlags.Faint; break;
                case 3: _flags |= CellFlags.Italic; break;
                case 4:
                    if (i + 1 < p.Length && sub[i + 1])
                    {
                        if (p[i + 1] == 0) _flags &= ~CellFlags.Underline;
                        else _flags |= CellFlags.Underline;
                        i++;
                    }
                    else
                    {
                        _flags |= CellFlags.Underline;
                    }
                    break;
                case 5 or 6: _flags |= CellFlags.Blink; break;
                case 7: _flags |= CellFlags.Inverse; break;
                case 8: _flags |= CellFlags.Invisible; break;
                case 9: _flags |= CellFlags.Strike; break;
                case 21: _flags |= CellFlags.Underline; break;
                case 22: _flags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                case 23: _flags &= ~CellFlags.Italic; break;
                case 24: _flags &= ~CellFlags.Underline; break;
                case 25: _flags &= ~CellFlags.Blink; break;
                case 27: _flags &= ~CellFlags.Inverse; break;
                case 28: _flags &= ~CellFlags.Invisible; break;
                case 29: _flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: _fg = TermColor.Palette(code - 30); break;
                case 38: _fg = ParseExtendedColor(p, sub, ref i, _fg); break;
                case 39: _fg = 0; break;
                case >= 40 and <= 47: _bg = TermColor.Palette(code - 40); break;
                case 48: _bg = ParseExtendedColor(p, sub, ref i, _bg); break;
                case 49: _bg = 0; break;
                case 58: ParseExtendedColor(p, sub, ref i, 0); break;
                case >= 90 and <= 97: _fg = TermColor.Palette(code - 90 + 8); break;
                case >= 100 and <= 107: _bg = TermColor.Palette(code - 100 + 8); break;
            }
        }
    }

    private static int ParseExtendedColor(ReadOnlySpan<int> p, ReadOnlySpan<bool> sub, ref int i, int current)
    {
        if (i + 1 >= p.Length) return current;
        int kind = p[i + 1];
        if (sub[i + 1])
        {
            // Colon form: 38:5:n, 38:2:r:g:b or 38:2::r:g:b with an optional color-space slot.
            int end = i + 2;
            while (end < p.Length && sub[end]) end++;
            int count = end - (i + 2);
            int result = current;
            if (kind == 5 && count >= 1) result = TermColor.Palette(p[i + 2]);
            else if (kind == 2 && count >= 3)
            {
                int offset = count >= 4 ? i + 3 : i + 2;
                result = TermColor.Rgb(p[offset], p[offset + 1], p[offset + 2]);
            }
            i = end - 1;
            return result;
        }

        if (kind == 5 && i + 2 < p.Length)
        {
            int color = TermColor.Palette(p[i + 2]);
            i += 2;
            return color;
        }
        if (kind == 2 && i + 4 < p.Length)
        {
            int color = TermColor.Rgb(p[i + 2], p[i + 3], p[i + 4]);
            i += 4;
            return color;
        }
        i = p.Length;
        return current;
    }

    // ---- OSC --------------------------------------------------------------------------------

    public void OscDispatch(string payload)
    {
        int separator = payload.IndexOf(';');
        if (separator < 0) return;
        var command = payload.AsSpan(0, separator);
        var argument = payload[(separator + 1)..];

        if (command is "0" or "2")
        {
            Title = argument;
            TitleChanged?.Invoke();
        }
        else if (command is "7")
        {
            WorkingDirectory = argument;
        }
        else if (command is "4")
        {
            var parts = argument.Split(';');
            for (int i = 0; i + 1 < parts.Length; i += 2)
                if (int.TryParse(parts[i], out int index) && index is >= 0 and < 256 && TryParseColor(parts[i + 1], out int rgb))
                    _paletteOverride[index] = rgb;
        }
        else if (command is "104")
        {
            if (argument.Length == 0) Array.Clear(_paletteOverride);
            else
                foreach (var part in argument.Split(';'))
                    if (int.TryParse(part, out int index) && index is >= 0 and < 256)
                        _paletteOverride[index] = 0;
        }
        else if (command is "10" or "11" or "12" && argument == "?")
        {
            // Full-screen programs ask for colors to pick a light or dark theme.
            string value = command is "11" ? "rgb:1414/1414/1c1c" : "rgb:dddd/dddd/e4e4";
            Respond($"\x1b]{payload[..separator]};{value}\x1b\\");
        }
    }

    private static bool TryParseColor(string text, out int rgb)
    {
        rgb = 0;
        if (text.StartsWith("rgb:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text[4..].Split('/');
            if (parts.Length != 3) return false;
            int[] v = new int[3];
            for (int i = 0; i < 3; i++)
            {
                if (parts[i].Length is 0 or > 4 || !int.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out int n)) return false;
                v[i] = parts[i].Length switch { 1 => n * 17, 2 => n, 3 => n >> 4, _ => n >> 8 };
            }
            rgb = TermColor.Rgb(v[0], v[1], v[2]);
            return true;
        }
        if (text.StartsWith('#') && text.Length == 7 && int.TryParse(text[1..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
        {
            rgb = 0x02000000 | hex;
            return true;
        }
        return false;
    }

    private void Respond(string text) => Response?.Invoke(Encoding.ASCII.GetBytes(text));
}

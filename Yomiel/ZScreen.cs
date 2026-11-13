using System;
using System.Collections.Generic;
using System.IO;

namespace Yomiel;

/// <summary>One character cell of a <see cref="ZScreen"/>.</summary>
public readonly struct ZCell
{
    public ZCell(char c, ZColor foreground, ZColor background, ZTextStyle style)
    {
        Char = c;
        Foreground = foreground;
        Background = background;
        Style = style;
    }

    public char Char { get; }
    public ZColor Foreground { get; }
    public ZColor Background { get; }
    public ZTextStyle Style { get; }
}

/// <summary>
/// A character-grid screen for hosts that draw in fixed-width cells. Feed it <see cref="ZOutput"/>
/// items with <see cref="Apply"/>, or text one character at a time with <see cref="Write(char)"/>, and
/// draw <see cref="Render"/>. The upper window is a grid without wrapping. The lower window word
/// wraps, keeps scrollback and can show a line being typed (<see cref="Input"/>).
/// </summary>
public sealed class ZScreen
{
    private sealed class Line
    {
        public readonly List<ZCell> Cells = new();
        public ZColor Fill;
    }

    private readonly List<Line> lines = new();
    private Line current;
    private int clearMark;
    private ZCell[] upper;
    private int upperRow;
    private int upperColumn;
    private readonly ZCell[] frame;
    private readonly int scrollbackLimit;
    private ZColor lowerFill;

    public ZScreen(int columns, int rows, int scrollbackLines = 1000)
    {
        if (columns < 1 || rows < 1)
            throw new ArgumentOutOfRangeException(nameof(columns));
        Columns = columns;
        Rows = rows;
        scrollbackLimit = Math.Max(rows, scrollbackLines);
        frame = new ZCell[columns * rows];
        Reset();
    }

    public int Columns { get; }
    public int Rows { get; }
    public int UpperHeight { get; private set; }
    /// <summary>The window <see cref="Write(char)"/> writes to: 0 lower, 1 upper.</summary>
    public int Window { get; set; }
    public ZTextStyle Style { get; set; }
    public ZColor Foreground { get; set; }
    public ZColor Background { get; set; }
    public bool BufferMode { get; private set; }

    /// <summary>Line breaks in the lower window so far, including wrapping. Hosts use it to pause output.</summary>
    public long LowerLineCount { get; private set; }

    /// <summary>
    /// For revealing text a line at a time: lower-window lines numbered from this value on aren't
    /// shown, counting lines as <see cref="LowerLineCount"/> does. The line being written is number
    /// <see cref="LowerLineCount"/>, so <c>LowerLineCount + 1</c> shows everything. The default shows
    /// everything. Clearing the lower window shows the lines before the clear.
    /// </summary>
    public long ShownLines { get; set; } = long.MaxValue;

    /// <summary>
    /// For revealing text a character at a time: how many characters of line number
    /// <see cref="ShownLines"/> are shown. Lines before it are shown whole.
    /// </summary>
    public int ShownColumns { get; set; }

    /// <summary>
    /// The length of lower-window line <paramref name="number"/>, numbered as for
    /// <see cref="ShownLines"/>, or 0 if it's gone from the scrollback. With
    /// <paramref name="wholeWords"/>, the line being written counts only up to its last space,
    /// since a word still being written can wrap to the next line.
    /// </summary>
    public int LineLength(long number, bool wholeWords = false)
    {
        if (number == LowerLineCount)
            return wholeWords ? current.Cells.FindLastIndex(c => c.Char == ' ') + 1 : current.Cells.Count;
        long index = number - (LowerLineCount - lines.Count);
        return index >= 0 && index < lines.Count ? lines[(int)index].Cells.Count : 0;
    }

    /// <summary>Text being typed, shown after the lower window's last line, or null.</summary>
    public string Input { get; set; }

    /// <summary>Cursor position inside <see cref="Input"/>.</summary>
    public int InputCursor { get; set; }

    /// <summary>Where <see cref="Render"/> put the cursor, or -1 when it isn't shown.</summary>
    public int CursorColumn { get; private set; }
    public int CursorRow { get; private set; }

    /// <summary>How many lines the lower window can scroll back from the bottom.</summary>
    public int ScrollbackAvailable => Math.Max(0, DisplayLineCount() - (Rows - UpperHeight));

    public void Reset()
    {
        UpperHeight = 0;
        Window = 0;
        Style = ZTextStyle.Roman;
        Foreground = ZColor.Default;
        Background = ZColor.Default;
        BufferMode = true;
        lowerFill = ZColor.Default;
        lines.Clear();
        clearMark = 0;
        current = new Line { Fill = Background };
        upper = new ZCell[Columns * Rows];
        ClearUpper();
        Input = null;
    }

    /// <summary>Applies one item. Kinds that aren't about the screen are ignored.</summary>
    public void Apply(ZOutput item)
    {
        switch (item.Kind)
        {
            case ZOutputKind.Text:
            case ZOutputKind.InputEcho:
                Write(item.Text);
                break;
            case ZOutputKind.SplitWindow:
                UpperHeight = Math.Max(0, Math.Min(item.Value, Rows));
                if (upperRow >= UpperHeight)
                    upperRow = upperColumn = 0;
                break;
            case ZOutputKind.SetWindow:
                Window = item.Value == 1 ? 1 : 0;
                if (Window == 1)
                    upperRow = upperColumn = 0;
                break;
            case ZOutputKind.EraseWindow:
                Erase(item.Value);
                break;
            case ZOutputKind.EraseLine:
                if (Window == 1)
                    for (int c = upperColumn; c < Columns && upperRow < Rows; c++)
                        upper[upperRow * Columns + c] = Blank(Background);
                break;
            case ZOutputKind.SetCursor:
                if (Window == 1)
                {
                    upperRow = item.Row - 1;
                    upperColumn = item.Column - 1;
                }
                break;
            case ZOutputKind.SetStyle:
                Style = item.Style;
                break;
            case ZOutputKind.SetColor:
                Foreground = item.Foreground;
                Background = item.Background;
                break;
            case ZOutputKind.BufferMode:
                BufferMode = item.Value != 0;
                break;
            case ZOutputKind.Restarted:
                Reset();
                break;
        }
    }

    public void Write(string text)
    {
        foreach (char c in text ?? "")
            Write(c);
    }

    /// <summary>Writes one character in the current window, style and colours.</summary>
    public void Write(char c)
    {
        if (Window == 1)
        {
            if (c == '\n')
            {
                upperRow++;
                upperColumn = 0;
            }
            else
            {
                if (upperRow >= 0 && upperRow < Rows && upperColumn >= 0 && upperColumn < Columns)
                    upper[upperRow * Columns + upperColumn] = Cell(c);
                upperColumn++;
            }
            return;
        }

        if (c == '\n')
        {
            NewLine();
            return;
        }
        if (current.Cells.Count >= Columns)
        {
            if (c == ' ')
            {
                NewLine();
                return;
            }
            int space = BufferMode ? current.Cells.FindLastIndex(x => x.Char == ' ') : -1;
            if (space > 0)
            {
                List<ZCell> word = current.Cells.GetRange(space + 1, current.Cells.Count - space - 1);
                current.Cells.RemoveRange(space, current.Cells.Count - space);
                NewLine();
                current.Cells.AddRange(word);
            }
            else
            {
                NewLine();
            }
        }
        current.Cells.Add(Cell(c));
    }

    /// <summary>
    /// Removes text from the end of the lower window's last line, if it ends with it. Used to take
    /// back pre-loaded input that the story printed, so it can be edited as <see cref="Input"/>.
    /// </summary>
    public bool TakeBack(string text)
    {
        if (string.IsNullOrEmpty(text) || current.Cells.Count < text.Length)
            return false;
        int start = current.Cells.Count - text.Length;
        for (int i = 0; i < text.Length; i++)
            if (char.ToLowerInvariant(current.Cells[start + i].Char) != char.ToLowerInvariant(text[i]))
                return false;
        current.Cells.RemoveRange(start, text.Length);
        return true;
    }

    private ZCell Cell(char c) => new(c, Foreground, Background, Style);

    private static ZCell Blank(ZColor background) => new(' ', ZColor.Default, background, ZTextStyle.Roman);

    private void NewLine()
    {
        lines.Add(current);
        current = new Line { Fill = Background };
        LowerLineCount++;
        if (lines.Count > scrollbackLimit)
        {
            int drop = lines.Count - scrollbackLimit;
            lines.RemoveRange(0, drop);
            clearMark = Math.Max(0, clearMark - drop);
        }
    }

    private void Erase(int window)
    {
        if (window == -1)
        {
            UpperHeight = 0;
            Window = 0;
        }
        if (window == -1 || window == -2 || window == 1)
            ClearUpper();
        if (window == -1 || window == -2 || window == 0)
        {
            if (current.Cells.Count > 0)
            {
                lines.Add(current);
                LowerLineCount++;
            }
            current = new Line { Fill = Background };
            clearMark = lines.Count;
            if (ShownLines < LowerLineCount)
            {
                ShownLines = LowerLineCount;
                ShownColumns = 0;
            }
            lowerFill = Background;
        }
    }

    private void ClearUpper()
    {
        for (int i = 0; i < upper.Length; i++)
            upper[i] = Blank(Background);
        upperRow = upperColumn = 0;
    }

    // The last line and the input after it, wrapped at the screen width.
    private List<List<ZCell>> TailLines(out int cursorLine, out int cursorColumn)
    {
        var cells = new List<ZCell>(current.Cells);
        int cursorAt = cells.Count;
        if (Input != null)
        {
            cursorAt += Math.Max(0, Math.Min(InputCursor, Input.Length));
            foreach (char c in Input)
                cells.Add(Cell(c));
        }
        cursorLine = cursorAt / Columns;
        cursorColumn = cursorAt % Columns;
        int needed = Math.Max(Math.Max(1, (cells.Count + Columns - 1) / Columns), cursorLine + 1);
        var result = new List<List<ZCell>>(needed);
        for (int i = 0; i < needed; i++)
        {
            int start = i * Columns;
            result.Add(cells.GetRange(Math.Min(start, cells.Count), Math.Max(0, Math.Min(Columns, cells.Count - start))));
        }
        return result;
    }

    private int DisplayLineCount() => VisibleCommitted() + (PartialLine() != null ? 1 : 0) + (TailShown ? TailLines(out _, out _).Count : 0);

    /// <summary>The shown start of line <see cref="ShownLines"/>, or null when none of it is shown.</summary>
    private List<ZCell> PartialLine()
    {
        if (ShownColumns <= 0 || ShownLines > LowerLineCount)
            return null;
        List<ZCell> cells;
        if (ShownLines == LowerLineCount)
        {
            cells = current.Cells;
        }
        else
        {
            long index = ShownLines - (LowerLineCount - lines.Count);
            if (index < 0 || index >= lines.Count)
                return null;
            cells = lines[(int)index].Cells;
        }
        return cells.GetRange(0, Math.Min(ShownColumns, cells.Count));
    }

    // Compared before subtracting: ShownLines is long.MaxValue when everything is shown.
    private int VisibleCommitted() => ShownLines >= LowerLineCount
        ? lines.Count
        : (int)Math.Max(0, Math.Min(lines.Count, ShownLines - (LowerLineCount - lines.Count)));

    private bool TailShown => ShownLines > LowerLineCount;

    /// <summary>
    /// Composes the screen: the upper window over the top rows, the lower window below, scrolled
    /// back by <paramref name="scrollBack"/> lines. Returns Columns x Rows cells, row by row. The
    /// array is reused by the next call.
    /// </summary>
    public ZCell[] Render(int scrollBack = 0)
    {
        int lowerRows = Rows - UpperHeight;
        List<List<ZCell>> tail = TailLines(out int cursorLine, out int cursorColumn);
        int committed = VisibleCommitted();
        bool tailShown = TailShown;
        List<ZCell> partial = tailShown ? null : PartialLine();
        int total = committed + (partial != null ? 1 : 0) + (tailShown ? tail.Count : 0);
        scrollBack = Math.Max(0, Math.Min(scrollBack, total - lowerRows));
        int top = total - scrollBack - lowerRows;
        if (scrollBack == 0 && top < clearMark)
            top = clearMark;
        if (top < 0)
            top = 0;

        CursorColumn = CursorRow = -1;
        for (int r = 0; r < lowerRows; r++)
        {
            int index = top + r;
            List<ZCell> cells = null;
            ZColor fill = lowerFill;
            if (index < committed)
            {
                cells = lines[index].Cells;
                fill = lines[index].Fill;
            }
            else if (partial != null && index == committed)
            {
                cells = partial;
                fill = index < lines.Count ? lines[index].Fill : current.Fill;
            }
            else if (tailShown && index < total)
            {
                cells = tail[index - committed];
                fill = current.Fill;
            }
            int row = UpperHeight + r;
            for (int c = 0; c < Columns; c++)
                frame[row * Columns + c] = cells != null && c < cells.Count ? cells[c] : Blank(fill);
            if (tailShown && index == committed + cursorLine && scrollBack == 0)
            {
                CursorRow = row;
                CursorColumn = cursorColumn;
            }
        }
        Array.Copy(upper, frame, UpperHeight * Columns);
        if (Window == 1 && upperRow < UpperHeight && upperColumn < Columns)
        {
            CursorRow = upperRow;
            CursorColumn = upperColumn;
        }
        return frame;
    }

    // ---- Saving the contents, for suspended stories ----

    private const int FormatVersion = 1;

    /// <summary>The upper window and the last <paramref name="maxLines"/> lines of the lower window, with styles and colours.</summary>
    public byte[] SaveContents(int maxLines = 200)
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(FormatVersion);
        w.Write(Columns);
        w.Write(Rows);
        w.Write(UpperHeight);
        w.Write(Window);
        w.Write(upperRow);
        w.Write(upperColumn);
        w.Write((byte)Style);
        WriteColor(w, Foreground);
        WriteColor(w, Background);
        w.Write(BufferMode);
        WriteColor(w, lowerFill);
        foreach (ZCell cell in upper)
            WriteCell(w, cell);
        var kept = new List<Line>(lines.GetRange(Math.Max(0, lines.Count - maxLines), Math.Min(lines.Count, maxLines))) { current };
        w.Write(kept.Count);
        w.Write(Math.Max(0, clearMark - (lines.Count - (kept.Count - 1))));
        foreach (Line line in kept)
        {
            WriteColor(w, line.Fill);
            w.Write(line.Cells.Count);
            foreach (ZCell cell in line.Cells)
                WriteCell(w, cell);
        }
        return stream.ToArray();
    }

    /// <summary>Loads contents saved by <see cref="SaveContents"/>. Returns false, leaving the screen empty, if they don't fit this screen.</summary>
    public bool LoadContents(byte[] data)
    {
        Reset();
        try
        {
            var r = new BinaryReader(new MemoryStream(data));
            if (r.ReadInt32() != FormatVersion || r.ReadInt32() != Columns || r.ReadInt32() != Rows)
                return false;
            UpperHeight = r.ReadInt32();
            Window = r.ReadInt32();
            upperRow = r.ReadInt32();
            upperColumn = r.ReadInt32();
            Style = (ZTextStyle)r.ReadByte();
            Foreground = ReadColor(r);
            Background = ReadColor(r);
            BufferMode = r.ReadBoolean();
            lowerFill = ReadColor(r);
            for (int i = 0; i < upper.Length; i++)
                upper[i] = ReadCell(r);
            int count = r.ReadInt32();
            clearMark = r.ReadInt32();
            for (int n = 0; n < count; n++)
            {
                var line = new Line { Fill = ReadColor(r) };
                int cells = r.ReadInt32();
                for (int i = 0; i < cells; i++)
                    line.Cells.Add(ReadCell(r));
                if (n < count - 1)
                    lines.Add(line);
                else
                    current = line;
            }
            // Line numbers count the loaded lines, so ShownLines and LineLength line up with them.
            LowerLineCount = lines.Count;
            return true;
        }
        catch (Exception e) when (e is EndOfStreamException || e is IOException || e is ArgumentException)
        {
            Reset();
            return false;
        }
    }

    private static void WriteColor(BinaryWriter w, ZColor c) => w.Write((short)(c.IsDefault ? -1 : c.Rgb15));

    private static ZColor ReadColor(BinaryReader r)
    {
        short v = r.ReadInt16();
        return v < 0 ? ZColor.Default : ZColor.FromRgb15(v);
    }

    private static void WriteCell(BinaryWriter w, ZCell cell)
    {
        w.Write((ushort)cell.Char);
        WriteColor(w, cell.Foreground);
        WriteColor(w, cell.Background);
        w.Write((byte)cell.Style);
    }

    private static ZCell ReadCell(BinaryReader r) =>
        new((char)r.ReadUInt16(), ReadColor(r), ReadColor(r), (ZTextStyle)r.ReadByte());
}

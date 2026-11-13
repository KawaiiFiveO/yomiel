using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Yomiel.Tui;

/// <summary>
/// Draws <see cref="ZCell"/> grids with ANSI escape codes on the alternate screen, sending only
/// the cells that changed, and reads keys.
/// </summary>
internal sealed class Terminal
{
    private const string Esc = "\u001b[";

    // The standard colours 2-12 as SGR foreground codes: black, red, green, yellow, blue, magenta,
    // cyan, white, light grey, medium grey, dark grey. Backgrounds are 10 more.
    private static readonly int[] Ansi = { 30, 31, 32, 33, 34, 35, 36, 97, 37, 90, 90 };
    private static readonly int[] AnsiRgb = { 0x000000, 0xAA0000, 0x00AA00, 0xAAAA00, 0x0000AA, 0xAA00AA, 0x00AAAA, 0xFFFFFF, 0xAAAAAA, 0x555555, 0x555555 };

    private readonly bool trueColor;
    private readonly StringBuilder text = new();
    private Stream output;
    private ZCell[] shown;
    private int shownColumns, shownRows;
    private (int Columns, int Rows) shownTerminal;
    private (int Column, int Row, bool Visible) shownCursor = (-1, -1, false);
    private bool beep;
    private uint savedMode;
    private bool restoreMode;
    private string title;
    private string savedTitle;

    public Terminal(bool trueColor) => this.trueColor = trueColor;

    /// <summary>The terminal's size in characters.</summary>
    public static (int Columns, int Rows) Size
    {
        get
        {
            try
            {
                return (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
            }
            catch (IOException)
            {
                return (80, 25);
            }
        }
    }

    /// <summary>Switches to the alternate screen and takes Ctrl+C as a key.</summary>
    public void Enter()
    {
        EnableVirtualTerminal();
        Console.TreatControlCAsInput = true;
        output = Console.OpenStandardOutput();
        shown = null;
        title = null;
        Send(Esc + "?1049h" + Esc + "?25l" + Esc + "0m" + Esc + "2J");
        // Windows can read the title back; other terminals keep it on their title stack.
        if (OperatingSystem.IsWindows())
            savedTitle = TryGetTitle();
        else
            Send(Esc + "22;0t");
    }

    /// <summary>Restores the screen, the cursor and the window title.</summary>
    public void Leave()
    {
        if (output == null)
            return;
        Send(Esc + "0m" + Esc + "?25h" + Esc + "?1049l");
        if (!OperatingSystem.IsWindows())
            Send(Esc + "23;0t");
        else if (title != null && savedTitle != null)
            TrySetTitle(savedTitle);
        output = null;
        Console.TreatControlCAsInput = false;
        RestoreMode();
    }

    /// <summary>Sets the window title, if it changed.</summary>
    public void SetTitle(string value)
    {
        value = new string(value.Where(c => !char.IsControl(c)).ToArray());
        if (value == title)
            return;
        title = value;
        if (OperatingSystem.IsWindows())
            TrySetTitle(value);
        else
            Send("\u001b]0;" + value + "\u0007");
    }

    private static string TryGetTitle()
    {
        try
        {
            return OperatingSystem.IsWindows() ? Console.Title : null;
        }
        catch (Exception e) when (e is IOException || e is PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static void TrySetTitle(string value)
    {
        try
        {
            Console.Title = value;
        }
        catch (Exception e) when (e is IOException || e is PlatformNotSupportedException || e is ArgumentOutOfRangeException)
        {
        }
    }

    /// <summary>Rings the bell with the next frame.</summary>
    public void Beep() => beep = true;

    public static bool TryReadKey(out ConsoleKeyInfo key)
    {
        if (Console.KeyAvailable)
        {
            key = Console.ReadKey(intercept: true);
            return true;
        }
        key = default;
        return false;
    }

    /// <summary>
    /// Draws a grid of <paramref name="columns"/> × <paramref name="rows"/> cells from the top left
    /// corner, cut to the terminal's size.
    /// </summary>
    public void Draw(ZCell[] cells, int columns, int rows, int cursorColumn, int cursorRow, bool cursorVisible)
    {
        (int Columns, int Rows) terminal = Size;
        bool full = shown == null || shownColumns != columns || shownRows != rows || shownTerminal != terminal;
        text.Clear();
        if (full)
        {
            text.Append(Esc).Append("0m").Append(Esc).Append("2J");
            shown = new ZCell[columns * rows];
            shownColumns = columns;
            shownRows = rows;
            shownTerminal = terminal;
            shownCursor = (-1, -1, false);
        }

        string attributes = null;
        int atRow = -1, atColumn = -1;
        bool changed = false;
        for (int r = 0; r < Math.Min(rows, terminal.Rows); r++)
        {
            for (int c = 0; c < Math.Min(columns, terminal.Columns); c++)
            {
                int i = r * columns + c;
                ZCell cell = cells[i];
                if (!full && Same(cell, shown[i]))
                    continue;
                if (!changed)
                {
                    text.Append(Esc).Append("?25l");
                    changed = true;
                }
                if (atRow != r || atColumn != c)
                    text.Append(Esc).Append(r + 1).Append(';').Append(c + 1).Append('H');
                string sgr = Sgr(cell);
                if (sgr != attributes)
                {
                    text.Append(sgr);
                    attributes = sgr;
                }
                text.Append(Printable(cell.Char));
                atRow = r;
                atColumn = c + 1;
                shown[i] = cell;
            }
        }

        if (beep)
        {
            text.Append('\a');
            beep = false;
        }
        bool visible = cursorVisible && cursorRow >= 0 && cursorRow < terminal.Rows && cursorColumn >= 0 && cursorColumn < terminal.Columns;
        var cursor = (cursorColumn, cursorRow, visible);
        if (changed || cursor != shownCursor)
        {
            text.Append(Esc).Append("0m");
            if (visible)
                text.Append(Esc).Append(cursorRow + 1).Append(';').Append(cursorColumn + 1).Append('H').Append(Esc).Append("?25h");
            else
                text.Append(Esc).Append("?25l");
            shownCursor = cursor;
        }
        if (text.Length > 0)
            Send(text.ToString());
    }

    private static bool Same(ZCell a, ZCell b) =>
        a.Char == b.Char && a.Style == b.Style && a.Foreground == b.Foreground && a.Background == b.Background;

    private static char Printable(char c) => c < ' ' || c >= '\u007f' && c < ' ' ? ' ' : c;

    private string Sgr(ZCell cell)
    {
        var sgr = new StringBuilder(Esc).Append('0');
        if ((cell.Style & ZTextStyle.Bold) != 0)
            sgr.Append(";1");
        if ((cell.Style & ZTextStyle.Italic) != 0)
            sgr.Append(";3");
        if ((cell.Style & ZTextStyle.Reverse) != 0)
            sgr.Append(";7");
        AppendColor(sgr, cell.Foreground, background: false);
        AppendColor(sgr, cell.Background, background: true);
        return sgr.Append('m').ToString();
    }

    private void AppendColor(StringBuilder sgr, ZColor color, bool background)
    {
        if (color.IsDefault)
            return;
        if (trueColor)
        {
            int rgb = color.ToRgb24();
            sgr.Append(background ? ";48;2;" : ";38;2;").Append(rgb >> 16).Append(';').Append(rgb >> 8 & 255).Append(';').Append(rgb & 255);
            return;
        }
        int index = color.Index >= 2 ? color.Index - 2 : Nearest(color.ToRgb24());
        sgr.Append(';').Append(Ansi[index] + (background ? 10 : 0));
    }

    private static int Nearest(int rgb)
    {
        int best = 0;
        long bestDistance = long.MaxValue;
        for (int i = 0; i < AnsiRgb.Length; i++)
        {
            long dr = (rgb >> 16) - (AnsiRgb[i] >> 16), dg = (rgb >> 8 & 255) - (AnsiRgb[i] >> 8 & 255), db = (rgb & 255) - (AnsiRgb[i] & 255);
            long distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    private void Send(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        try
        {
            output.Write(bytes, 0, bytes.Length);
            output.Flush();
        }
        catch (IOException)
        {
            // The terminal is closing; the story is still suspended.
        }
    }

    // ---- Windows console ----

    private const int StdOutput = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>The classic Windows console reads escape codes only in this mode. Windows Terminal always does.</summary>
    private void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
            return;
        IntPtr handle = GetStdHandle(StdOutput);
        if (GetConsoleMode(handle, out savedMode))
            restoreMode = SetConsoleMode(handle, savedMode | EnableVirtualTerminalProcessing);
    }

    private void RestoreMode()
    {
        if (restoreMode)
            SetConsoleMode(GetStdHandle(StdOutput), savedMode);
        restoreMode = false;
    }
}

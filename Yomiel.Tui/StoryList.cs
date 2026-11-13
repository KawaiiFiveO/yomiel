using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Yomiel.Tui;

/// <summary>A story file, and its folder for saves and the suspended story.</summary>
internal sealed class StoryEntry
{
    private static readonly string[] Extensions = { ".z1", ".z2", ".z3", ".z4", ".z5", ".z6", ".z7", ".z8" };

    public string Path;
    public string Name;
    public int Version;
    public long Size;
    public string DataFolder;

    public bool Supported => ZMachine.IsSupportedVersion(Version);
    public bool CanContinue => Supported && new ZStoryFolder(DataFolder).HasSuspended;

    public static bool IsStoryFile(string path) => Extensions.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Reads the header. The data folder is named after the file and the story's release, serial
    /// number and checksum, so stories with the same file name keep apart.
    /// </summary>
    public static StoryEntry Read(string path, string dataRoot)
    {
        var header = new byte[64];
        int read = 0;
        try
        {
            using FileStream stream = File.OpenRead(path);
            while (read < header.Length)
            {
                int n = stream.Read(header, read, header.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { }

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        int release = header[2] << 8 | header[3];
        string serial = Encoding.ASCII.GetString(header, 0x12, 6);
        int checksum = header[0x1C] << 8 | header[0x1D];
        string id = $"{release}-{ZStoryFolder.SafeName(serial)}-{checksum:x4}";
        string folder = ZStoryFolder.SafeName(name.Length > 40 ? name.Substring(0, 40) : name) + "-" + id;
        return new StoryEntry
        {
            Path = path,
            Name = name,
            Version = read > 0 ? header[0] : 0,
            Size = read > 0 ? new FileInfo(path).Length : 0,
            DataFolder = System.IO.Path.Combine(dataRoot, folder),
        };
    }

    public static List<StoryEntry> Find(string folder, string dataRoot)
    {
        if (!Directory.Exists(folder))
            return new List<StoryEntry>();
        return Directory.GetFiles(folder)
            .Where(IsStoryFile)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => Read(p, dataRoot))
            .ToList();
    }
}

/// <summary>The story picker: a list of the folder's stories.</summary>
internal sealed class Picker
{
    public enum Choice { None, Play, StartOver, Quit }

    private static readonly ZColor Grey = ZColor.FromIndex(11);
    private static readonly ZColor Warning = ZColor.FromIndex(3);

    private readonly string folder;
    private readonly string dataRoot;
    private List<StoryEntry> stories = new();
    private int selected;

    public Picker(string folder, string dataRoot)
    {
        this.folder = folder;
        this.dataRoot = dataRoot;
        Refresh();
    }

    public string Message { get; set; }

    public StoryEntry Selected => stories.Count > 0 ? stories[selected] : null;

    public void Refresh()
    {
        string current = Selected?.Path;
        stories = StoryEntry.Find(folder, dataRoot);
        selected = Math.Max(0, stories.FindIndex(s => s.Path == current));
    }

    public Choice Key(ConsoleKeyInfo key)
    {
        int page = Math.Max(1, Terminal.Size.Rows - 6);
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                selected = Math.Max(0, selected - 1);
                break;
            case ConsoleKey.DownArrow:
                selected = Math.Max(0, Math.Min(stories.Count - 1, selected + 1));
                break;
            case ConsoleKey.PageUp:
                selected = Math.Max(0, selected - page);
                break;
            case ConsoleKey.PageDown:
                selected = Math.Max(0, Math.Min(stories.Count - 1, selected + page));
                break;
            case ConsoleKey.Home:
                selected = 0;
                break;
            case ConsoleKey.End:
                selected = Math.Max(0, stories.Count - 1);
                break;
            case ConsoleKey.Enter when stories.Count > 0:
                return Choice.Play;
            case ConsoleKey.F2 when stories.Count > 0:
                return Choice.StartOver;
            case ConsoleKey.F5:
                Refresh();
                Message = null;
                break;
            case ConsoleKey.Escape:
            case ConsoleKey.Q when key.Modifiers == 0 || (key.Modifiers & ConsoleModifiers.Control) != 0:
                return Choice.Quit;
            case ConsoleKey.C when (key.Modifiers & ConsoleModifiers.Control) != 0:
                return Choice.Quit;
        }
        return Choice.None;
    }

    public void Draw(Terminal terminal)
    {
        (int columns, int rows) = Terminal.Size;
        var cells = new ZCell[columns * rows];
        var blank = new ZCell(' ', ZColor.Default, ZColor.Default, ZTextStyle.Roman);
        for (int i = 0; i < cells.Length; i++)
            cells[i] = blank;

        void Put(int row, int column, string s, ZTextStyle style = ZTextStyle.Roman, ZColor? fore = null, int width = -1)
        {
            if (row < 0 || row >= rows)
                return;
            if (width >= 0)
                s = s.Length > width ? s.Substring(0, width) : s.PadRight(width);
            for (int i = 0; i < s.Length && column + i < columns; i++)
                if (column + i >= 0)
                    cells[row * columns + column + i] = new ZCell(s[i], fore ?? ZColor.Default, ZColor.Default, style);
        }

        Put(0, 0, " YOMIEL   " + Path.GetFullPath(folder), ZTextStyle.Reverse, width: columns);
        int top = 2;
        if (stories.Count == 0)
        {
            Put(top, 2, "No story files (.z5, .z7, .z8) in this folder.");
            Put(top + 2, 2, "Run yomiel-tui with a story file or a folder, or press F5 to look again.");
        }
        else
        {
            int visible = Math.Max(1, rows - 6);
            int first = Math.Max(0, Math.Min(selected - visible / 2, stories.Count - visible));
            int nameWidth = Math.Max(8, columns - 34);
            for (int i = first; i < Math.Min(stories.Count, first + visible); i++)
            {
                StoryEntry s = stories[i];
                string state = !s.Supported ? "not supported" : s.CanContinue ? "continues" : "";
                string name = s.Name.Length > nameWidth ? s.Name.Substring(0, nameWidth) : s.Name;
                string line = $" {name.PadRight(nameWidth)} v{s.Version}  {s.Size / 1024,5} KB  {state,-13}";
                bool chosen = i == selected;
                Put(top + i - first, 1, line, chosen ? ZTextStyle.Reverse : ZTextStyle.Roman, s.Supported || chosen ? null : Grey, columns - 2);
            }
        }

        if (Message != null)
            Put(rows - 3, 1, Message, fore: Warning, width: columns - 2);
        string help = stories.Count > 0 ? " Enter play or continue   F2 start over   F5 refresh   Esc quit" : " F5 refresh   Esc quit";
        Put(rows - 1, 0, help, ZTextStyle.Reverse, width: columns);
        terminal.Draw(cells, columns, rows, 0, 0, cursorVisible: false);
    }
}

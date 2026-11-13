using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Yomiel;

// A plain console front end, for trying stories and testing the core. Lower-window text goes to
// stdout as it is printed. The upper window is kept as a grid and printed above each prompt when
// it has changed. Saves are read and written as files named on stdin.

const string Usage = """
    Usage: yomiel-cli <story> [options]

      --width N     screen width in characters (default 80)
      --height N    screen height in lines (default 25)
      --seed N      random number seed
      --no-upper    don't print the upper window
      --stats       print the instruction count and speed at exit
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine(Usage);
    return 2;
}

var options = new ZMachineOptions();
bool showUpper = true, stats = false;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--width": options.ScreenColumns = int.Parse(args[++i]); break;
        case "--height": options.ScreenRows = int.Parse(args[++i]); break;
        case "--seed": options.RandomSeed = int.Parse(args[++i]); break;
        case "--no-upper": showUpper = false; break;
        case "--stats": stats = true; break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}\n{Usage}");
            return 2;
    }
}

Console.OutputEncoding = Encoding.UTF8;
ZMachine machine;
try
{
    machine = new ZMachine(File.ReadAllBytes(args[0]), options);
}
catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"yomiel: {e.Message}");
    return 1;
}

var upper = new UpperWindow(options.ScreenColumns);
var clock = Stopwatch.StartNew();
TextWriter transcript = null, record = null;

while (true)
{
    ZStatus status = machine.Run();
    while (machine.TryGetOutput(out ZOutput item))
    {
        switch (item.Kind)
        {
            case ZOutputKind.Text when item.Window == 0:
                Console.Write(item.Text);
                break;
            case ZOutputKind.Text:
                upper.Write(item.Text);
                break;
            case ZOutputKind.SplitWindow:
                upper.Resize(item.Value);
                break;
            case ZOutputKind.SetCursor when item.Window == 1:
                upper.MoveTo(item.Row, item.Column);
                break;
            case ZOutputKind.SetWindow when item.Value == 1:
                upper.MoveTo(1, 1);
                break;
            case ZOutputKind.EraseWindow when item.Value != 0:
                upper.Clear();
                if (item.Value == -1)
                    upper.Resize(0);
                break;
            case ZOutputKind.Transcript:
                transcript?.Write(item.Text);
                break;
            case ZOutputKind.Warning:
                Console.Error.WriteLine($"[warning] {item.Text}");
                break;
            case ZOutputKind.CommandRecord:
                record?.WriteLine(item.Text);
                break;
            case ZOutputKind.OutputStream:
                string name = Path.ChangeExtension(args[0], item.Value is 2 or -2 ? ".transcript.txt" : ".commands.txt");
                if (item.Value == 2) transcript = new StreamWriter(name, true);
                else if (item.Value == -2) { transcript?.Dispose(); transcript = null; }
                else if (item.Value == 4) record = new StreamWriter(name, true);
                else if (item.Value == -4) { record?.Dispose(); record = null; }
                break;
        }
    }

    switch (status)
    {
        case ZStatus.Running:
            continue;
        case ZStatus.WaitingForLine:
        {
            if (showUpper)
                upper.PrintIfChanged();
            string line = Console.ReadLine();
            if (line == null)
                return Finish(0);
            machine.SubmitLine(machine.LineRequest.InitialText + line);
            break;
        }
        case ZStatus.WaitingForKey:
        {
            if (showUpper)
                upper.PrintIfChanged();
            string line = Console.ReadLine();
            if (line == null)
                return Finish(0);
            if (line.Length == 0)
                machine.SubmitKey(ZKey.Enter);
            else
                machine.SubmitKey(line[0]);
            break;
        }
        case ZStatus.WaitingForSave:
        {
            Console.Write($"Save to file [{machine.SaveRequest.SuggestedName ?? "story.qzl"}]: ");
            string file = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(file))
                file = machine.SaveRequest.SuggestedName ?? "story.qzl";
            bool ok = true;
            try { File.WriteAllBytes(file, machine.SaveRequest.Data); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine(e.Message); ok = false; }
            machine.CompleteSave(ok);
            break;
        }
        case ZStatus.WaitingForRestore:
        {
            Console.Write($"Restore from file [{machine.RestoreRequest.SuggestedName ?? "story.qzl"}]: ");
            string file = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(file))
                file = machine.RestoreRequest.SuggestedName ?? "story.qzl";
            byte[] data = null;
            try { data = File.ReadAllBytes(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine(e.Message); }
            if (data != null && !machine.CompleteRestore(data))
                Console.WriteLine(machine.RestoreError);
            else if (data == null)
                machine.CompleteRestore(null);
            break;
        }
        case ZStatus.Halted:
            return Finish(0);
        case ZStatus.Error:
            Console.Error.WriteLine($"\nyomiel: {machine.ErrorMessage}");
            return Finish(1);
    }
}

int Finish(int code)
{
    transcript?.Dispose();
    record?.Dispose();
    if (stats)
    {
        double seconds = clock.Elapsed.TotalSeconds;
        Console.Error.WriteLine($"\n{machine.InstructionCount:N0} instructions in {seconds:F2} s");
    }
    return code;
}

internal sealed class UpperWindow
{
    private readonly int width;
    private char[][] rows = Array.Empty<char[]>();
    private int row, column;
    private string shown = "";

    public UpperWindow(int width) => this.width = width;

    public void Resize(int height)
    {
        var old = rows;
        rows = new char[height][];
        for (int i = 0; i < height; i++)
            rows[i] = i < old.Length ? old[i] : Blank();
    }

    public void Clear()
    {
        for (int i = 0; i < rows.Length; i++)
            rows[i] = Blank();
        row = column = 0;
    }

    public void MoveTo(int r, int c)
    {
        row = r - 1;
        column = c - 1;
    }

    public void Write(string text)
    {
        foreach (char c in text)
        {
            if (c == '\n')
            {
                row++;
                column = 0;
            }
            else
            {
                if (row >= 0 && row < rows.Length && column >= 0 && column < width)
                    rows[row][column] = c;
                column++;
            }
        }
    }

    public void PrintIfChanged()
    {
        var sb = new StringBuilder();
        foreach (char[] r in rows)
            sb.AppendLine(new string(r).TrimEnd());
        string now = sb.ToString();
        if (now == shown || now.Trim().Length == 0)
            return;
        shown = now;
        Console.Write($"\n[{new string('-', width - 2)}]\n{now}[{new string('-', width - 2)}]\n");
    }

    private char[] Blank()
    {
        var r = new char[width];
        Array.Fill(r, ' ');
        return r;
    }
}

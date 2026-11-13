using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Yomiel.Tui;

// A full-screen terminal player. Plays a story file, or lists a folder's stories to choose from.
// Leaving keeps the story's place: Ctrl+Q, Ctrl+C, closing the terminal and SIGTERM suspend it,
// and it continues next time.

internal static class Program
{
    private const string Usage = """
        Usage: yomiel-tui [story | folder] [options]

        Plays a story file. With a folder, or no argument, lists the stories
        to choose from. Sessions are saved automatically.

          --new         start the story over instead of continuing it
          --data DIR    folder for saves, transcripts and suspended stories
          --colors N    24 (true colour, the default) or 16
          --seed N      random number seed
          --no-more     don't pause with [MORE]
          --version     print the version and exit

        Keys: Page Up/Down scroll back, Up/Down earlier commands, Ctrl+Q leave, Ctrl+C quit.
        """;

    // The release version, set from the tag by the release build. The SDK appends "+<commit>"; it's left out.
    private static string Version
    {
        get
        {
            string v = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            int plus = v.IndexOf('+');
            return plus < 0 ? v : v.Substring(0, plus);
        }
    }

    private static int Main(string[] args)
    {
        string target = null;
        var settings = new Settings();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-h" or "--help":
                        Console.Error.WriteLine(Usage);
                        return 0;
                    case "--version":
                        Console.WriteLine($"yomiel-tui {Version}");
                        return 0;
                    case "--new": settings.Fresh = true; break;
                    case "--data": settings.DataRoot = args[++i]; break;
                    case "--colors":
                        settings.TrueColor = args[++i] switch
                        {
                            "24" => true,
                            "16" => false,
                            _ => throw new FormatException("--colors takes 24 or 16"),
                        };
                        break;
                    case "--seed": settings.Seed = int.Parse(args[++i]); break;
                    case "--no-more": settings.MorePrompts = false; break;
                    default:
                        if (args[i].StartsWith("-", StringComparison.Ordinal) || target != null)
                            throw new FormatException($"unknown option {args[i]}");
                        target = args[i];
                        break;
                }
            }
        }
        catch (Exception e) when (e is FormatException || e is IndexOutOfRangeException || e is OverflowException)
        {
            Console.Error.WriteLine($"yomiel-tui: {e.Message}\n\n{Usage}");
            return 2;
        }

        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine("yomiel-tui: needs a terminal. yomiel-cli plays through pipes.");
            return 2;
        }
        target ??= Directory.GetCurrentDirectory();
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            Console.Error.WriteLine($"yomiel-tui: {target} not found");
            return 1;
        }
        settings.DataRoot ??= DefaultDataRoot();
        return new App(settings).Run(target);
    }

    /// <summary>The per-user data folder: %APPDATA%\Yomiel, ~/Library/Application Support/Yomiel or $XDG_DATA_HOME/yomiel.</summary>
    private static string DefaultDataRoot()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yomiel");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "Yomiel");
        string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".local", "share") : xdg, "yomiel");
    }
}

internal sealed class Settings
{
    public bool Fresh;
    public string DataRoot;
    public bool TrueColor = true;
    public int? Seed;
    public bool MorePrompts = true;
    public int Scrollback = 2000;
}

internal sealed class App
{
    // The smallest screen a story is given. A smaller terminal shows its top left corner.
    private const int MinColumns = 20;
    private const int MinRows = 5;
    private const int IdleMilliseconds = 15;

    private enum Outcome { Ended, Left, Quit }

    private readonly Settings settings;
    private readonly Terminal terminal;
    private int stopRequested;
    private readonly ManualResetEventSlim stopped = new();

    public App(Settings settings)
    {
        this.settings = settings;
        terminal = new Terminal(settings.TrueColor);
    }

    private bool StopRequested => Volatile.Read(ref stopRequested) != 0;

    public int Run(string target)
    {
        var signals = new List<PosixSignalRegistration>();
        foreach (PosixSignal signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGINT })
        {
            try
            {
                signals.Add(PosixSignalRegistration.Create(signal, OnSignal));
            }
            catch (PlatformNotSupportedException) { }
        }

        string message = null;
        int code = 0;
        Exception crash = null;
        terminal.Enter();
        try
        {
            if (File.Exists(target))
            {
                StoryEntry entry = StoryEntry.Read(Path.GetFullPath(target), settings.DataRoot);
                (_, message) = Play(entry, settings.Fresh, fromList: false);
                code = message == null ? 0 : 1;
            }
            else
            {
                ChooseAndPlay(target);
            }
        }
        catch (Exception e)
        {
            // Reported after the screen is restored, where it can be read.
            crash = e;
        }
        finally
        {
            terminal.Leave();
            stopped.Set();
            foreach (PosixSignalRegistration registration in signals)
                registration.Dispose();
        }
        if (crash != null)
        {
            Console.Error.WriteLine($"yomiel-tui: {crash}");
            return 1;
        }
        if (message != null)
            Console.Error.WriteLine($"yomiel-tui: {message}");
        return code;
    }

    /// <summary>
    /// Closing the terminal or a signal: the main loop suspends the story and restores the
    /// terminal. Windows ends the process when this returns, so it waits for that.
    /// </summary>
    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Interlocked.Exchange(ref stopRequested, 1);
        stopped.Wait(TimeSpan.FromSeconds(4));
    }

    private void ChooseAndPlay(string folder)
    {
        var picker = new Picker(folder, settings.DataRoot);
        bool fresh = settings.Fresh;
        while (!StopRequested)
        {
            terminal.SetTitle("Yomiel");
            picker.Draw(terminal);
            Picker.Choice choice = Picker.Choice.None;
            if (Terminal.TryReadKey(out ConsoleKeyInfo key))
                choice = picker.Key(key);
            else
                Thread.Sleep(IdleMilliseconds);
            if (choice == Picker.Choice.Quit)
                return;
            if (choice is Picker.Choice.Play or Picker.Choice.StartOver)
            {
                StoryEntry entry = picker.Selected;
                if (!entry.Supported)
                {
                    picker.Message = $"{entry.Name} is a version {entry.Version} story. Yomiel plays versions 5, 7 and 8.";
                    continue;
                }
                (Outcome outcome, string message) = Play(entry, fresh || choice == Picker.Choice.StartOver, fromList: true);
                fresh = false;
                picker.Message = message;
                picker.Refresh();
                if (outcome == Outcome.Quit)
                    return;
            }
        }
    }

    private static (int Columns, int Rows) StorySize()
    {
        (int columns, int rows) = Terminal.Size;
        return (Math.Max(MinColumns, columns), Math.Max(MinRows, rows));
    }

    /// <summary>Plays a story until it ends, the player leaves or quits. Returns a message if it couldn't start.</summary>
    private (Outcome, string) Play(StoryEntry entry, bool fresh, bool fromList)
    {
        var storage = new ZStoryFolder(entry.DataFolder);
        (int columns, int rows) = StorySize();
        var options = new ZMachineOptions { ScreenColumns = columns, ScreenRows = rows, RandomSeed = settings.Seed };
        ZMachine machine = null;
        ZScreen screen = null;
        string note = null;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(entry.Path);
            ZSuspended suspended = fresh ? null : storage.ReadSuspended();
            if (suspended != null)
            {
                try
                {
                    machine = ZMachine.Resume(bytes, suspended.State, options);
                    screen = ZScreen.FromContents(suspended.Screen, settings.Scrollback);
                    screen?.Resize(columns, rows);
                }
                catch (ArgumentException e)
                {
                    machine = null;
                    note = $"[Couldn't continue where the story was left: {e.Message}. Starting over.]\n";
                }
            }
            machine ??= new ZMachine(bytes, options);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
        {
            return (Outcome.Ended, $"Can't open {entry.Name}: {e.Message}");
        }
        screen ??= new ZScreen(columns, rows, settings.Scrollback);
        if (note != null)
            screen.Write(note);
        terminal.SetTitle("Yomiel - " + entry.Name);

        string after = fromList ? "return to the list" : "exit";
        var session = new ZSession(machine, screen, new ZSessionOptions
        {
            Storage = storage,
            MorePrompts = settings.MorePrompts,
            EndText = $"[The story has ended. Press any key to {after}.]",
            ErrorText = $"[Yomiel stopped the story: {{0}}. Press any key to {after}.]",
            Sound = _ => terminal.Beep(),
        });

        Outcome outcome = Loop(session);
        string message = null;
        if (outcome != Outcome.Ended)
        {
            try
            {
                session.Suspend();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
            {
                message = $"Couldn't keep {entry.Name} for next time: {e.Message}";
            }
        }
        session.Close();
        return (outcome, message);
    }

    private Outcome Loop(ZSession session)
    {
        var clock = Stopwatch.StartNew();
        double last = 0;
        while (true)
        {
            if (StopRequested)
                return Outcome.Quit;

            (int columns, int rows) = StorySize();
            if (columns != session.Screen.Columns || rows != session.Screen.Rows)
                session.Resize(columns, rows);

            while (Terminal.TryReadKey(out ConsoleKeyInfo key))
            {
                bool control = (key.Modifiers & ConsoleModifiers.Control) != 0;
                if (control && key.Key == ConsoleKey.Q)
                    return Outcome.Left;
                if (control && key.Key == ConsoleKey.C)
                    return Outcome.Quit;
                if (ToKeyPress(key) is ZKeyPress press)
                    session.KeyPressed(press);
                if (session.Dismissed)
                    return Outcome.Ended;
            }

            double now = clock.Elapsed.TotalSeconds;
            session.Update(now - last);
            last = now;

            ZCell[] cells = session.Screen.Render(session.Scroll);
            ZScreen screen = session.Screen;
            if (session.ShowingMore)
                Overlay(cells, screen.Columns, screen.Rows - 1, 0, "[MORE]");
            if (session.Scroll > 0)
            {
                string note = $" {session.Scroll} lines back - Page Down returns ";
                Overlay(cells, screen.Columns, screen.Rows - 1, screen.Columns - note.Length, note);
            }
            terminal.Draw(cells, screen.Columns, screen.Rows, screen.CursorColumn, screen.CursorRow, session.AwaitingPlayer);

            if (session.Machine.Status != ZStatus.Running && !session.OutputPending)
                Thread.Sleep(IdleMilliseconds);
        }
    }

    private static void Overlay(ZCell[] cells, int columns, int row, int column, string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (column + i >= 0 && column + i < columns)
                cells[row * columns + column + i] = new ZCell(text[i], ZColor.Default, ZColor.Default, ZTextStyle.Reverse);
    }

    private static ZKeyPress? ToKeyPress(ConsoleKeyInfo key)
    {
        ZPlayerKey? special = key.Key switch
        {
            ConsoleKey.Enter => ZPlayerKey.Enter,
            ConsoleKey.Backspace => ZPlayerKey.Backspace,
            ConsoleKey.Delete => ZPlayerKey.Delete,
            ConsoleKey.Escape => ZPlayerKey.Escape,
            ConsoleKey.Tab => ZPlayerKey.Tab,
            ConsoleKey.UpArrow => ZPlayerKey.Up,
            ConsoleKey.DownArrow => ZPlayerKey.Down,
            ConsoleKey.LeftArrow => ZPlayerKey.Left,
            ConsoleKey.RightArrow => ZPlayerKey.Right,
            ConsoleKey.Home => ZPlayerKey.Home,
            ConsoleKey.End => ZPlayerKey.End,
            ConsoleKey.PageUp => ZPlayerKey.PageUp,
            ConsoleKey.PageDown => ZPlayerKey.PageDown,
            >= ConsoleKey.F1 and <= ConsoleKey.F12 => ZPlayerKey.F1 + (key.Key - ConsoleKey.F1),
            _ => null,
        };
        if (special is ZPlayerKey k)
            return new ZKeyPress(k);
        if (key.KeyChar >= ' ' && key.KeyChar != '\u007f')
            return ZKeyPress.Character(key.KeyChar);
        return null;
    }
}

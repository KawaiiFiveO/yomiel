using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Yomiel;

/// <summary>Keys a player presses, as a <see cref="ZSession"/> takes them.</summary>
public enum ZPlayerKey
{
    /// <summary>A typed character, in <see cref="ZKeyPress.Char"/>.</summary>
    Character,
    Enter, Backspace, Delete, Escape, Tab,
    Up, Down, Left, Right, Home, End, PageUp, PageDown,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
}

public readonly struct ZKeyPress
{
    public ZKeyPress(ZPlayerKey key, char c = '\0')
    {
        Key = key;
        Char = key == ZPlayerKey.Character ? c : '\0';
    }

    public static ZKeyPress Character(char c) => new(ZPlayerKey.Character, c);

    public ZPlayerKey Key { get; }

    /// <summary>The character for <see cref="ZPlayerKey.Character"/>, otherwise '\0'.</summary>
    public char Char { get; }

    public override string ToString() => Key == ZPlayerKey.Character ? $"'{Char}'" : Key.ToString();
}

public sealed class ZSessionOptions
{
    /// <summary>Saves, streams and the suspended story. Without it, saves and restores fail.</summary>
    public IZStorage Storage { get; set; }

    /// <summary>Slows output and input down. Null shows output as soon as the story prints it.</summary>
    public ZPacer Pacer { get; set; }

    /// <summary>Pause with [MORE] when more than a screenful of text arrives at once.</summary>
    public bool MorePrompts { get; set; } = true;

    /// <summary>The most time each <see cref="ZSession.Update"/> runs the machine for.</summary>
    public int RunMilliseconds { get; set; } = 8;

    /// <summary>Keys kept while the story is busy.</summary>
    public int MaxTypeAhead { get; set; } = 64;

    /// <summary>Shown when the story quits.</summary>
    public string EndText { get; set; } = "[The story has ended. Press any key.]";

    /// <summary>Shown when the story fails; {0} is <see cref="ZMachine.ErrorMessage"/>.</summary>
    public string ErrorText { get; set; } = "[Yomiel stopped the story: {0}. Press any key.]";

    /// <summary>A beep (1 high, 2 low) or sound effect number, when its place in the output is shown.</summary>
    public Action<int> Sound { get; set; }

    /// <summary>A <see cref="ZOutputKind.Warning"/>, or a file that couldn't be opened. For a log.</summary>
    public Action<string> Warning { get; set; }

    /// <summary>The story failed: <see cref="ZMachine.ErrorMessage"/>. Called once.</summary>
    public Action<string> Error { get; set; }
}

/// <summary>
/// Paces a <see cref="ZSession"/>. The base class does nothing, so output shows at once. Hosts
/// that imitate a slower machine or a modem subclass it. Each <see cref="ZSession.Update"/> calls
/// <see cref="BeforeReveal"/>, then reveals output that <see cref="MayReveal"/> allows, then calls
/// <see cref="AfterReveal"/>, then takes input unless <see cref="HoldInput"/>.
/// </summary>
public class ZPacer
{
    /// <summary>The session started, with its machine and screen.</summary>
    public virtual void Start(ZSession session) { }

    public virtual void BeforeReveal(ZSession session, double seconds) { }

    /// <summary>Whether <paramref name="item"/> may be shown yet. Later items wait behind it.</summary>
    public virtual bool MayReveal(ZSession session, ZOutput item) => true;

    /// <summary><paramref name="revealed"/>: whether any output reached the screen this update.</summary>
    public virtual void AfterReveal(ZSession session, double seconds, bool revealed) { }

    /// <summary>Keeps input waiting, and keys queued as type-ahead.</summary>
    public virtual bool HoldInput(ZSession session) => false;

    /// <summary>Keeps [MORE] from being dismissed, while its page is still appearing.</summary>
    public virtual bool HoldMore(ZSession session) => false;

    /// <summary>
    /// A save or restore reached the disk (<paramref name="skip"/> false) or was cancelled or found
    /// nothing (true). Call <paramref name="complete"/> now or later.
    /// </summary>
    public virtual void DiskOperation(ZSession session, bool skip, Action complete) => complete();
}

/// <summary>
/// Plays a story on a <see cref="ZScreen"/>: queues the machine's output and reveals it, pausing
/// at [MORE], and takes keys for line editing with history, single keys, timed input, save and
/// restore prompts and scrolling back. Hosts call <see cref="Update"/> every frame,
/// <see cref="KeyPressed"/> for each key, and draw <see cref="Screen"/>.
/// </summary>
public sealed class ZSession
{
    private const int FileNameLength = 60;

    private enum FilePrompt { None, Save, Restore }

    private readonly ZSessionOptions options;
    private readonly ZPacer pacer;

    // Output
    private readonly Queue<ZOutput> display = new();
    private ZOutput textItem;
    private int textOffset;
    private bool more;
    private long moreMark;
    private bool ended;
    private int scroll;

    // Input
    private ZLineRequest activeLine;
    private readonly StringBuilder edit = new();
    private int cursor;
    private string takenBack;
    private readonly List<string> history = new();
    private int historyIndex;
    private double timer;
    private readonly Queue<ZKeyPress> typeAhead = new();
    private FilePrompt filePrompt;
    private string lastSaveName = "save";

    // Streams
    private TextWriter transcript;
    private TextWriter record;
    private readonly Queue<string> replay = new();

    public ZSession(ZMachine machine, ZScreen screen, ZSessionOptions options = null)
    {
        Machine = machine ?? throw new ArgumentNullException(nameof(machine));
        Screen = screen ?? throw new ArgumentNullException(nameof(screen));
        this.options = options ?? new ZSessionOptions();
        pacer = this.options.Pacer ?? new ZPacer();
        moreMark = screen.LowerLineCount;
        pacer.Start(this);
    }

    public ZMachine Machine { get; }
    public ZScreen Screen { get; }

    /// <summary>A screenful arrived; the next key continues.</summary>
    public bool ShowingMore => more;

    /// <summary>Lines scrolled back from the bottom, for <see cref="ZScreen.Render"/>.</summary>
    public int Scroll => scroll;

    /// <summary>The story quit or failed, and the message says so.</summary>
    public bool Ended => ended;

    /// <summary>A key was pressed after the story ended.</summary>
    public bool Dismissed { get; private set; }

    /// <summary>Output the machine produced that isn't on the screen yet.</summary>
    public bool OutputPending => textItem != null || display.Count > 0;

    /// <summary>The screen has caught up, so the session can take input.</summary>
    public bool InputReady => !more && !OutputPending && !pacer.HoldInput(this);

    /// <summary>The player is being asked for a key, a line or a file name: show the cursor.</summary>
    public bool AwaitingPlayer => InputReady && scroll == 0 && !ended && (filePrompt != FilePrompt.None
        || Machine.Status == ZStatus.WaitingForLine || Machine.Status == ZStatus.WaitingForKey);

    private int LowerRows => Screen.Rows - Screen.UpperHeight;

    // ---- Every frame ----

    public void Update(double seconds)
    {
        if (Machine.Status == ZStatus.Running)
        {
            Stopwatch clock = Stopwatch.StartNew();
            do
                Machine.Run(20_000);
            while (Machine.Status == ZStatus.Running && clock.ElapsedMilliseconds < options.RunMilliseconds);
        }
        while (Machine.TryGetOutput(out ZOutput item))
            Collect(item);

        pacer.BeforeReveal(this, seconds);
        bool revealed = Reveal();
        pacer.AfterReveal(this, seconds, revealed);
        if (InputReady)
            Answer(seconds);
    }

    /// <summary>Handles file output at once and queues screen output for display.</summary>
    private void Collect(ZOutput item)
    {
        switch (item.Kind)
        {
            case ZOutputKind.Transcript:
                transcript?.Write(item.Text.Replace("\n", Environment.NewLine));
                break;
            case ZOutputKind.CommandRecord:
                record?.WriteLine(item.Text);
                break;
            case ZOutputKind.OutputStream:
                OpenOrCloseStream(item.Value);
                break;
            case ZOutputKind.InputStream:
                replay.Clear();
                if (item.Value == 1 && options.Storage != null)
                {
                    try
                    {
                        foreach (string line in options.Storage.ReadCommands())
                            replay.Enqueue(line);
                    }
                    catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                    {
                        options.Warning?.Invoke($"Couldn't read the commands for input stream 1: {e.Message}");
                    }
                }
                break;
            case ZOutputKind.Warning:
                options.Warning?.Invoke(item.Text);
                break;
            default:
                display.Enqueue(item);
                break;
        }
    }

    private void OpenOrCloseStream(int stream)
    {
        try
        {
            switch (stream)
            {
                case 2:
                    transcript ??= options.Storage?.OpenTranscript();
                    break;
                case -2:
                    transcript?.Dispose();
                    transcript = null;
                    break;
                case 4:
                    record ??= options.Storage?.OpenCommandRecord();
                    break;
                case -4:
                    record?.Dispose();
                    record = null;
                    break;
            }
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            options.Warning?.Invoke($"Couldn't open a file for output stream {stream}: {e.Message}");
        }
    }

    /// <summary>Moves queued output onto the screen, stopping when a screenful has arrived. Returns whether anything was shown.</summary>
    private bool Reveal()
    {
        bool shown = false;
        while (!more)
        {
            if (textItem != null)
            {
                bool echo = textItem.Kind == ZOutputKind.InputEcho;
                string text = textItem.Text;
                while (textOffset < text.Length)
                {
                    if (!echo && options.MorePrompts && Screen.Window == 0 && Screen.LowerLineCount - moreMark >= LowerRows - 1)
                    {
                        more = true;
                        return shown;
                    }
                    Screen.Write(text[textOffset++]);
                    shown = true;
                }
                if (echo)
                    moreMark = Screen.LowerLineCount;
                textItem = null;
            }
            if (display.Count == 0 || !pacer.MayReveal(this, display.Peek()))
                return shown;
            ZOutput item = display.Dequeue();
            shown = true;
            if (item.Kind is ZOutputKind.Text or ZOutputKind.InputEcho)
            {
                textItem = item;
                textOffset = 0;
                continue;
            }
            if (item.Kind == ZOutputKind.Sound)
                options.Sound?.Invoke(item.Value);
            Screen.Apply(item);
            if (item.Kind == ZOutputKind.EraseWindow && item.Value != 1 || item.Kind == ZOutputKind.Restarted)
                moreMark = Screen.LowerLineCount;
        }
        return shown;
    }

    /// <summary>The display has caught up: start or continue whatever the machine is waiting for.</summary>
    private void Answer(double seconds)
    {
        switch (Machine.Status)
        {
            case ZStatus.WaitingForLine:
                if (activeLine != Machine.LineRequest)
                    BeginLine();
                if (replay.Count > 0)
                {
                    edit.Clear().Append(replay.Dequeue());
                    SubmitLine(ZKey.Enter);
                    return;
                }
                Tick(seconds, Machine.LineRequest.TimeoutTenths);
                break;
            case ZStatus.WaitingForKey:
                Tick(seconds, Machine.KeyRequest.TimeoutTenths);
                break;
            case ZStatus.WaitingForSave:
                if (filePrompt == FilePrompt.None)
                    BeginSave();
                break;
            case ZStatus.WaitingForRestore:
                if (filePrompt == FilePrompt.None)
                    BeginRestore();
                break;
            case ZStatus.Halted:
                End(options.EndText);
                return;
            case ZStatus.Error:
                if (!ended)
                    options.Error?.Invoke(Machine.ErrorMessage);
                End(string.Format(options.ErrorText, Machine.ErrorMessage));
                return;
            default:
                return;
        }
        while (typeAhead.Count > 0 && InputReady && IsWaitingForPlayer)
            HandleKey(typeAhead.Dequeue());
    }

    private bool IsWaitingForPlayer =>
        Machine.Status == ZStatus.WaitingForKey || Machine.Status == ZStatus.WaitingForLine && activeLine == Machine.LineRequest || filePrompt != FilePrompt.None;

    private void Tick(double seconds, int tenths)
    {
        if (tenths <= 0)
            return;
        timer += seconds;
        if (timer < tenths / 10.0)
            return;
        timer = 0;
        Machine.Interrupt(Machine.Status == ZStatus.WaitingForLine ? edit.ToString() : null);
    }

    private void End(string message)
    {
        if (ended)
            return;
        ended = true;
        try
        {
            options.Storage?.DeleteSuspended();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            options.Warning?.Invoke($"Couldn't remove the suspended story: {e.Message}");
        }
        Screen.Window = 0;
        Screen.Input = null;
        Screen.Write("\n\n" + message);
    }

    // ---- Line input ----

    private void BeginLine()
    {
        activeLine = Machine.LineRequest;
        string initial = activeLine.InitialText;
        takenBack = initial.Length > 0 && Screen.TakeBack(initial) ? initial : null;
        edit.Clear().Append(initial);
        cursor = edit.Length;
        historyIndex = history.Count;
        timer = 0;
        ShowEdit();
    }

    private void ShowEdit()
    {
        Screen.Input = edit.ToString();
        Screen.InputCursor = cursor;
    }

    private void SubmitLine(ZKey terminator)
    {
        string line = edit.ToString();
        Screen.Input = null;
        // The story printed the pre-loaded text; put it back where it was before the echo follows it.
        if (takenBack != null && line.StartsWith(takenBack, StringComparison.Ordinal))
            Screen.Write(takenBack);
        takenBack = null;
        if (terminator == ZKey.Enter && line.Trim().Length > 0 && (history.Count == 0 || history[history.Count - 1] != line))
            history.Add(line);
        timer = 0;
        Machine.SubmitLine(line, terminator);
    }

    private int MaxLength => filePrompt != FilePrompt.None ? FileNameLength : activeLine?.MaxLength ?? 0;

    /// <summary>Edits the line being typed. Returns false for keys that don't edit.</summary>
    private bool EditLine(ZKeyPress key)
    {
        switch (key.Key)
        {
            case ZPlayerKey.Character when key.Char >= ' ':
                if (edit.Length < MaxLength)
                    edit.Insert(cursor++, key.Char);
                break;
            case ZPlayerKey.Backspace when cursor > 0:
                edit.Remove(--cursor, 1);
                break;
            case ZPlayerKey.Delete when cursor < edit.Length:
                edit.Remove(cursor, 1);
                break;
            case ZPlayerKey.Left when cursor > 0:
                cursor--;
                break;
            case ZPlayerKey.Right when cursor < edit.Length:
                cursor++;
                break;
            case ZPlayerKey.Home:
                cursor = 0;
                break;
            case ZPlayerKey.End:
                cursor = edit.Length;
                break;
            case ZPlayerKey.Up when filePrompt == FilePrompt.None && historyIndex > 0:
                edit.Clear().Append(history[--historyIndex]);
                cursor = edit.Length;
                break;
            case ZPlayerKey.Down when filePrompt == FilePrompt.None && historyIndex < history.Count:
                historyIndex++;
                edit.Clear().Append(historyIndex < history.Count ? history[historyIndex] : "");
                cursor = edit.Length;
                break;
            default:
                return false;
        }
        ShowEdit();
        return true;
    }

    // ---- Keys ----

    public void KeyPressed(ZKeyPress key)
    {
        if (key.Key == ZPlayerKey.PageUp || key.Key == ZPlayerKey.PageDown)
        {
            int page = Math.Max(1, LowerRows - 1);
            scroll = Math.Max(0, Math.Min(Screen.ScrollbackAvailable, scroll + (key.Key == ZPlayerKey.PageUp ? page : -page)));
            return;
        }
        scroll = 0;
        if (ended)
        {
            Dismissed = true;
            return;
        }
        if (more)
        {
            // [MORE] shows once its page has been revealed.
            if (!pacer.HoldMore(this))
            {
                more = false;
                moreMark = Screen.LowerLineCount;
            }
            return;
        }
        if (!InputReady || !IsWaitingForPlayer)
        {
            if (typeAhead.Count < options.MaxTypeAhead)
                typeAhead.Enqueue(key);
            return;
        }
        HandleKey(key);
    }

    private void HandleKey(ZKeyPress key)
    {
        if (filePrompt != FilePrompt.None)
        {
            if (key.Key == ZPlayerKey.Enter)
                FinishFilePrompt(cancelled: false);
            else if (key.Key == ZPlayerKey.Escape)
                FinishFilePrompt(cancelled: true);
            else
                EditLine(key);
            return;
        }

        if (Machine.Status == ZStatus.WaitingForKey)
        {
            moreMark = Screen.LowerLineCount;
            timer = 0;
            if (key.Key == ZPlayerKey.Character)
                Machine.SubmitKey(key.Char);
            else if (ToZKey(key.Key) is ZKey z)
                Machine.SubmitKey(z);
            return;
        }

        if (Machine.Status != ZStatus.WaitingForLine)
            return;
        ZKey? terminator = ToZKey(key.Key);
        if (key.Key == ZPlayerKey.Enter)
        {
            SubmitLine(ZKey.Enter);
        }
        else if (terminator is ZKey t && t != ZKey.Delete && t != ZKey.Escape && activeLine.IsTerminator(t))
        {
            SubmitLine(t);
        }
        else if (key.Key == ZPlayerKey.Escape)
        {
            edit.Clear();
            cursor = 0;
            ShowEdit();
        }
        else
        {
            EditLine(key);
        }
    }

    private static ZKey? ToZKey(ZPlayerKey key) => key switch
    {
        ZPlayerKey.Enter => ZKey.Enter,
        ZPlayerKey.Backspace => ZKey.Delete,
        ZPlayerKey.Delete => ZKey.Delete,
        ZPlayerKey.Escape => ZKey.Escape,
        ZPlayerKey.Up => ZKey.Up,
        ZPlayerKey.Down => ZKey.Down,
        ZPlayerKey.Left => ZKey.Left,
        ZPlayerKey.Right => ZKey.Right,
        >= ZPlayerKey.F1 and <= ZPlayerKey.F12 => ZKey.F1 + (key - ZPlayerKey.F1),
        _ => null,
    };

    // ---- Saving and restoring ----

    private void BeginSave()
    {
        ZSaveRequest request = Machine.SaveRequest;
        if (request.IsAuxiliary)
        {
            Machine.CompleteSave(TryWrite(s => s.WriteData(request.SuggestedName ?? "data", request.Data)));
            return;
        }
        StartFilePrompt(FilePrompt.Save, $"\nSave as [{lastSaveName}]: ");
    }

    private void BeginRestore()
    {
        ZRestoreRequest request = Machine.RestoreRequest;
        if (request.IsAuxiliary)
        {
            Machine.CompleteRestore(TryRead(s => s.ReadData(request.SuggestedName ?? "data")));
            return;
        }
        IList<string> saves = TryList();
        if (saves.Count == 0)
        {
            Screen.Write("\n[There are no saved games.]\n");
            Machine.CompleteRestore(null);
            return;
        }
        if (!saves.Contains(lastSaveName, StringComparer.OrdinalIgnoreCase))
            lastSaveName = saves[0];
        StartFilePrompt(FilePrompt.Restore, $"\nSaved games: {string.Join(", ", saves)}\nRestore which [{lastSaveName}]: ");
    }

    private void StartFilePrompt(FilePrompt kind, string prompt)
    {
        filePrompt = kind;
        Screen.Write(prompt);
        edit.Clear();
        cursor = 0;
        ShowEdit();
    }

    private void FinishFilePrompt(bool cancelled)
    {
        FilePrompt kind = filePrompt;
        filePrompt = FilePrompt.None;
        string name = edit.ToString().Trim();
        Screen.Input = null;
        Screen.Write(cancelled ? "\n" : name + "\n");
        moreMark = Screen.LowerLineCount;
        if (name.Length == 0)
            name = lastSaveName;
        name = ZStoryFolder.SafeName(name);

        if (kind == FilePrompt.Save)
        {
            bool saved = !cancelled && TryWrite(s => s.WriteSave(name, Machine.SaveRequest.Data));
            if (saved)
                lastSaveName = name;
            pacer.DiskOperation(this, cancelled, () => Machine.CompleteSave(saved));
            return;
        }

        byte[] data = cancelled ? null : TryRead(s => s.ReadSave(name));
        if (!cancelled && data == null)
            Screen.Write($"[There is no saved game called {name}.]\n");
        if (data != null)
            lastSaveName = name;
        pacer.DiskOperation(this, cancelled || data == null, () =>
        {
            if (!Machine.CompleteRestore(data) && data != null)
                Screen.Write($"[{Machine.RestoreError}.]\n");
        });
    }

    private bool TryWrite(Action<IZStorage> write)
    {
        if (options.Storage == null)
        {
            Screen.Write("[Couldn't save: there is nowhere to save.]\n");
            return false;
        }
        try
        {
            write(options.Storage);
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            Screen.Write($"[Couldn't save: {e.Message}]\n");
            return false;
        }
    }

    private byte[] TryRead(Func<IZStorage, byte[]> read)
    {
        try
        {
            return options.Storage == null ? null : read(options.Storage);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return null;
        }
    }

    private IList<string> TryList()
    {
        try
        {
            return options.Storage?.SaveNames() ?? new List<string>();
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Changes the screen size, for the screen and for the story, which sees it at its next
    /// status line or box. Text already on the screen wraps again.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        Screen.Resize(columns, rows);
        Machine.SetScreenSize(columns, rows);
        moreMark = Screen.LowerLineCount;
        scroll = 0;
    }

    // ---- Leaving ----

    /// <summary>
    /// Keeps the story so the next session continues where this one stopped, in
    /// <see cref="IZStorage.WriteSuspended"/>, or removes the suspended story if this one can't
    /// continue. Shows all pending output first, and answers a pending save or restore as failed.
    /// Text being typed is dropped. Call it when leaving, then <see cref="Close"/>.
    /// </summary>
    /// <exception cref="IOException">The storage couldn't write.</exception>
    /// <exception cref="UnauthorizedAccessException">The storage couldn't write.</exception>
    public void Suspend()
    {
        if (options.Storage == null)
            return;
        if (Machine.Status == ZStatus.WaitingForSave)
            Machine.CompleteSave(false);
        else if (Machine.Status == ZStatus.WaitingForRestore)
            Machine.CompleteRestore(null);
        for (int i = 0; i < 100 && Machine.Status == ZStatus.Running; i++)
            Machine.Run();
        while (Machine.TryGetOutput(out ZOutput item))
            Collect(item);
        if (textItem != null)
            Screen.Write(textItem.Text.Substring(textOffset));
        textItem = null;
        while (display.Count > 0)
            Screen.Apply(display.Dequeue());
        Screen.Input = null;
        if (takenBack != null && activeLine != null)
            Screen.Write(takenBack);
        takenBack = null;

        if (!ended && Machine.Status is ZStatus.WaitingForLine or ZStatus.WaitingForKey or ZStatus.Running)
            options.Storage.WriteSuspended(new ZSuspended(Machine.SaveState(), Screen.SaveContents()));
        else
            options.Storage.DeleteSuspended();
    }

    /// <summary>Closes the transcript and command record.</summary>
    public void Close()
    {
        transcript?.Dispose();
        transcript = null;
        record?.Dispose();
        record = null;
    }
}

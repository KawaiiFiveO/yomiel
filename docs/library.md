# Yomiel library

Z-machine interpreter core for .NET Standard 2.0. Runs story files without a display; the host steps the machine, draws its output and answers its requests.

## Support

| Item | Status |
|---|---|
| Versions | 5 and 8. 7 accepted, untested. 1–4 and 6 refused (`ZMachine.IsSupportedVersion`). |
| Standard | Z-Machine Standards Document 1.1, without sound effects and mouse. Reports 1.1. |
| Saves | Quetzal 1.4 |
| Runtimes | .NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+, Unity, Godot |
| Dependencies | None. No reflection, no native code. |

## Usage

```csharp
var machine = new ZMachine(File.ReadAllBytes("story.z5"), new ZMachineOptions { ScreenColumns = 100 });

// Each frame:
ZStatus status = machine.Run(100_000);
while (machine.TryGetOutput(out ZOutput item))
    Draw(item);

switch (status)
{
    case ZStatus.WaitingForLine:
        machine.SubmitLine(line);
        break;
    case ZStatus.WaitingForKey:
        machine.SubmitKey(key);
        break;
    case ZStatus.WaitingForSave:
        File.WriteAllBytes(path, machine.SaveRequest.Data);
        machine.CompleteSave(true);
        break;
    case ZStatus.WaitingForRestore:
        machine.CompleteRestore(File.ReadAllBytes(path)); // null if there is none
        break;
}
```

`Run(n)` executes at most `n` instructions. It returns early when the story waits for the host, quits (`Halted`) or fails (`Error`, see `ErrorMessage`). Requests can be answered over several frames.

## Output

`TryGetOutput` returns `ZOutput` items in order. Each has the `Window` it was output in and the `InstructionCount` at the time.

| Kind | Meaning |
|---|---|
| `Text` | Text in `Window` 0 (lower) or 1 (upper). Line breaks are `\n`. |
| `InputEcho` | The submitted line, ending in `\n` if ended by Enter. Excludes pre-loaded text. |
| `SplitWindow` | Upper window height is `Value` lines. |
| `SetWindow` | Text goes to window `Value`. |
| `EraseWindow` | Clear window `Value`: 0, 1, -1 (unsplit and clear) or -2 (clear). |
| `EraseLine` | Clear from the cursor to the end of the line. |
| `SetCursor` | Cursor to `Row`, `Column`, 1-based. |
| `SetStyle` | `Style`: Roman, or any of Reverse, Bold, Italic, FixedPitch. |
| `SetColor` | `Foreground`, `Background`. |
| `SetFont` | Font `Value`: 1 normal, 4 fixed pitch. |
| `BufferMode` | Lower-window wrapping `Value`: 1 on, 0 off. |
| `Sound` | Beep (`Value` 1 high, 2 low) or sound effect `Value`. |
| `Transcript` | Text for the transcript file (stream 2). |
| `CommandRecord` | One command for the command file (stream 4). |
| `OutputStream` | Stream `Value` (2 or 4) opened, or closed if negative. The host opens or closes the file. |
| `InputStream` | Commands come from a file (`Value` 1) or the keyboard (0). The host submits the file's lines. |
| `Restarted` | The story restarted. Reset windows and styles. |
| `Warning` | Undefined behaviour the machine continued through, such as a number that isn't an object. Once per instruction. For a log. |

`ZColor` is the default colour, a standard colour (`Index` 2–12) or a 15-bit true colour (`Rgb15`). `ToRgb24` converts with the Standard 1.1 values.

## Input

| Member | Meaning |
|---|---|
| `LineRequest.MaxLength` | Longer lines are cut. |
| `LineRequest.InitialText` | Pre-loaded text. Show it as typed; submit it as part of the line. |
| `LineRequest.Terminators` | Function keys that also end the line. |
| `LineRequest.TimeoutTenths`, `KeyRequest.TimeoutTenths` | Nonzero for timed input. |
| `Interrupt(partialLine)` | Call each time the timeout passes, then `Run`. The story ends the input or waits again. |
| `InterruptPrinted` | The timed routine printed. Show the typed text again below. |

Lines are lowercased for the story.

## Saves and undo

* `SaveState()`: the whole machine as a Quetzal file, while running or waiting for input.
* `ZMachine.Resume(story, state)`: continues from `SaveState` output or from the story's own save. Outputs the window, style, colour and font; screen contents are not saved.
* `save_undo` states are kept in memory, `UndoLevels` deep.

## Screen model

`ZScreen` is an optional character grid for hosts that draw fixed-width cells. Feed it with `Apply(item)`, or `Write(c)` to pace text. `Render(scrollBack)` returns `Columns` × `Rows` `ZCell`s: character, colours, style.

| Member | Meaning |
|---|---|
| Upper window | Grid over the top rows, no wrapping. |
| Lower window | Word wraps at spaces; keeps `scrollbackLines` lines. Text starts at the top after a clear. |
| `Input`, `InputCursor` | Line being typed, after the last line. |
| `CursorColumn`, `CursorRow` | Cursor position after `Render`, or -1. |
| `TakeBack(text)` | Removes pre-loaded input from the last line, to edit it as `Input`. |
| `LowerLineCount` | Line breaks so far, for `[MORE]`. |
| `ShownLines`, `ShownColumns` | Hide lines from `ShownLines` on, except the first `ShownColumns` characters of that line. For line-by-line or character-by-character reveal. |
| `LineLength(n, wholeWords)` | Length of line `n`; with `wholeWords`, the line being written counts only to its last space. |
| `Resize(columns, rows)` | New size. The lower window and scrollback wrap again; the upper window keeps its top left corner. |
| `SaveContents()`, `LoadContents(data)` | Screen contents, for a suspended story. `LoadContents` needs the same size. |
| `ZScreen.FromContents(data)` | A screen at the size the contents were saved at, or null. Then `Resize`. |

Contents format 2 keeps where lines wrapped. Format 1 loads with every line ending in a line break.

## Sessions

`ZSession` plays a story on a `ZScreen`: what a host needs besides drawing and reading keys. Call `Update(seconds)` every frame, `KeyPressed(key)` for each key, and draw `Screen.Render(Scroll)`.

```csharp
var session = new ZSession(machine, new ZScreen(80, 25), new ZSessionOptions { Storage = new ZStoryFolder(folder) });
```

* Output appears as the story prints it. `[MORE]` pauses when a screenful arrives at once (`ShowingMore`).
* Line input: editing (Left, Right, Home, End, Backspace, Delete), Escape clears, Up and Down recall earlier commands, pre-loaded text, terminator keys.
* Keys pressed while the story is busy are kept (`MaxTypeAhead`, 64).
* Timed input calls `Interrupt` when the timeout passes.
* Save and restore ask for a name in the lower window. Restore lists the saves.
* Streams 2 and 4 write `transcript.txt` and `commands.txt`; input stream 1 replays `commands.txt`.
* Page Up and Page Down scroll back (`Scroll`).
* When the story quits or fails, `EndText` or `ErrorText` is shown and `Ended` is set; the next key sets `Dismissed`.
* `Resize(columns, rows)`: the screen and the story's screen size.
* `Suspend()`: keeps the story in the storage for next time, or removes it if the story has ended. Then `Close()`.

| Member | Meaning |
|---|---|
| `AwaitingPlayer` | Waiting for a key, a line or a name: show the cursor. |
| `InputReady` | All output is shown. |
| `OutputPending` | Output not on the screen yet. |

`ZSessionOptions`:

| Option | Default | Meaning |
|---|---|---|
| `Storage` | none | `IZStorage` for saves, streams and the suspended story. Without it, saves fail. |
| `Pacer` | none | `ZPacer` subclass that slows output and input down. |
| `MorePrompts` | true | Pause at `[MORE]`. |
| `RunMilliseconds` | 8 | Longest machine run per `Update`. |
| `MaxTypeAhead` | 64 | Keys kept while the story is busy. |
| `EndText`, `ErrorText` | | Shown when the story quits or fails. `{0}` in `ErrorText` is the error. |
| `Sound`, `Warning`, `Error` | | Callbacks: beeps, warnings for a log, the story failing. |

`ZStoryFolder(folder)` is the `IZStorage` in a folder: `name.qzl` saves, `data\` for auxiliary data, `transcript.txt`, `commands.txt`, `suspended.qzl` and `suspended.screen`. `ZStoryFolder.SafeName` makes a name safe for a file.

`ZPacer` methods, called in this order each `Update`: `BeforeReveal`, `MayReveal` for each item, `AfterReveal`, then `HoldInput`. `HoldMore` keeps `[MORE]` showing; `DiskOperation` runs a save or restore's completion, now or later. The base class does nothing.

## Options

`ZMachineOptions`:

| Option | Default | Meaning |
|---|---|---|
| `ScreenColumns`, `ScreenRows` | 80, 25 | Screen size in characters. Change later with `SetScreenSize`. |
| `Colors`, `Bold`, `Italic`, `FixedPitch`, `TimedInput` | true | Capabilities reported to the story. |
| `DefaultForeground`, `DefaultBackground` | 9, 2 | Default colours reported to the story. |
| `InterpreterNumber`, `InterpreterVersion` | 6, `Y` | Header interpreter number (6: IBM PC) and version. |
| `RandomSeed` | clock | Random number seed. |
| `UndoLevels` | 16 | `save_undo` states kept. |
| `CanPrint` | all | Characters the host can draw, for `check_unicode`. |

# Yomiel

Z-machine interpreter core for .NET Standard 2.0. Runs story files without a display; the host steps the machine, draws its output and answers its requests.

## Support

| Item | Status |
|---|---|
| Versions | 5 and 8. 7 accepted, untested. 1–4 and 6 refused. |
| Standard | Z-Machine Standards Document 1.1, without sound effects and mouse. Reports 1.1. |
| Saves | Quetzal 1.4 |
| Runtimes | .NET Framework 4.6.1+, .NET Core 2.0+, .NET 5+, Unity, Godot |
| Dependencies | None. No reflection, no native code. |

## Building

```
dotnet build Yomiel
dotnet test Yomiel.Tests
```

`Yomiel.Tests` targets `net10.0` and `net472`. It reads story files from `stories/`, or from the folder in `YOMIEL_STORIES`. Tests without their story are skipped.

| File | Source |
|---|---|
| `czech.z5` | [IF Archive](https://ifarchive.org/if-archive/infocom/interpreters/tools/czech_0_8.zip) |
| `praxix.z5` | [IF Archive](https://ifarchive.org/if-archive/infocom/interpreters/tools/praxix.zip) |
| `etude.z5` | [IF Archive](https://ifarchive.org/if-archive/infocom/interpreters/tools/etude.tar.Z) |
| `strictz.z5` | [IF Archive](https://ifarchive.org/if-archive/infocom/interpreters/tools/strictz.z5) |
| `Tangle.z5` | *Spider and Web*, Andrew Plotkin |
| `nameless.z8` | *Endless, Nameless*, Adam Cadre |
| `shrapnel.z5` | *Shrapnel*, Adam Cadre |
| `I-0.z5` | *I-0*, Adam Cadre |

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

### Output

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

### Input

| Member | Meaning |
|---|---|
| `LineRequest.MaxLength` | Longer lines are cut. |
| `LineRequest.InitialText` | Pre-loaded text. Show it as typed; submit it as part of the line. |
| `LineRequest.Terminators` | Function keys that also end the line. |
| `LineRequest.TimeoutTenths`, `KeyRequest.TimeoutTenths` | Nonzero for timed input. |
| `Interrupt(partialLine)` | Call each time the timeout passes, then `Run`. The story ends the input or waits again. |
| `InterruptPrinted` | The timed routine printed. Show the typed text again below. |

Lines are lowercased for the story.

### Saves and undo

* `SaveState()`: the whole machine as a Quetzal file, while running or waiting for input.
* `ZMachine.Resume(story, state)`: continues from `SaveState` output or from the story's own save. Outputs the window, style, colour and font; screen contents are not saved.
* `save_undo` states are kept in memory, `UndoLevels` deep.

### Screen model

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
| `SaveContents()`, `LoadContents(data)` | Screen contents, for a suspended story. |

### Options

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

## Console player

`Yomiel.Cli` builds `yomiel-cli`, a plain console player for testing.

```
yomiel-cli <story> [--width N] [--height N] [--seed N] [--no-upper] [--stats]
```

| Option | Effect |
|---|---|
| `--width N`, `--height N` | Screen size. Default 80 × 25. |
| `--seed N` | Random number seed. |
| `--no-upper` | Don't print the upper window. |
| `--stats` | Print the instruction count and speed at exit. |

* Lower-window text goes to standard output; warnings to standard error.
* The upper window is printed above each prompt when it has changed.
* Single-key input takes the first character of a line, or Enter for an empty line.
* Saves and restores ask for a file name.

## License

0BSD. See `LICENSE.txt`.

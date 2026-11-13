# Development

| Project | Output |
|---|---|
| `Yomiel` | `Yomiel.dll`, the library (`netstandard2.0`). See `library.md`. |
| `Yomiel.Tui` | `yomiel-tui`, the terminal player. |
| `Yomiel.Cli` | `yomiel-cli`, a plain console player for pipes and tests. |
| `Yomiel.Tests` | Tests, on `net10.0` and `net472`. |

## Building

```
dotnet build Yomiel
dotnet build Yomiel.Tui
dotnet test Yomiel.Tests
```

## Tests

`Yomiel.Tests` reads story files from the folder in `YOMIEL_STORIES`, then `Yomiel.Tests/stories/`, then `stories/`. Tests without their story are skipped.

`Yomiel.Tests/stories/` holds the redistributable stories (`czech.z5`, `praxix.z5`, `Tangle.z5`); their licenses are in its `README.md`. Put the others in `stories/`, which is gitignored. Releases contain no story files.

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

## Console player

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

## Terminal player internals

* Draws with ANSI escape codes on the alternate screen, sending only changed cells.
* Colours: true colour (`38;2`/`48;2`), or the nearest of the 16 ANSI colours with `--colors 16`. The story's default colours are left as the terminal's own. Beeps are BEL.
* Windows: turns on virtual terminal processing for the console and restores it on exit.
* Window title: "Yomiel" in the picker, "Yomiel - name" in a story (the file name without its extension). Restored on exit: on Windows from `Console.Title`, elsewhere from the xterm title stack (`CSI 22;0 t` and `CSI 23;0 t`). Terminals without a title stack keep the last title.
* Suspends on Ctrl+Q, Ctrl+C (`TreatControlCAsInput`), SIGTERM, SIGHUP and SIGINT (`PosixSignalRegistration`). Closing a Windows console arrives as SIGHUP. The signal handler waits up to 4 seconds for the main loop to suspend.
* Each story's data folder is named from its file name (40 characters at most), release, serial number and checksum.

using System;

namespace Yomiel;

/// <summary>What the machine is doing, as returned by <see cref="ZMachine.Run"/>.</summary>
public enum ZStatus
{
    /// <summary>Executing. Call <see cref="ZMachine.Run"/> again.</summary>
    Running,

    /// <summary>Waiting for a line: see <see cref="ZMachine.LineRequest"/>, answer with <see cref="ZMachine.SubmitLine"/>.</summary>
    WaitingForLine,

    /// <summary>Waiting for one key: see <see cref="ZMachine.KeyRequest"/>, answer with <see cref="ZMachine.SubmitKey(char)"/>.</summary>
    WaitingForKey,

    /// <summary>Waiting for the host to store a save: see <see cref="ZMachine.SaveRequest"/>, answer with <see cref="ZMachine.CompleteSave"/>.</summary>
    WaitingForSave,

    /// <summary>Waiting for the host to load a save: see <see cref="ZMachine.RestoreRequest"/>, answer with <see cref="ZMachine.CompleteRestore"/>.</summary>
    WaitingForRestore,

    /// <summary>The story quit.</summary>
    Halted,

    /// <summary>The story did something illegal: see <see cref="ZMachine.ErrorMessage"/>.</summary>
    Error,
}

/// <summary>Kinds of <see cref="ZOutput"/>.</summary>
public enum ZOutputKind
{
    /// <summary><see cref="ZOutput.Text"/> printed in <see cref="ZOutput.Window"/>. Line breaks are '\n'.</summary>
    Text,

    /// <summary>
    /// Echo of a line the host submitted, ending in '\n' when ended by Enter. Leaves out
    /// <see cref="ZLineRequest.InitialText"/> when the line starts with it, since the story printed
    /// that. Hosts that drew the line while it was typed can skip it.
    /// </summary>
    InputEcho,

    /// <summary>The upper window is now <see cref="ZOutput.Value"/> lines high.</summary>
    SplitWindow,

    /// <summary>Text now goes to window <see cref="ZOutput.Value"/>: 0 lower, 1 upper.</summary>
    SetWindow,

    /// <summary>
    /// Clear window <see cref="ZOutput.Value"/>: 0 lower, 1 upper, -1 unsplit and clear the screen
    /// (lower window selected), -2 clear the screen without unsplitting.
    /// </summary>
    EraseWindow,

    /// <summary>Clear from the cursor to the end of its line.</summary>
    EraseLine,

    /// <summary>Move the cursor of the current window to <see cref="ZOutput.Row"/>, <see cref="ZOutput.Column"/> (1-based).</summary>
    SetCursor,

    /// <summary>Text style is now <see cref="ZOutput.Style"/>.</summary>
    SetStyle,

    /// <summary>Colours are now <see cref="ZOutput.Foreground"/> and <see cref="ZOutput.Background"/>.</summary>
    SetColor,

    /// <summary>Font is now <see cref="ZOutput.Value"/>: 1 normal, 4 fixed pitch.</summary>
    SetFont,

    /// <summary>Word wrapping in the lower window: <see cref="ZOutput.Value"/> 1 on, 0 off.</summary>
    BufferMode,

    /// <summary>A beep (<see cref="ZOutput.Value"/> 1 high, 2 low) or sound effect number <see cref="ZOutput.Value"/>.</summary>
    Sound,

    /// <summary>Text for the transcript (output stream 2).</summary>
    Transcript,

    /// <summary>A command for the command recording (output stream 4), without a line break.</summary>
    CommandRecord,

    /// <summary>Output stream <see cref="ZOutput.Value"/> was selected, or deselected when negative.</summary>
    OutputStream,

    /// <summary>Input now comes from stream <see cref="ZOutput.Value"/>: 0 keyboard, 1 a command file.</summary>
    InputStream,

    /// <summary>The story restarted. The host should reset its windows and styles.</summary>
    Restarted,

    /// <summary>
    /// The story did something the Standard leaves undefined, and the machine carried on:
    /// <see cref="ZOutput.Text"/> says what. Each instruction warns once. Meant for a log, not the
    /// player.
    /// </summary>
    Warning,
}

/// <summary>One item of output. Fields not used by <see cref="Kind"/> are zero.</summary>
public sealed class ZOutput
{
    internal ZOutput(ZOutputKind kind, int window)
    {
        Kind = kind;
        Window = window;
    }

    public ZOutputKind Kind { get; }

    /// <summary>The window selected when this was output.</summary>
    public int Window { get; }

    /// <summary>
    /// <see cref="ZMachine.InstructionCount"/> when this was output. Hosts that pace output like a
    /// slower machine use it to pause where the story did a lot of work.
    /// </summary>
    public long InstructionCount { get; internal set; }

    public string Text { get; internal set; }
    public int Value { get; internal set; }
    public int Row { get; internal set; }
    public int Column { get; internal set; }
    public ZTextStyle Style { get; internal set; }
    public ZColor Foreground { get; internal set; }
    public ZColor Background { get; internal set; }

    public override string ToString() => Kind switch
    {
        ZOutputKind.Text or ZOutputKind.InputEcho or ZOutputKind.Transcript or ZOutputKind.CommandRecord => $"{Kind} [{Window}] \"{Text}\"",
        ZOutputKind.SetCursor => $"{Kind} [{Window}] {Row},{Column}",
        ZOutputKind.SetStyle => $"{Kind} [{Window}] {Style}",
        ZOutputKind.SetColor => $"{Kind} [{Window}] {Foreground} on {Background}",
        _ => $"{Kind} [{Window}] {Value}",
    };
}

/// <summary>Text styles. Roman clears the others; the others combine.</summary>
[Flags]
public enum ZTextStyle
{
    Roman = 0,
    Reverse = 1,
    Bold = 2,
    Italic = 4,
    FixedPitch = 8,
}

/// <summary>
/// A Z-machine colour: the default colour, one of the standard colours 2 to 12, or a 15-bit true
/// colour (Standard 1.1).
/// </summary>
public readonly struct ZColor : IEquatable<ZColor>
{
    // Standard 1.1, section 8.3.7: the standard colours as 15-bit values (bits 0-4 red, 5-9 green, 10-14 blue).
    private static readonly int[] Standard = { 0x0000, 0x001D, 0x0340, 0x03BD, 0x59A0, 0x7C1F, 0x77A0, 0x7FFF, 0x5AD6, 0x4631, 0x2D6B };

    private ZColor(int index, int rgb15)
    {
        Index = index;
        Rgb15 = rgb15;
    }

    /// <summary>The host's default colour.</summary>
    public static ZColor Default => new(1, -1);

    /// <summary>2 black, 3 red, 4 green, 5 yellow, 6 blue, 7 magenta, 8 cyan, 9 white, 10 light grey, 11 medium grey, 12 dark grey.</summary>
    public static ZColor FromIndex(int index)
    {
        if (index == 1)
            return Default;
        if (index < 2 || index > 12)
            throw new ArgumentOutOfRangeException(nameof(index));
        return new ZColor(index, Standard[index - 2]);
    }

    /// <summary>A true colour, bits 0-4 red, 5-9 green, 10-14 blue. Standard colours are matched to their index.</summary>
    public static ZColor FromRgb15(int rgb15)
    {
        rgb15 &= 0x7FFF;
        int i = Array.IndexOf(Standard, rgb15);
        return new ZColor(i >= 0 ? i + 2 : 0, rgb15);
    }

    /// <summary>1 default, 2 to 12 a standard colour, 0 a true colour that isn't a standard one.</summary>
    public int Index { get; }

    /// <summary>The colour as 15-bit RGB, or -1 for the default.</summary>
    public int Rgb15 { get; }

    public bool IsDefault => Index == 1;

    /// <summary>The colour as 0xRRGGBB, or -1 for the default.</summary>
    public int ToRgb24()
    {
        if (IsDefault)
            return -1;
        static int Scale(int c) => c << 3 | c >> 2;
        return Scale(Rgb15 & 31) << 16 | Scale(Rgb15 >> 5 & 31) << 8 | Scale(Rgb15 >> 10 & 31);
    }

    public bool Equals(ZColor other) => Index == other.Index && Rgb15 == other.Rgb15;
    public override bool Equals(object obj) => obj is ZColor other && Equals(other);
    public override int GetHashCode() => Index * 65536 + Rgb15;
    public static bool operator ==(ZColor a, ZColor b) => a.Equals(b);
    public static bool operator !=(ZColor a, ZColor b) => !a.Equals(b);

    public override string ToString() => IsDefault ? "default" : Index > 0 ? $"colour {Index}" : $"#{ToRgb24():X6}";
}

/// <summary>Keys other than characters. The values are their ZSCII codes.</summary>
public enum ZKey
{
    Delete = 8,
    Enter = 13,
    Escape = 27,
    Up = 129, Down = 130, Left = 131, Right = 132,
    F1 = 133, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Keypad0 = 145, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
}

/// <summary>The line the story is waiting for.</summary>
public sealed class ZLineRequest
{
    internal ZLineRequest(int maxLength, string initialText, int timeoutTenths, ZKey[] terminators, bool anyFunctionKey)
    {
        MaxLength = maxLength;
        InitialText = initialText;
        TimeoutTenths = timeoutTenths;
        Terminators = terminators;
        AnyFunctionKeyTerminates = anyFunctionKey;
    }

    /// <summary>The most characters the story accepts. Longer lines are cut.</summary>
    public int MaxLength { get; }

    /// <summary>
    /// Text the story already printed as the start of the input. The host should let the player
    /// edit it, and submit the whole line including it.
    /// </summary>
    public string InitialText { get; }

    /// <summary>
    /// When nonzero, call <see cref="ZMachine.Interrupt"/> each time this many tenths of a second
    /// pass without the line being submitted.
    /// </summary>
    public int TimeoutTenths { get; }

    /// <summary>Keys besides Enter that end the line, passed to <see cref="ZMachine.SubmitLine"/>.</summary>
    public ZKey[] Terminators { get; }

    /// <summary>Every function key (cursor, F1-F12, keypad) ends the line.</summary>
    public bool AnyFunctionKeyTerminates { get; }

    public bool IsTerminator(ZKey key) => key == ZKey.Enter || Array.IndexOf(Terminators, key) >= 0
        || AnyFunctionKeyTerminates && (int)key >= 129 && (int)key <= 154;
}

/// <summary>The key the story is waiting for.</summary>
public sealed class ZKeyRequest
{
    internal ZKeyRequest(int timeoutTenths) => TimeoutTenths = timeoutTenths;

    /// <summary>When nonzero, call <see cref="ZMachine.Interrupt"/> each time this many tenths of a second pass without a key.</summary>
    public int TimeoutTenths { get; }
}

/// <summary>Data the story wants saved.</summary>
public sealed class ZSaveRequest
{
    internal ZSaveRequest(byte[] data, string name, bool auxiliary)
    {
        Data = data;
        SuggestedName = name;
        IsAuxiliary = auxiliary;
    }

    /// <summary>
    /// A Quetzal save file, or for an auxiliary save, the raw bytes of a table from the story's
    /// memory.
    /// </summary>
    public byte[] Data { get; }

    /// <summary>The file name the story suggested, or null. Auxiliary saves always name one.</summary>
    public string SuggestedName { get; }

    /// <summary>A table of story data rather than the game state (the save opcode with operands).</summary>
    public bool IsAuxiliary { get; }
}

/// <summary>Data the story wants loaded.</summary>
public sealed class ZRestoreRequest
{
    internal ZRestoreRequest(string name, bool auxiliary, int maxLength)
    {
        SuggestedName = name;
        IsAuxiliary = auxiliary;
        MaxLength = maxLength;
    }

    /// <summary>The file name the story suggested, or null.</summary>
    public string SuggestedName { get; }

    /// <summary>The story wants a table of its own data back, not a game state.</summary>
    public bool IsAuxiliary { get; }

    /// <summary>For an auxiliary restore, the most bytes the story reads.</summary>
    public int MaxLength { get; }
}

/// <summary>A story did something the Z-machine doesn't allow.</summary>
public sealed class ZMachineException : Exception
{
    public ZMachineException(string message) : base(message) { }
}

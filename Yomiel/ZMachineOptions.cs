using System;

namespace Yomiel;

/// <summary>What the host supports and tells the story through the header.</summary>
public sealed class ZMachineOptions
{
    /// <summary>Screen width in characters. Change it later with <see cref="ZMachine.SetScreenSize"/>.</summary>
    public int ScreenColumns { get; set; } = 80;

    /// <summary>Screen height in lines. 255 means no limit.</summary>
    public int ScreenRows { get; set; } = 25;

    public bool Colors { get; set; } = true;
    public bool Bold { get; set; } = true;
    public bool Italic { get; set; } = true;
    public bool FixedPitch { get; set; } = true;

    /// <summary>The host calls <see cref="ZMachine.Interrupt"/> for timed input.</summary>
    public bool TimedInput { get; set; } = true;

    /// <summary>Standard colour (2 to 9) used for the default foreground, reported in the header.</summary>
    public int DefaultForeground { get; set; } = 9;

    /// <summary>Standard colour (2 to 9) used for the default background, reported in the header.</summary>
    public int DefaultBackground { get; set; } = 2;

    /// <summary>Header interpreter number. 6 is IBM PC.</summary>
    public int InterpreterNumber { get; set; } = 6;

    /// <summary>Header interpreter version, by convention a capital letter.</summary>
    public char InterpreterVersion { get; set; } = 'Y';

    /// <summary>Seed for the random number generator. Null seeds it from the clock.</summary>
    public int? RandomSeed { get; set; }

    /// <summary>How many save_undo states are kept.</summary>
    public int UndoLevels { get; set; } = 16;

    /// <summary>Whether the host can draw a character, for check_unicode. Null means every printable character.</summary>
    public Func<char, bool> CanPrint { get; set; }

    internal ZMachineOptions Clone() => (ZMachineOptions)MemberwiseClone();
}

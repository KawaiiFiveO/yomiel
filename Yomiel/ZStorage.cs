using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Yomiel;

/// <summary>A suspended story: the machine (<see cref="ZMachine.SaveState"/>) and the screen (<see cref="ZScreen.SaveContents"/>).</summary>
public sealed class ZSuspended
{
    public ZSuspended(byte[] state, byte[] screen)
    {
        State = state ?? throw new ArgumentNullException(nameof(state));
        Screen = screen;
    }

    public byte[] State { get; }

    /// <summary>The screen contents, or null if they weren't kept.</summary>
    public byte[] Screen { get; }
}

/// <summary>
/// Where a <see cref="ZSession"/> keeps one story's files. Save names are already made safe with
/// <see cref="ZStoryFolder.SafeName"/>. Writes throw <see cref="IOException"/> or
/// <see cref="UnauthorizedAccessException"/> on failure; reads return null for a missing file.
/// </summary>
public interface IZStorage
{
    /// <summary>Saved games, most recent first.</summary>
    IList<string> SaveNames();
    byte[] ReadSave(string name);
    void WriteSave(string name, byte[] data);

    /// <summary>Auxiliary data: a table the story saves with the save opcode's operands.</summary>
    byte[] ReadData(string name);
    void WriteData(string name, byte[] data);

    /// <summary>Output stream 2, appended to.</summary>
    TextWriter OpenTranscript();

    /// <summary>Output stream 4, appended to.</summary>
    TextWriter OpenCommandRecord();

    /// <summary>Commands for input stream 1, or none.</summary>
    IList<string> ReadCommands();

    ZSuspended ReadSuspended();
    void WriteSuspended(ZSuspended suspended);
    void DeleteSuspended();
}

/// <summary>
/// <see cref="IZStorage"/> in a folder: saves as <c>name.qzl</c>, auxiliary data in <c>data\</c>,
/// <c>transcript.txt</c>, <c>commands.txt</c>, and <c>suspended.qzl</c> with <c>suspended.screen</c>.
/// The folder is created on the first write.
/// </summary>
public sealed class ZStoryFolder : IZStorage
{
    private const string SuspendedStateFile = "suspended.qzl";
    private const string SuspendedScreenFile = "suspended.screen";

    public ZStoryFolder(string folder) => Folder = folder ?? throw new ArgumentNullException(nameof(folder));

    public string Folder { get; }

    public bool HasSuspended => File.Exists(Path.Combine(Folder, SuspendedStateFile));

    /// <summary>A file name made only of letters, digits, spaces, dots, dashes and underscores, at most 60 characters.</summary>
    public static string SafeName(string name)
    {
        var chars = (name ?? "").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.').ToArray();
        string safe = new string(chars).Trim().Trim('.');
        return safe.Length == 0 ? "save" : safe.Length > 60 ? safe.Substring(0, 60) : safe;
    }

    public IList<string> SaveNames() => Directory.Exists(Folder)
        ? Directory.GetFiles(Folder, "*.qzl")
            .Where(f => !string.Equals(Path.GetFileName(f), SuspendedStateFile, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(Path.GetFileNameWithoutExtension).ToList()
        : new List<string>();

    public byte[] ReadSave(string name) => Read(Path.Combine(Folder, SafeName(name) + ".qzl"));
    public void WriteSave(string name, byte[] data) => Write(Path.Combine(Folder, SafeName(name) + ".qzl"), data);
    public byte[] ReadData(string name) => Read(Path.Combine(Folder, "data", SafeName(name)));
    public void WriteData(string name, byte[] data) => Write(Path.Combine(Folder, "data", SafeName(name)), data);

    public TextWriter OpenTranscript() => Append("transcript.txt");
    public TextWriter OpenCommandRecord() => Append("commands.txt");

    public IList<string> ReadCommands()
    {
        string path = Path.Combine(Folder, "commands.txt");
        return File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
    }

    public ZSuspended ReadSuspended()
    {
        byte[] state = Read(Path.Combine(Folder, SuspendedStateFile));
        return state == null ? null : new ZSuspended(state, Read(Path.Combine(Folder, SuspendedScreenFile)));
    }

    public void WriteSuspended(ZSuspended suspended)
    {
        Write(Path.Combine(Folder, SuspendedStateFile), suspended.State);
        string screen = Path.Combine(Folder, SuspendedScreenFile);
        if (suspended.Screen != null)
            Write(screen, suspended.Screen);
        else if (File.Exists(screen))
            File.Delete(screen);
    }

    public void DeleteSuspended()
    {
        if (!Directory.Exists(Folder))
            return;
        File.Delete(Path.Combine(Folder, SuspendedStateFile));
        File.Delete(Path.Combine(Folder, SuspendedScreenFile));
    }

    private TextWriter Append(string name)
    {
        Directory.CreateDirectory(Folder);
        return new StreamWriter(Path.Combine(Folder, name), true);
    }

    private static byte[] Read(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    private static void Write(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, data);
    }
}

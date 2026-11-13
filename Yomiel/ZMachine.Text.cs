using System;
using System.Collections.Generic;
using System.Text;

namespace Yomiel;

public sealed partial class ZMachine
{
    // Standard 3.8.5.3: ZSCII 155-223 when the story has no Unicode translation table.
    private const string DefaultExtraCharacters =
        "äöüÄÖÜß»«ëïÿËÏáéíóúýÁÉÍÓÚÝàèìòùÀÈÌÒÙâêîôûÂÊÎÔÛåÅøØãñõÃÑÕæÆçÇþðÞÐ£œŒ¡¿";

    private readonly byte[][] alphabets = new byte[3][];
    /// <summary>ZSCII to Z-characters for encoding: alphabet * 32 + Z-character (6-31), or -1.</summary>
    private readonly int[] zcharOf = new int[256];
    private char[] extraCharacters;
    private readonly Dictionary<char, int> zsciiOf = new();

    private void InitText()
    {
        int custom = ReadWord(0x34);
        for (int a = 0; a < 3; a++)
        {
            alphabets[a] = new byte[26];
            for (int i = 0; i < 26; i++)
                alphabets[a][i] = custom != 0 ? mem[custom + a * 26 + i] : (byte)"abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ \n0123456789.,!?_#'\"/\\-:()"[a * 26 + i];
        }
        // In A2, Z-character 6 is the ZSCII escape and 7 is a new line, whatever the table says.
        alphabets[2][0] = 0;
        alphabets[2][1] = 13;
        for (int i = 0; i < 256; i++)
            zcharOf[i] = -1;
        for (int a = 2; a >= 0; a--)
            for (int i = a == 2 ? 2 : 0; i < 26; i++)
                zcharOf[alphabets[a][i]] = a * 32 + i + 6;

        int ext = ReadWord(0x36);
        int table = ext != 0 && ReadWord(ext) >= 3 ? ReadWord(ext + 6) : 0;
        if (table != 0)
        {
            extraCharacters = new char[mem[table]];
            for (int i = 0; i < extraCharacters.Length; i++)
                extraCharacters[i] = (char)ReadWord(table + 1 + 2 * i);
        }
        else
        {
            extraCharacters = DefaultExtraCharacters.ToCharArray();
        }
        for (int i = 0; i < extraCharacters.Length && 155 + i <= 251; i++)
            zsciiOf[extraCharacters[i]] = 155 + i;
    }

    /// <summary>ZSCII to a character for output, or '\0' for nothing.</summary>
    private char ZsciiToChar(int z)
    {
        if (z >= 32 && z <= 126)
            return (char)z;
        if (z == 13 || z == 10)
            return '\n';
        if (z == 0)
            return '\0';
        if (z >= 155 && z - 155 < extraCharacters.Length)
            return extraCharacters[z - 155];
        return '?';
    }

    /// <summary>A character to ZSCII for input, or -1 if ZSCII has no code for it.</summary>
    private int CharToZscii(char c)
    {
        if (c >= 32 && c <= 126)
            return c;
        if (c == '\n' || c == '\r')
            return 13;
        return zsciiOf.TryGetValue(c, out int z) ? z : -1;
    }

    /// <summary>Prints the Z-encoded string at the address. Returns the address after it.</summary>
    private int PrintZString(int address, bool abbreviation = false)
    {
        int alphabet = 0, pendingAbbreviation = 0, escape = 0, high = 0;
        while (true)
        {
            int word = ReadWord(address);
            address += 2;
            for (int shift = 10; shift >= 0; shift -= 5)
            {
                int z = word >> shift & 31;
                if (pendingAbbreviation != 0)
                {
                    int entry = ReadWord(abbreviations + 2 * (32 * (pendingAbbreviation - 1) + z));
                    PrintZString(entry * 2, true);
                    pendingAbbreviation = 0;
                }
                else if (escape == 1)
                {
                    high = z;
                    escape = 2;
                }
                else if (escape == 2)
                {
                    PrintZscii(high << 5 | z);
                    escape = 0;
                }
                else if (z == 0)
                {
                    PrintZscii(32);
                    alphabet = 0;
                }
                else if (z <= 3)
                {
                    if (abbreviation)
                        throw new ZMachineException("abbreviation inside an abbreviation");
                    pendingAbbreviation = z;
                    alphabet = 0;
                }
                else if (z == 4 || z == 5)
                {
                    alphabet = z - 3;
                }
                else
                {
                    if (alphabet == 2 && z == 6)
                        escape = 1;
                    else
                        PrintZscii(alphabets[alphabet][z - 6]);
                    alphabet = 0;
                }
            }
            if ((word & 0x8000) != 0)
                return address;
        }
    }

    /// <summary>Encodes ZSCII text from memory as a dictionary word: 6 bytes, 9 Z-characters.</summary>
    private byte[] Encode(int address, int length)
    {
        var z = new int[9];
        int n = 0;
        for (int i = 0; i < length && n < 9; i++)
        {
            int c = mem[address + i];
            int code = zcharOf[c];
            if (code >= 0 && code < 32)
            {
                z[n++] = code;
            }
            else if (code >= 0)
            {
                z[n++] = 3 + code / 32; // shift 4 or 5
                if (n < 9) z[n++] = code % 32;
            }
            else
            {
                foreach (int part in new[] { 5, 6, c >> 5, c & 31 })
                    if (n < 9) z[n++] = part;
            }
        }
        while (n < 9)
            z[n++] = 5;
        var result = new byte[6];
        for (int w = 0; w < 3; w++)
        {
            int word = z[w * 3] << 10 | z[w * 3 + 1] << 5 | z[w * 3 + 2];
            if (w == 2)
                word |= 0x8000;
            result[w * 2] = (byte)(word >> 8);
            result[w * 2 + 1] = (byte)word;
        }
        return result;
    }

    private void EncodeText()
    {
        int text = ops[0], length = ops[1], from = ops[2], coded = ops[3];
        byte[] encoded = Encode(text + from, length);
        for (int i = 0; i < encoded.Length; i++)
            WriteByte(coded + i, encoded[i]);
    }

    // ---- Output ----

    private readonly Queue<ZOutput> output = new();
    private readonly StringBuilder text = new();
    private int textWindow;
    private readonly StringBuilder transcript = new();
    private bool transcriptOn;
    private bool screenStream;
    private bool recordStream;
    private readonly int[] memStreamTable = new int[16];
    private readonly int[] memStreamLength = new int[16];
    private int memStreamDepth;
    private long printed;

    private int window;
    private int upperHeight;
    private int upperRow;
    private int upperColumn;
    private int lowerColumn;
    private ZTextStyle style;
    private ZColor fg;
    private ZColor bg;
    private int font;

    /// <summary>Takes the next item of output. Output is queued until taken.</summary>
    public bool TryGetOutput(out ZOutput item)
    {
        if (output.Count > 0)
        {
            item = output.Dequeue();
            return true;
        }
        item = null;
        return false;
    }

    /// <summary>Items of output waiting to be taken.</summary>
    public int OutputCount => output.Count;

    private void ResetScreenState()
    {
        window = 0;
        upperHeight = 0;
        upperRow = 1;
        upperColumn = 1;
        lowerColumn = 1;
        style = ZTextStyle.Roman;
        fg = ZColor.Default;
        bg = ZColor.Default;
        font = 1;
        screenStream = true;
        recordStream = false;
        memStreamDepth = 0;
        transcriptOn = (mem[0x11] & 1) != 0;
    }

    private ZOutput Emit(ZOutputKind kind)
    {
        FlushText();
        var item = new ZOutput(kind, window) { InstructionCount = InstructionCount };
        output.Enqueue(item);
        return item;
    }

    private void FlushText()
    {
        if (text.Length > 0)
        {
            output.Enqueue(new ZOutput(ZOutputKind.Text, textWindow) { Text = text.ToString(), InstructionCount = InstructionCount });
            text.Clear();
        }
        if (transcript.Length > 0)
        {
            output.Enqueue(new ZOutput(ZOutputKind.Transcript, 0) { Text = transcript.ToString(), InstructionCount = InstructionCount });
            transcript.Clear();
        }
    }

    private void PrintZscii(int z)
    {
        if (z == 0)
            return;
        if (memStreamDepth > 0)
        {
            WriteMemStream(z);
            return;
        }
        char c = ZsciiToChar(z);
        if (c != '\0')
            PrintChar(c);
    }

    private void PrintUnicode(int c)
    {
        if (memStreamDepth > 0)
        {
            int z = CharToZscii((char)c);
            WriteMemStream(z >= 0 ? z : '?');
        }
        else if (c >= 32 || c == 13)
        {
            PrintChar(c == 13 ? '\n' : (char)c);
        }
    }

    private int CheckUnicode(int c)
    {
        int result = 0;
        if (c >= 32 && (c < 127 || c > 159) && (c < 0xD800 || c > 0xDFFF) && (options.CanPrint == null || options.CanPrint((char)c)))
            result |= 1;
        if (CharToZscii((char)c) >= 0)
            result |= 2;
        return result;
    }

    private void PrintChar(char c)
    {
        printed++;
        // The story can turn the transcript on and off through Flags 2 as well as output_stream.
        if (((mem[0x11] & 1) != 0) != transcriptOn)
        {
            transcriptOn = !transcriptOn;
            Emit(ZOutputKind.OutputStream).Value = transcriptOn ? 2 : -2;
        }
        if (screenStream)
        {
            if (text.Length == 0)
                textWindow = window;
            text.Append(c);
            AdvanceCursor(c);
        }
        if (transcriptOn && window == 0)
            transcript.Append(c);
    }

    private void AdvanceCursor(char c)
    {
        if (window == 1)
        {
            if (c == '\n')
            {
                upperRow++;
                upperColumn = 1;
            }
            else if (upperColumn <= options.ScreenColumns)
            {
                upperColumn++;
            }
        }
        else if (c == '\n' || ++lowerColumn > options.ScreenColumns)
        {
            lowerColumn = 1;
        }
    }

    private void WriteMemStream(int z)
    {
        int i = memStreamDepth - 1;
        WriteByte(memStreamTable[i] + 2 + memStreamLength[i], z);
        memStreamLength[i]++;
    }

    private void OutputStream(int stream, int table)
    {
        switch (stream)
        {
            case 1:
            case -1:
                screenStream = stream > 0;
                break;
            case 2:
            case -2:
                if (stream > 0) mem[0x11] |= 1; else mem[0x11] &= unchecked((byte)~1);
                if (transcriptOn != stream > 0)
                {
                    transcriptOn = stream > 0;
                    Emit(ZOutputKind.OutputStream).Value = stream;
                }
                break;
            case 3:
                if (memStreamDepth == 16)
                    throw new ZMachineException("more than 16 memory streams open");
                memStreamTable[memStreamDepth] = table;
                memStreamLength[memStreamDepth] = 0;
                memStreamDepth++;
                break;
            case -3:
                if (memStreamDepth > 0)
                {
                    memStreamDepth--;
                    WriteWord(memStreamTable[memStreamDepth], memStreamLength[memStreamDepth]);
                }
                break;
            case 4:
            case -4:
                if (recordStream != stream > 0)
                {
                    recordStream = stream > 0;
                    Emit(ZOutputKind.OutputStream).Value = stream;
                }
                break;
            case 0:
                break;
            default:
                throw new ZMachineException($"output stream {stream} doesn't exist");
        }
    }

    // ---- Windows and styles ----

    private void SplitWindow(int lines)
    {
        upperHeight = lines;
        if (upperRow > lines)
        {
            upperRow = 1;
            upperColumn = 1;
        }
        Emit(ZOutputKind.SplitWindow).Value = lines;
    }

    private void SelectWindow(int w)
    {
        if (w != 0 && w != 1)
            throw new ZMachineException($"window {w} doesn't exist");
        FlushText();
        window = w;
        if (w == 1)
        {
            upperRow = 1;
            upperColumn = 1;
        }
        Emit(ZOutputKind.SetWindow).Value = w;
    }

    private void EraseWindow(int w)
    {
        Emit(ZOutputKind.EraseWindow).Value = w;
        if (w == -1)
        {
            upperHeight = 0;
            window = 0;
        }
        if (w == -1 || w == -2 || w == 1)
        {
            upperRow = 1;
            upperColumn = 1;
        }
        if (w == -1 || w == -2 || w == 0)
            lowerColumn = 1;
    }

    private void EraseLine(int value)
    {
        if (value == 1)
            Emit(ZOutputKind.EraseLine).Value = 1;
    }

    private void SetCursor(int row, int column)
    {
        if (window == 1)
        {
            upperRow = row;
            upperColumn = column;
        }
        ZOutput item = Emit(ZOutputKind.SetCursor);
        item.Row = row;
        item.Column = column;
    }

    private void GetCursor(int array)
    {
        WriteWord(array, window == 1 ? upperRow : Math.Min(options.ScreenRows, 255));
        WriteWord(array + 2, window == 1 ? upperColumn : lowerColumn);
    }

    private void SetTextStyle(int value)
    {
        style = value == 0 ? ZTextStyle.Roman : style | (ZTextStyle)(value & 15);
        Emit(ZOutputKind.SetStyle).Style = style;
    }

    private void SetFont(int f)
    {
        if (f == 0)
        {
            Store(font);
        }
        else if (f == 1 || f == 4)
        {
            int previous = font;
            font = f;
            Emit(ZOutputKind.SetFont).Value = f;
            Store(previous);
        }
        else
        {
            Store(0);
        }
    }

    private static ZColor ResolveColor(int c, ZColor current) =>
        c == 1 ? ZColor.Default : c >= 2 && c <= 12 ? ZColor.FromIndex(c) : current;

    private void SetColor(int foreground, int background)
    {
        fg = ResolveColor((short)foreground, fg);
        bg = ResolveColor((short)background, bg);
        EmitColor();
    }

    private void SetTrueColor(int foreground, int background)
    {
        static ZColor Resolve(int c, ZColor current) => c == -1 ? ZColor.Default : c >= 0 ? ZColor.FromRgb15(c) : current;
        fg = Resolve(foreground, fg);
        bg = Resolve(background, bg);
        EmitColor();
    }

    private void EmitColor()
    {
        ZOutput item = Emit(ZOutputKind.SetColor);
        item.Foreground = fg;
        item.Background = bg;
    }

    private void PrintTable()
    {
        int address = ops[0], width = ops[1];
        int height = opCount > 2 ? ops[2] : 1;
        int skip = opCount > 3 ? ops[3] : 0;
        int row = upperRow, column = upperColumn;
        for (int r = 0; r < height; r++)
        {
            if (r > 0)
            {
                if (window == 1 && memStreamDepth == 0)
                    SetCursor(row + r, column);
                else
                    PrintZscii(13);
            }
            for (int c = 0; c < width; c++)
                PrintZscii(mem[address++]);
            address += skip;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Yomiel;

public sealed partial class ZMachine
{
    private sealed class PendingRead
    {
        public bool IsLine;
        public int Text;
        public int Parse;
        public int StoreVar;
        public int Time;
        public int Routine;
        public bool InInterrupt;
        public string Partial;
        public long PrintedMark;
    }

    private PendingRead pending;

    /// <summary>The line the machine is waiting for, while <see cref="Status"/> is <see cref="ZStatus.WaitingForLine"/>.</summary>
    public ZLineRequest LineRequest { get; private set; }

    /// <summary>The key the machine is waiting for, while <see cref="Status"/> is <see cref="ZStatus.WaitingForKey"/>.</summary>
    public ZKeyRequest KeyRequest { get; private set; }

    /// <summary>
    /// The last timed-input routine printed something and left input going. The host should show
    /// the line typed so far again, below the new text.
    /// </summary>
    public bool InterruptPrinted { get; private set; }

    private void ReadLine()
    {
        CheckNotInInterrupt();
        int textBuffer = ops[0];
        pending = new PendingRead
        {
            IsLine = true,
            Text = textBuffer,
            Parse = opCount > 1 ? ops[1] : 0,
            Time = opCount > 2 ? ops[2] : 0,
            Routine = opCount > 3 ? ops[3] : 0,
            StoreVar = mem[pc++],
        };
        StartLineRequest();
    }

    private void StartLineRequest()
    {
        int textBuffer = pending.Text;
        int max = mem[textBuffer];
        int existing = mem[textBuffer + 1];
        var initial = new StringBuilder();
        if (existing <= max)
            for (int i = 0; i < existing; i++)
                initial.Append(ZsciiToChar(mem[textBuffer + 2 + i]));

        var terminators = new List<ZKey>();
        bool anyFunctionKey = false;
        int table = ReadWord(0x2E);
        for (int a = table; table != 0 && mem[a] != 0; a++)
        {
            int z = mem[a];
            if (z == 255)
                anyFunctionKey = true;
            else if (z >= 129 && z <= 154)
                terminators.Add((ZKey)z);
        }
        LineRequest = new ZLineRequest(max, initial.ToString(), Timed ? pending.Time : 0, terminators.ToArray(), anyFunctionKey);
        status = ZStatus.WaitingForLine;
    }

    private bool Timed => options.TimedInput && pending.Time > 0 && pending.Routine != 0;

    private void ReadKey()
    {
        CheckNotInInterrupt();
        pending = new PendingRead
        {
            Time = opCount > 1 ? ops[1] : 0,
            Routine = opCount > 2 ? ops[2] : 0,
            StoreVar = mem[pc++],
        };
        KeyRequest = new ZKeyRequest(Timed ? pending.Time : 0);
        status = ZStatus.WaitingForKey;
    }

    /// <summary>
    /// Ends line input. <paramref name="line"/> is the whole line, including
    /// <see cref="ZLineRequest.InitialText"/>; text past <see cref="ZLineRequest.MaxLength"/> is cut.
    /// <paramref name="terminator"/> is Enter or one of <see cref="ZLineRequest.Terminators"/>.
    /// </summary>
    public void SubmitLine(string line, ZKey terminator = ZKey.Enter)
    {
        if (status != ZStatus.WaitingForLine)
            throw new InvalidOperationException($"the machine isn't waiting for a line ({status})");
        if (!LineRequest.IsTerminator(terminator))
            throw new ArgumentException($"{terminator} doesn't end input here", nameof(terminator));
        FinishLine(line ?? "", (int)terminator);
    }

    private void FinishLine(string line, int terminator)
    {
        int textBuffer = pending.Text;
        int max = mem[textBuffer];
        if (line.Length > max)
            line = line.Substring(0, max);
        for (int i = 0; i < line.Length; i++)
        {
            int z = CharToZscii(char.ToLowerInvariant(line[i]));
            if (z < 32 || z > 126 && z < 155 || z > 251)
                z = '?';
            WriteByte(textBuffer + 2 + i, z);
        }
        WriteByte(textBuffer + 1, line.Length);
        if (pending.Parse != 0)
            Tokenise(textBuffer, pending.Parse, 0, false);

        // The story printed the initial text itself, so it isn't echoed again.
        string initial = LineRequest.InitialText;
        string echo = line.StartsWith(initial, StringComparison.Ordinal) ? line.Substring(initial.Length) : line;
        if (terminator == 13)
            echo += "\n";
        Emit(ZOutputKind.InputEcho).Text = echo;
        if (terminator == 13)
            lowerColumn = 1;
        if (transcriptOn && window == 0)
            transcript.Append(echo);
        if (recordStream)
            Emit(ZOutputKind.CommandRecord).Text = line;

        int storeVar = pending.StoreVar;
        pending = null;
        LineRequest = null;
        status = ZStatus.Running;
        WriteVar(storeVar, terminator);
    }

    /// <summary>Ends key input with a character.</summary>
    public void SubmitKey(char c)
    {
        int z = c == '\b' ? 8 : c == '\x1B' ? 27 : CharToZscii(c);
        CheckWaitingForKey();
        FinishKey(z < 0 ? '?' : z);
    }

    /// <summary>Ends key input with a key other than a character.</summary>
    public void SubmitKey(ZKey key)
    {
        CheckWaitingForKey();
        FinishKey((int)key);
    }

    private void CheckWaitingForKey()
    {
        if (status != ZStatus.WaitingForKey)
            throw new InvalidOperationException($"the machine isn't waiting for a key ({status})");
    }

    // Also called when a timed-input routine ends the input, while the machine is running.
    private void FinishKey(int z)
    {
        int storeVar = pending.StoreVar;
        pending = null;
        KeyRequest = null;
        status = ZStatus.Running;
        WriteVar(storeVar, z);
    }

    /// <summary>
    /// The timeout in <see cref="ZLineRequest.TimeoutTenths"/> or <see cref="ZKeyRequest.TimeoutTenths"/>
    /// passed. Runs the story's timed-input routine on the next <see cref="Run"/>: the machine then
    /// either ends input or waits again, with <see cref="InterruptPrinted"/> set if it printed.
    /// </summary>
    /// <param name="partialLine">For line input, what the player has typed so far.</param>
    public void Interrupt(string partialLine = null)
    {
        if (status != ZStatus.WaitingForLine && status != ZStatus.WaitingForKey || !Timed)
            throw new InvalidOperationException("the machine isn't waiting for timed input");
        pending.Partial = partialLine ?? "";
        pending.InInterrupt = true;
        pending.PrintedMark = printed;
        InterruptPrinted = false;
        status = ZStatus.Running;
        Call(pending.Routine, 0, -1, interrupt: true);
    }

    private void InterruptReturned(int value)
    {
        pending.InInterrupt = false;
        bool didPrint = printed != pending.PrintedMark;
        if (value == 0)
        {
            status = pending.IsLine ? ZStatus.WaitingForLine : ZStatus.WaitingForKey;
            InterruptPrinted = didPrint;
        }
        else if (pending.IsLine)
        {
            FinishLine(pending.Partial, 0);
        }
        else
        {
            FinishKey(0);
        }
    }

    // Before the routine runs, the read instruction has finished decoding, so a read inside the
    // routine would replace it.
    private void CheckNotInInterrupt()
    {
        if (pending != null && pending.InInterrupt)
            throw new ZMachineException("input inside a timed-input routine");
    }

    private void Tokenise(int textBuffer, int parseBuffer, int dict, bool skipUnknown)
    {
        if (dict == 0)
            dict = dictionary;
        int separatorCount = mem[dict];
        int length = mem[textBuffer + 1];
        int start = textBuffer + 2;
        int maxWords = mem[parseBuffer];
        int count = 0;

        bool IsSeparator(int c)
        {
            for (int s = 0; s < separatorCount; s++)
                if (mem[dict + 1 + s] == c)
                    return true;
            return false;
        }

        int i = 0;
        while (i < length && count < maxWords)
        {
            int c = mem[start + i];
            if (c == ' ')
            {
                i++;
                continue;
            }
            int wordLength = 1;
            if (!IsSeparator(c))
                while (i + wordLength < length && mem[start + i + wordLength] != ' ' && !IsSeparator(mem[start + i + wordLength]))
                    wordLength++;
            int entry = LookUp(dict, start + i, wordLength);
            int slot = parseBuffer + 2 + 4 * count;
            if (entry != 0 || !skipUnknown)
            {
                WriteWord(slot, entry);
                WriteByte(slot + 2, wordLength);
                WriteByte(slot + 3, i + 2);
            }
            count++;
            i += wordLength;
        }
        WriteByte(parseBuffer + 1, count);
    }

    private int LookUp(int dict, int address, int length)
    {
        byte[] key = Encode(address, length);
        int separatorCount = mem[dict];
        int entryLength = mem[dict + 1 + separatorCount];
        int count = (short)ReadWord(dict + 2 + separatorCount);
        int entries = dict + 4 + separatorCount;

        int Compare(int entry)
        {
            for (int b = 0; b < 6; b++)
            {
                int d = key[b] - mem[entry + b];
                if (d != 0)
                    return d;
            }
            return 0;
        }

        if (count > 0)
        {
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int entry = entries + middle * entryLength;
                int d = Compare(entry);
                if (d == 0)
                    return entry;
                if (d < 0)
                    high = middle - 1;
                else
                    low = middle + 1;
            }
            return 0;
        }
        for (int n = 0; n < -count; n++)
        {
            int entry = entries + n * entryLength;
            if (Compare(entry) == 0)
                return entry;
        }
        return 0;
    }

    // ---- Suspend data: Quetzal IntD contents for SaveState ----

    private const byte SuspendFormat = 1;

    private byte[] WriteSuspendData()
    {
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);
        w.Write(SuspendFormat);
        w.Write((byte)(status == ZStatus.WaitingForLine ? 1 : status == ZStatus.WaitingForKey ? 2 : 0));
        if (pending != null)
        {
            w.Write((ushort)pending.Text);
            w.Write((ushort)pending.Parse);
            w.Write((byte)pending.StoreVar);
            w.Write((ushort)pending.Time);
            w.Write((ushort)pending.Routine);
        }
        w.Write((byte)window);
        w.Write((ushort)upperHeight);
        w.Write((short)upperRow);
        w.Write((short)upperColumn);
        w.Write((short)lowerColumn);
        w.Write((byte)style);
        w.Write((byte)font);
        WriteColor(w, fg);
        WriteColor(w, bg);
        w.Write(screenStream);
        w.Write(recordStream);
        w.Write(rng);
        w.Write(sequenceLength);
        w.Write(sequenceNext);
        return stream.ToArray();
    }

    private static void WriteColor(BinaryWriter w, ZColor c) => w.Write((short)(c.IsDefault ? -1 : c.Rgb15));

    private static ZColor ReadColor(BinaryReader r)
    {
        short v = r.ReadInt16();
        return v < 0 ? ZColor.Default : ZColor.FromRgb15(v);
    }

    private void ReadSuspendData(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data));
        if (r.ReadByte() != SuspendFormat)
            throw new ArgumentException("the saved state is from a different version of Yomiel");
        int waiting = r.ReadByte();
        if (waiting != 0)
        {
            pending = new PendingRead
            {
                IsLine = waiting == 1,
                Text = r.ReadUInt16(),
                Parse = r.ReadUInt16(),
                StoreVar = r.ReadByte(),
                Time = r.ReadUInt16(),
                Routine = r.ReadUInt16(),
            };
        }
        window = r.ReadByte();
        upperHeight = r.ReadUInt16();
        upperRow = r.ReadInt16();
        upperColumn = r.ReadInt16();
        lowerColumn = r.ReadInt16();
        style = (ZTextStyle)r.ReadByte();
        font = r.ReadByte();
        fg = ReadColor(r);
        bg = ReadColor(r);
        screenStream = r.ReadBoolean();
        recordStream = r.ReadBoolean();
        rng = r.ReadUInt32();
        sequenceLength = r.ReadInt32();
        sequenceNext = r.ReadInt32();
        transcriptOn = (mem[0x11] & 1) != 0;

        // Tell the host the screen state it needs to rebuild.
        int current = window;
        window = 0;
        Emit(ZOutputKind.SplitWindow).Value = upperHeight;
        Emit(ZOutputKind.SetWindow).Value = current;
        window = current;
        Emit(ZOutputKind.SetStyle).Style = style;
        EmitColor();
        Emit(ZOutputKind.SetFont).Value = font;
        if (current == 1)
        {
            ZOutput cursor = Emit(ZOutputKind.SetCursor);
            cursor.Row = upperRow;
            cursor.Column = upperColumn;
        }
        if (recordStream)
            Emit(ZOutputKind.OutputStream).Value = 4;

        if (pending == null)
            return;
        if (pending.IsLine)
        {
            StartLineRequest();
        }
        else
        {
            KeyRequest = new ZKeyRequest(Timed ? pending.Time : 0);
            status = ZStatus.WaitingForKey;
        }
    }
}

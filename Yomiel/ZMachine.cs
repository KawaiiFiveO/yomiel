using System;
using System.Collections.Generic;
using System.Text;

namespace Yomiel;

/// <summary>
/// A Z-machine running one story file. Everything runs on the caller's thread: call
/// <see cref="Run"/> from the host's loop, read output with <see cref="TryGetOutput"/>, and answer
/// whatever the machine is waiting for (<see cref="Status"/>).
/// </summary>
public sealed partial class ZMachine
{
    private const int StackSize = 65535;
    private const int MaxFrames = 32768;

    private readonly byte[] story;
    private readonly byte[] mem;
    private readonly ZMachineOptions options;
    private readonly int dynamicSize;
    private readonly int globals;
    private readonly int objects;
    private readonly int abbreviations;
    private readonly int dictionary;
    private readonly int routineOffset;
    private readonly int stringOffset;

    private int pc;
    private int instructionStart;
    private readonly ushort[] stack = new ushort[StackSize];
    private int sp;
    private Frame[] frames = new Frame[64];
    private int fp;
    private readonly int[] ops = new int[8];
    private int opCount;
    private ZStatus status;

    private readonly List<MachineState> undo = new();
    private int pendingStore;

    /// <summary>Loads a story file. Supports versions 5, 7 and 8.</summary>
    /// <exception cref="ArgumentException">The file isn't a story this machine can run.</exception>
    public ZMachine(byte[] storyFile, ZMachineOptions options = null)
    {
        if (storyFile == null)
            throw new ArgumentNullException(nameof(storyFile));
        if (storyFile.Length < 64)
            throw new ArgumentException("the file is too short to be a story file");
        Version = storyFile[0];
        if (Version != 5 && Version != 7 && Version != 8)
            throw new ArgumentException($"version {Version} story files aren't supported (5, 7 and 8 are)");

        this.options = (options ?? new ZMachineOptions()).Clone();
        story = (byte[])storyFile.Clone();
        mem = (byte[])storyFile.Clone();
        dynamicSize = ReadWord(0x0E);
        if (dynamicSize < 64 || dynamicSize > story.Length)
            throw new ArgumentException("the header's static memory address is outside the file");
        globals = ReadWord(0x0C);
        objects = ReadWord(0x0A);
        abbreviations = ReadWord(0x18);
        dictionary = ReadWord(0x08);
        routineOffset = ReadWord(0x28);
        stringOffset = ReadWord(0x2A);

        InitText();
        SeedRandom(this.options.RandomSeed ?? Environment.TickCount);
        Reset();
    }

    /// <summary>The story file's version: 5, 7 or 8.</summary>
    public int Version { get; }

    /// <summary>The release number from the header.</summary>
    public int Release => story[2] << 8 | story[3];

    /// <summary>The serial code from the header, usually the compile date as YYMMDD.</summary>
    public string Serial => Encoding.ASCII.GetString(story, 0x12, 6);

    /// <summary>The checksum from the header.</summary>
    public int Checksum => story[0x1C] << 8 | story[0x1D];

    public ZStatus Status => status;

    /// <summary>Why the machine stopped, when <see cref="Status"/> is <see cref="ZStatus.Error"/>.</summary>
    public string ErrorMessage { get; private set; }

    /// <summary>Why the last <see cref="CompleteRestore"/> failed, or null.</summary>
    public string RestoreError { get; private set; }

    /// <summary>Instructions executed since the story loaded.</summary>
    public long InstructionCount { get; private set; }

    /// <summary>
    /// Runs until the machine waits, stops, or has executed <paramref name="maxInstructions"/>
    /// instructions. Output is queued for <see cref="TryGetOutput"/>.
    /// </summary>
    public ZStatus Run(int maxInstructions = 100_000)
    {
        if (status != ZStatus.Running)
            return status;
        try
        {
            for (int n = 0; n < maxInstructions && status == ZStatus.Running; n++)
            {
                Step();
                InstructionCount++;
            }
        }
        catch (ZMachineException e)
        {
            Fail(e.Message);
        }
        catch (IndexOutOfRangeException)
        {
            Fail("memory access outside the story file");
        }
        FlushText();
        return status;
    }

    private void Fail(string message)
    {
        status = ZStatus.Error;
        ErrorMessage = $"{message} (instruction at ${instructionStart:X5})";
    }

    /// <summary>
    /// Changes the screen size the story sees. Stories read it at any time, so the next status
    /// line or box uses it.
    /// </summary>
    public void SetScreenSize(int columns, int rows)
    {
        options.ScreenColumns = columns;
        options.ScreenRows = rows;
        WriteScreenSize();
    }

    /// <summary>
    /// The upper window's height in lines, the window text goes to, and the current style,
    /// colours and font, for hosts that rebuild their display.
    /// </summary>
    public int UpperWindowHeight => upperHeight;
    public int CurrentWindow => window;
    public ZTextStyle CurrentStyle => style;
    public ZColor CurrentForeground => fg;
    public ZColor CurrentBackground => bg;
    public int CurrentFont => font;

    /// <summary>Flags 2 bit 1: the story asks for fixed-pitch text everywhere.</summary>
    public bool ForceFixedPitch => (mem[0x11] & 2) != 0;

    // ---- Start, restart ----

    private void Reset()
    {
        InitHeader();
        sp = 0;
        fp = 0;
        frames[0] = new Frame { StoreVar = -1 };
        pc = ReadWord(6);
        status = ZStatus.Running;
        ResetScreenState();
    }

    private void Restart()
    {
        int keep = mem[0x11] & 3;
        Buffer.BlockCopy(story, 0, mem, 0, dynamicSize);
        mem[0x11] = (byte)(mem[0x11] & ~3 | keep);
        pending = null;
        LineRequest = null;
        KeyRequest = null;
        Reset();
        Emit(ZOutputKind.Restarted);
    }

    private void InitHeader()
    {
        int flags1 = mem[1] & ~(1 | 2 | 4 | 8 | 16 | 32 | 128);
        if (options.Colors) flags1 |= 1;
        if (options.Bold) flags1 |= 4;
        if (options.Italic) flags1 |= 8;
        if (options.FixedPitch) flags1 |= 16;
        if (options.TimedInput) flags1 |= 128;
        mem[1] = (byte)flags1;

        // Flags 2: no pictures (bit 3), mouse (5), sound (7) or menus (8). Undo (4) and colours (6) stay as the story asked.
        mem[0x11] &= unchecked((byte)~(8 | 32 | 128));
        mem[0x10] &= unchecked((byte)~1);

        mem[0x1E] = (byte)options.InterpreterNumber;
        mem[0x1F] = (byte)options.InterpreterVersion;
        WriteScreenSize();
        mem[0x2C] = (byte)options.DefaultBackground;
        mem[0x2D] = (byte)options.DefaultForeground;
        mem[0x32] = 1;
        mem[0x33] = 1;

        int ext = ReadWord(0x36);
        if (ext != 0 && ext + 13 < dynamicSize && ReadWord(ext) >= 6)
        {
            SetWord(ext + 10, ZColor.FromIndex(options.DefaultForeground).Rgb15);
            SetWord(ext + 12, ZColor.FromIndex(options.DefaultBackground).Rgb15);
        }
    }

    private void WriteScreenSize()
    {
        int rows = Math.Max(1, Math.Min(options.ScreenRows, 255));
        int columns = Math.Max(1, Math.Min(options.ScreenColumns, 255));
        mem[0x20] = (byte)rows;
        mem[0x21] = (byte)columns;
        SetWord(0x22, columns);
        SetWord(0x24, rows);
        mem[0x26] = 1;
        mem[0x27] = 1;
    }

    private void SetWord(int address, int value)
    {
        mem[address] = (byte)(value >> 8);
        mem[address + 1] = (byte)value;
    }

    // ---- Memory ----

    private int ReadWord(int address) => mem[address] << 8 | mem[address + 1];

    private void WriteByte(int address, int value)
    {
        if ((uint)address >= (uint)dynamicSize)
            throw new ZMachineException($"write to static memory at ${address:X4}");
        mem[address] = (byte)value;
    }

    private void WriteWord(int address, int value)
    {
        WriteByte(address, value >> 8);
        WriteByte(address + 1, value);
    }

    private int UnpackRoutine(int packed) => Version switch
    {
        7 => packed * 4 + routineOffset * 8,
        8 => packed * 8,
        _ => packed * 4,
    };

    private int UnpackString(int packed) => Version switch
    {
        7 => packed * 4 + stringOffset * 8,
        8 => packed * 8,
        _ => packed * 4,
    };

    // ---- Variables and stack ----

    private void Push(int value)
    {
        if (sp >= StackSize)
            throw new ZMachineException("stack overflow");
        stack[sp++] = (ushort)value;
    }

    private int Pop()
    {
        if (sp <= frames[fp].EvalBase)
            throw new ZMachineException("stack underflow");
        return stack[--sp];
    }

    private int ReadVar(int v)
    {
        if (v == 0)
            return Pop();
        if (v < 16)
        {
            ref Frame f = ref frames[fp];
            if (v > f.LocalCount)
                throw new ZMachineException($"read of local {v}, but the routine has {f.LocalCount}");
            return stack[f.LocalBase + v - 1];
        }
        int a = globals + 2 * (v - 16);
        return mem[a] << 8 | mem[a + 1];
    }

    private void WriteVar(int v, int value)
    {
        if (v == 0)
        {
            Push(value);
        }
        else if (v < 16)
        {
            ref Frame f = ref frames[fp];
            if (v > f.LocalCount)
                throw new ZMachineException($"write to local {v}, but the routine has {f.LocalCount}");
            stack[f.LocalBase + v - 1] = (ushort)value;
        }
        else
        {
            WriteWord(globals + 2 * (v - 16), value);
        }
    }

    /// <summary>Reads a variable named by an operand: variable 0 is the top of the stack, read in place.</summary>
    private int PeekVar(int v)
    {
        v &= 0xFF;
        if (v != 0)
            return ReadVar(v);
        if (sp <= frames[fp].EvalBase)
            throw new ZMachineException("stack underflow");
        return stack[sp - 1];
    }

    /// <summary>Writes a variable named by an operand: variable 0 is the top of the stack, written in place.</summary>
    private void PokeVar(int v, int value)
    {
        v &= 0xFF;
        if (v != 0)
        {
            WriteVar(v, value & 0xFFFF);
            return;
        }
        if (sp <= frames[fp].EvalBase)
            throw new ZMachineException("stack underflow");
        stack[sp - 1] = (ushort)value;
    }

    private void Store(int value) => WriteVar(mem[pc++], value & 0xFFFF);

    private void Branch(bool condition)
    {
        int b = mem[pc++];
        int offset;
        if ((b & 0x40) != 0)
        {
            offset = b & 0x3F;
        }
        else
        {
            offset = (b & 0x3F) << 8 | mem[pc++];
            if ((offset & 0x2000) != 0)
                offset -= 0x4000;
        }
        if (((b & 0x80) != 0) != condition)
            return;
        if (offset == 0)
            Return(0);
        else if (offset == 1)
            Return(1);
        else
            pc += offset - 2;
    }

    // ---- Routines ----

    /// <summary>Calls a routine with ops[1..argCount] as arguments. storeVar -1 discards the result.</summary>
    private void Call(int packed, int argCount, int storeVar, bool interrupt = false)
    {
        if (packed == 0)
        {
            if (storeVar >= 0)
                WriteVar(storeVar, 0);
            return;
        }
        int address = UnpackRoutine(packed);
        int locals = mem[address++];
        if (locals > 15)
            throw new ZMachineException($"routine at ${address - 1:X5} has {locals} locals");
        if (fp + 1 == frames.Length)
        {
            if (frames.Length >= MaxFrames)
                throw new ZMachineException("call stack overflow");
            Array.Resize(ref frames, frames.Length * 2);
        }
        if (sp + locals > StackSize)
            throw new ZMachineException("stack overflow");
        ref Frame f = ref frames[++fp];
        f.ReturnPc = pc;
        f.StoreVar = storeVar;
        f.LocalBase = sp;
        f.LocalCount = locals;
        f.ArgCount = argCount;
        f.Interrupt = interrupt;
        for (int i = 0; i < locals; i++)
            stack[sp++] = (ushort)(i < argCount ? ops[i + 1] : 0);
        pc = address;
    }

    private void Return(int value)
    {
        if (fp == 0)
            throw new ZMachineException("return from the main routine");
        Frame f = frames[fp--];
        sp = f.LocalBase;
        pc = f.ReturnPc;
        if (f.Interrupt)
            InterruptReturned(value);
        else if (f.StoreVar >= 0)
            WriteVar(f.StoreVar, value & 0xFFFF);
    }

    private void Throw(int value, int frame)
    {
        if (frame < 1 || frame > fp)
            throw new ZMachineException($"throw to frame {frame}, which isn't on the stack");
        fp = frame;
        Return(value);
    }

    // ---- Random numbers ----

    private uint rng;
    private int sequenceLength;
    private int sequenceNext;

    private void SeedRandom(int seed)
    {
        rng = (uint)seed * 2654435761u | 1;
        sequenceLength = 0;
    }

    private int Random(int range)
    {
        if (range > 0)
        {
            if (sequenceLength > 0)
            {
                int v = sequenceNext;
                sequenceNext = sequenceNext % sequenceLength + 1;
                return (v - 1) % range + 1;
            }
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            return (int)(rng % (uint)range) + 1;
        }
        if (range == 0)
        {
            SeedRandom(Environment.TickCount ^ (int)DateTime.UtcNow.Ticks);
        }
        else if (-range < 1000)
        {
            // Standard 2.4.3: small seeds give the predictable sequence 1, 2, ..., n.
            sequenceLength = -range;
            sequenceNext = 1;
        }
        else
        {
            SeedRandom(-range);
        }
        return 0;
    }

    private bool Verify()
    {
        int scale = Version <= 3 ? 2 : Version <= 5 ? 4 : 8;
        int length = (story[0x1A] << 8 | story[0x1B]) * scale;
        if (length <= 0x40 || length > story.Length)
            length = story.Length;
        int sum = 0;
        for (int i = 0x40; i < length; i++)
            sum += story[i];
        return (sum & 0xFFFF) == (story[0x1C] << 8 | story[0x1D]);
    }

    // ---- Saved states ----

    private bool CanCapture => pending == null || !pending.InInterrupt;

    private MachineState Capture()
    {
        var state = new MachineState
        {
            Dynamic = new byte[dynamicSize],
            Stack = new ushort[sp],
            Frames = new Frame[fp + 1],
            Pc = pc,
        };
        Buffer.BlockCopy(mem, 0, state.Dynamic, 0, dynamicSize);
        Array.Copy(stack, state.Stack, sp);
        Array.Copy(frames, state.Frames, fp + 1);
        return state;
    }

    /// <summary>Loads a state, keeping the transcript and fixed-pitch bits and this host's header settings.</summary>
    private void Apply(MachineState state)
    {
        int keep = mem[0x11] & 3;
        Buffer.BlockCopy(state.Dynamic, 0, mem, 0, dynamicSize);
        mem[0x11] = (byte)(mem[0x11] & ~3 | keep);
        Array.Copy(state.Stack, stack, state.Stack.Length);
        sp = state.Stack.Length;
        if (frames.Length < state.Frames.Length)
            Array.Resize(ref frames, Math.Max(state.Frames.Length, frames.Length * 2));
        Array.Copy(state.Frames, frames, state.Frames.Length);
        fp = state.Frames.Length - 1;
        pc = state.Pc;
        InitHeader();
    }

    private void SaveUndo()
    {
        if (!CanCapture)
        {
            Store(0);
            return;
        }
        if (options.UndoLevels <= 0)
        {
            Store(0xFFFF);
            return;
        }
        undo.Add(Capture());
        if (undo.Count > options.UndoLevels)
            undo.RemoveAt(0);
        Store(1);
    }

    private void RestoreUndo()
    {
        if (undo.Count == 0 || !CanCapture)
        {
            Store(0);
            return;
        }
        MachineState state = undo[undo.Count - 1];
        undo.RemoveAt(undo.Count - 1);
        Apply(state);
        Store(2);
    }

    /// <summary>
    /// The whole machine state as a Quetzal file, for suspending the story and carrying on later
    /// with <see cref="Resume"/>. Allowed while running between instructions or waiting for input,
    /// not inside a timed-input routine. Screen contents aren't included; window, style, colour
    /// and font are, and <see cref="Resume"/> outputs them again.
    /// </summary>
    public byte[] SaveState()
    {
        if (status != ZStatus.Running && status != ZStatus.WaitingForLine && status != ZStatus.WaitingForKey)
            throw new InvalidOperationException($"can't save the state while {status}");
        if (!CanCapture)
            throw new InvalidOperationException("can't save the state inside a timed-input routine");
        if (memStreamDepth > 0)
            throw new InvalidOperationException("can't save the state while output goes to memory");
        return Quetzal.Write(story, Capture(), WriteSuspendData());
    }

    /// <summary>
    /// Starts a story from a state saved by <see cref="SaveState"/>, or from a Quetzal save made by
    /// the story's own save command (which then continues as if restored).
    /// </summary>
    /// <exception cref="ArgumentException">The data isn't a save of this story.</exception>
    public static ZMachine Resume(byte[] storyFile, byte[] state, ZMachineOptions options = null)
    {
        var machine = new ZMachine(storyFile, options);
        if (!Quetzal.TryRead(machine.story, machine.dynamicSize, state, out MachineState loaded, out byte[] suspend, out string error))
            throw new ArgumentException(error);
        machine.LoadState(loaded, suspend);
        return machine;
    }

    private void LoadState(MachineState state, byte[] suspend)
    {
        Apply(state);
        status = ZStatus.Running;
        if (suspend != null)
            ReadSuspendData(suspend);
        else
            Store(2);
    }

    /// <summary>The host stored the data in <see cref="SaveRequest"/>, or failed to.</summary>
    public void CompleteSave(bool succeeded)
    {
        if (status != ZStatus.WaitingForSave)
            throw new InvalidOperationException($"the machine isn't waiting for a save ({status})");
        SaveRequest = null;
        status = ZStatus.Running;
        WriteVar(pendingStore, succeeded ? 1 : 0);
    }

    /// <summary>
    /// Gives the story the data it asked for in <see cref="RestoreRequest"/>, or null if there is
    /// none. Returns false if the data was refused; <see cref="RestoreError"/> says why.
    /// </summary>
    public bool CompleteRestore(byte[] data)
    {
        if (status != ZStatus.WaitingForRestore)
            throw new InvalidOperationException($"the machine isn't waiting for a restore ({status})");
        ZRestoreRequest request = RestoreRequest;
        RestoreRequest = null;
        RestoreError = null;
        status = ZStatus.Running;
        if (data == null)
        {
            WriteVar(pendingStore, 0);
            return false;
        }
        if (request.IsAuxiliary)
        {
            int n = Math.Min(data.Length, request.MaxLength);
            try
            {
                for (int i = 0; i < n; i++)
                    WriteByte(auxTable + i, data[i]);
            }
            catch (ZMachineException e)
            {
                Fail(e.Message);
                return false;
            }
            WriteVar(pendingStore, n);
            return true;
        }
        if (!Quetzal.TryRead(story, dynamicSize, data, out MachineState state, out byte[] suspend, out string error))
        {
            RestoreError = error;
            WriteVar(pendingStore, 0);
            return false;
        }
        LoadState(state, suspend);
        return true;
    }

    /// <summary>The save the machine is waiting for the host to store.</summary>
    public ZSaveRequest SaveRequest { get; private set; }

    /// <summary>The save the machine is waiting for the host to load.</summary>
    public ZRestoreRequest RestoreRequest { get; private set; }

    private int auxTable;

    private void OpSave()
    {
        if (opCount >= 2)
        {
            int table = ops[0], length = ops[1];
            string name = opCount > 2 && ops[2] != 0 ? ReadName(ops[2]) : null;
            var data = new byte[length];
            Array.Copy(mem, table, data, 0, length);
            pendingStore = mem[pc++];
            SaveRequest = new ZSaveRequest(data, name, true);
            status = ZStatus.WaitingForSave;
            return;
        }
        if (!CanCapture)
        {
            Store(0);
            return;
        }
        MachineState state = Capture();
        pendingStore = mem[pc++];
        SaveRequest = new ZSaveRequest(Quetzal.Write(story, state, null), null, false);
        status = ZStatus.WaitingForSave;
    }

    private void OpRestore()
    {
        if (opCount >= 2)
        {
            auxTable = ops[0];
            string name = opCount > 2 && ops[2] != 0 ? ReadName(ops[2]) : null;
            pendingStore = mem[pc++];
            RestoreRequest = new ZRestoreRequest(name, true, ops[1]);
        }
        else
        {
            pendingStore = mem[pc++];
            RestoreRequest = new ZRestoreRequest(null, false, 0);
        }
        status = ZStatus.WaitingForRestore;
    }

    /// <summary>A file name in the story's memory: a length byte, then ZSCII.</summary>
    private string ReadName(int address)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < mem[address]; i++)
            sb.Append(ZsciiToChar(mem[address + 1 + i]));
        return sb.ToString();
    }
}

internal struct Frame
{
    public int ReturnPc;
    /// <summary>Variable that receives the result, or -1 to discard it.</summary>
    public int StoreVar;
    public int LocalBase;
    public int LocalCount;
    public int ArgCount;
    /// <summary>A timed-input routine, called by the interpreter.</summary>
    public bool Interrupt;

    public int EvalBase => LocalBase + LocalCount;
}

internal sealed class MachineState
{
    public byte[] Dynamic;
    public ushort[] Stack;
    public Frame[] Frames;
    /// <summary>For a game save: the save instruction's store byte. For a suspend: where to continue.</summary>
    public int Pc;
}

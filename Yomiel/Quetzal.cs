using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Yomiel;

/// <summary>
/// Quetzal 1.4 save files: an IFF FORM of type IFZS holding IFhd (story identity and PC), CMem
/// (dynamic memory XORed with the story file, run-length encoded) and Stks (call frames).
/// Suspended states add an IntD chunk with interpreter ID "YMEL".
/// </summary>
internal static class Quetzal
{
    private const string InterpreterId = "YMEL";

    public static byte[] Write(byte[] story, MachineState state, byte[] suspendData)
    {
        var body = new MemoryStream();
        body.Write(Ascii("IFZS"), 0, 4);

        var header = new byte[13];
        Array.Copy(story, 2, header, 0, 2);
        Array.Copy(story, 0x12, header, 2, 6);
        Array.Copy(story, 0x1C, header, 8, 2);
        header[10] = (byte)(state.Pc >> 16);
        header[11] = (byte)(state.Pc >> 8);
        header[12] = (byte)state.Pc;
        WriteChunk(body, "IFhd", header);
        WriteChunk(body, "CMem", CompressMemory(story, state.Dynamic));
        WriteChunk(body, "Stks", WriteStacks(state));
        if (suspendData != null)
        {
            var intd = new MemoryStream();
            intd.Write(Ascii("    "), 0, 4); // OS ID: any
            intd.WriteByte(0); // flags
            intd.WriteByte(0); // contents ID
            intd.WriteByte(0);
            intd.WriteByte(0);
            intd.Write(Ascii(InterpreterId), 0, 4);
            intd.Write(suspendData, 0, suspendData.Length);
            WriteChunk(body, "IntD", intd.ToArray());
        }

        var file = new MemoryStream();
        file.Write(Ascii("FORM"), 0, 4);
        WriteUInt32(file, (uint)body.Length);
        body.WriteTo(file);
        return file.ToArray();
    }

    public static bool TryRead(byte[] story, int dynamicSize, byte[] data, out MachineState state, out byte[] suspendData, out string error)
    {
        state = null;
        suspendData = null;
        try
        {
            error = Read(story, dynamicSize, data, out state, out suspendData);
        }
        catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is EndOfStreamException)
        {
            error = "the save file is damaged";
        }
        if (error != null)
            state = null;
        return error == null;
    }

    private static string Read(byte[] story, int dynamicSize, byte[] data, out MachineState state, out byte[] suspendData)
    {
        state = null;
        suspendData = null;
        if (data.Length < 12 || Encoding.ASCII.GetString(data, 0, 4) != "FORM" || Encoding.ASCII.GetString(data, 8, 4) != "IFZS")
            return "not a Quetzal save file";
        int end = Math.Min(data.Length, 8 + (int)ReadUInt32(data, 4));

        byte[] header = null, memory = null, stacks = null;
        bool compressed = false;
        for (int pos = 12; pos + 8 <= end;)
        {
            string id = Encoding.ASCII.GetString(data, pos, 4);
            int length = (int)ReadUInt32(data, pos + 4);
            if (length < 0 || pos + 8 + length > end)
                return "the save file is damaged";
            var chunk = new byte[length];
            Array.Copy(data, pos + 8, chunk, 0, length);
            switch (id)
            {
                case "IFhd": header = chunk; break;
                case "CMem": memory = chunk; compressed = true; break;
                case "UMem": memory = chunk; compressed = false; break;
                case "Stks": stacks = chunk; break;
                case "IntD":
                    if (length >= 12 && Encoding.ASCII.GetString(chunk, 8, 4) == InterpreterId)
                    {
                        suspendData = new byte[length - 12];
                        Array.Copy(chunk, 12, suspendData, 0, suspendData.Length);
                    }
                    break;
            }
            pos += 8 + length + (length & 1);
        }
        if (header == null || header.Length < 13 || memory == null || stacks == null)
            return "the save file is missing a chunk";
        for (int i = 0; i < 2; i++)
            if (header[i] != story[2 + i] || header[8 + i] != story[0x1C + i])
                return "the save is from a different story";
        for (int i = 0; i < 6; i++)
            if (header[2 + i] != story[0x12 + i])
                return "the save is from a different story";

        var dynamic = new byte[dynamicSize];
        Array.Copy(story, dynamic, dynamicSize);
        if (compressed)
        {
            int at = 0;
            for (int i = 0; i < memory.Length; i++)
            {
                if (memory[i] == 0)
                {
                    at += memory[++i] + 1;
                }
                else
                {
                    if (at >= dynamicSize)
                        return "the save file's memory is too long";
                    dynamic[at++] ^= memory[i];
                }
            }
            if (at > dynamicSize)
                return "the save file's memory is too long";
        }
        else
        {
            if (memory.Length != dynamicSize)
                return "the save file's memory is the wrong size";
            dynamic = memory;
        }

        state = new MachineState
        {
            Dynamic = dynamic,
            Pc = header[10] << 16 | header[11] << 8 | header[12],
        };
        return ReadStacks(stacks, state);
    }

    private static byte[] CompressMemory(byte[] story, byte[] dynamic)
    {
        var result = new List<byte>();
        int zeros = 0;
        for (int i = 0; i < dynamic.Length; i++)
        {
            int b = dynamic[i] ^ story[i];
            if (b == 0)
            {
                zeros++;
                continue;
            }
            for (; zeros > 0; zeros -= 256)
            {
                result.Add(0);
                result.Add((byte)(Math.Min(zeros, 256) - 1));
            }
            zeros = 0;
            result.Add((byte)b);
        }
        return result.ToArray();
    }

    private static byte[] WriteStacks(MachineState state)
    {
        var s = new MemoryStream();
        Frame[] frames = state.Frames;
        for (int i = 0; i < frames.Length; i++)
        {
            Frame f = frames[i];
            int evalEnd = i + 1 < frames.Length ? frames[i + 1].LocalBase : state.Stack.Length;
            int evalCount = evalEnd - f.EvalBase;
            s.WriteByte((byte)(f.ReturnPc >> 16));
            s.WriteByte((byte)(f.ReturnPc >> 8));
            s.WriteByte((byte)f.ReturnPc);
            s.WriteByte((byte)(f.LocalCount | (f.StoreVar < 0 ? 0x10 : 0)));
            s.WriteByte((byte)(f.StoreVar < 0 ? 0 : f.StoreVar));
            s.WriteByte((byte)((1 << Math.Min(f.ArgCount, 7)) - 1));
            WriteUInt16(s, evalCount);
            for (int v = 0; v < f.LocalCount; v++)
                WriteUInt16(s, state.Stack[f.LocalBase + v]);
            for (int v = 0; v < evalCount; v++)
                WriteUInt16(s, state.Stack[f.EvalBase + v]);
        }
        return s.ToArray();
    }

    private static string ReadStacks(byte[] data, MachineState state)
    {
        var frames = new List<Frame>();
        var stack = new List<ushort>();
        int pos = 0;
        while (pos < data.Length)
        {
            if (pos + 8 > data.Length)
                return "the save file's stack is damaged";
            int flags = data[pos + 3];
            int args = data[pos + 5];
            var f = new Frame
            {
                ReturnPc = data[pos] << 16 | data[pos + 1] << 8 | data[pos + 2],
                LocalCount = flags & 15,
                StoreVar = (flags & 0x10) != 0 ? -1 : data[pos + 4],
                LocalBase = stack.Count,
            };
            while ((args & 1) != 0)
            {
                f.ArgCount++;
                args >>= 1;
            }
            int evalCount = data[pos + 6] << 8 | data[pos + 7];
            pos += 8;
            int words = f.LocalCount + evalCount;
            if (pos + 2 * words > data.Length || stack.Count + words > 65535)
                return "the save file's stack is damaged";
            for (int i = 0; i < words; i++, pos += 2)
                stack.Add((ushort)(data[pos] << 8 | data[pos + 1]));
            frames.Add(f);
        }
        if (frames.Count == 0 || frames[0].LocalCount != 0)
            return "the save file's stack is damaged";
        state.Frames = frames.ToArray();
        state.Stack = stack.ToArray();
        return null;
    }

    private static void WriteChunk(Stream s, string id, byte[] data)
    {
        s.Write(Ascii(id), 0, 4);
        WriteUInt32(s, (uint)data.Length);
        s.Write(data, 0, data.Length);
        if ((data.Length & 1) != 0)
            s.WriteByte(0);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static void WriteUInt16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }

    private static uint ReadUInt32(byte[] d, int p) => (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);
}

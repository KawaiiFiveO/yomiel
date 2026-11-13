using System.Collections.Generic;

namespace Yomiel;

// Object table, versions 4 and later: 63 default property words, then 14-byte entries (48
// attribute bits, parent, sibling, child, property table address). Object 0 means "nothing":
// reading from it gives 0 and changing it does nothing, as most interpreters allow. Numbers past
// the end of the table are treated the same way: some Inform games pass a dictionary word where
// an object belongs (I-0 asks for the parent of the word "smell").
public sealed partial class ZMachine
{
    private int objectCount = -1;

    /// <summary>
    /// The number of objects. The table has no count, so it ends where the lowest property table
    /// starts, as Inform lays it out, or at the end of the file.
    /// </summary>
    private int ObjectCount
    {
        get
        {
            if (objectCount >= 0)
                return objectCount;
            int lowest = story.Length;
            int n = 0;
            for (int entry = objects + 126; entry + 14 <= lowest && entry + 14 <= story.Length; entry += 14, n++)
            {
                int table = story[entry + 12] << 8 | story[entry + 13];
                if (table > entry && table < lowest)
                    lowest = table;
            }
            return objectCount = n;
        }
    }

    private readonly HashSet<int> warnedAt = new();

    /// <summary>Object 0 is accepted quietly; numbers past the table also warn, once per instruction.</summary>
    private bool IsObject(int obj)
    {
        if (obj >= 1 && obj <= ObjectCount)
            return true;
        if (obj != 0 && warnedAt.Add(instructionStart))
            Emit(ZOutputKind.Warning).Text = $"object {obj} doesn't exist (the story has {ObjectCount}); treated as nothing (instruction at ${instructionStart:X5})";
        return false;
    }

    private int ObjectAddress(int obj) => objects + 126 + (obj - 1) * 14;

    private int Parent(int obj) => IsObject(obj) ? ReadWord(ObjectAddress(obj) + 6) : 0;
    private int Sibling(int obj) => IsObject(obj) ? ReadWord(ObjectAddress(obj) + 8) : 0;
    private int Child(int obj) => IsObject(obj) ? ReadWord(ObjectAddress(obj) + 10) : 0;
    private int PropertyTable(int obj) => ReadWord(ObjectAddress(obj) + 12);

    private void SetParent(int obj, int value) => WriteWord(ObjectAddress(obj) + 6, value);
    private void SetSibling(int obj, int value) => WriteWord(ObjectAddress(obj) + 8, value);
    private void SetChild(int obj, int value) => WriteWord(ObjectAddress(obj) + 10, value);

    private bool TestAttribute(int obj, int attribute)
    {
        if (!IsObject(obj) || attribute > 47)
            return false;
        return (mem[ObjectAddress(obj) + attribute / 8] & 0x80 >> attribute % 8) != 0;
    }

    private void SetAttribute(int obj, int attribute, bool on)
    {
        if (!IsObject(obj) || attribute > 47)
            return;
        int address = ObjectAddress(obj) + attribute / 8;
        int bit = 0x80 >> attribute % 8;
        WriteByte(address, on ? mem[address] | bit : mem[address] & ~bit);
    }

    private void RemoveObject(int obj)
    {
        if (!IsObject(obj))
            return;
        int parent = Parent(obj);
        if (parent == 0)
            return;
        int next = Sibling(obj);
        int child = Child(parent);
        if (child == obj)
        {
            SetChild(parent, next);
        }
        else
        {
            while (child != 0 && Sibling(child) != obj)
                child = Sibling(child);
            if (child != 0)
                SetSibling(child, next);
        }
        SetParent(obj, 0);
        SetSibling(obj, 0);
    }

    private void InsertObject(int obj, int destination)
    {
        if (!IsObject(obj) || !IsObject(destination))
            return;
        RemoveObject(obj);
        SetSibling(obj, Child(destination));
        SetChild(destination, obj);
        SetParent(obj, destination);
    }

    private void PrintObjectName(int obj)
    {
        if (!IsObject(obj))
            return;
        int table = PropertyTable(obj);
        if (mem[table] != 0)
            PrintZString(table + 1);
    }

    // A property entry starts with a size byte: bits 0-5 the number, bit 7 set means a second
    // byte holds the length in bits 0-5 (0 means 64), else bit 6 gives a length of 2 or 1.

    private int FirstProperty(int obj)
    {
        int table = PropertyTable(obj);
        return table + 1 + 2 * mem[table];
    }

    private int PropertyData(int entry) => (mem[entry] & 0x80) != 0 ? entry + 2 : entry + 1;

    private int NextProperty(int entry) => PropertyData(entry) + GetPropertyLength(PropertyData(entry));

    private int FindProperty(int obj, int property)
    {
        if (!IsObject(obj))
            return 0;
        for (int entry = FirstProperty(obj); mem[entry] != 0; entry = NextProperty(entry))
        {
            if ((mem[entry] & 0x3F) == property)
                return entry;
        }
        return 0;
    }

    private int GetPropertyLength(int data)
    {
        if (data == 0)
            return 0;
        int b = mem[data - 1];
        if ((b & 0x80) != 0)
        {
            int length = b & 0x3F;
            return length == 0 ? 64 : length;
        }
        return (b & 0x40) != 0 ? 2 : 1;
    }

    private int GetProperty(int obj, int property)
    {
        int entry = FindProperty(obj, property);
        if (entry == 0)
            return property >= 1 && property <= 63 ? ReadWord(objects + 2 * (property - 1)) : 0;
        int data = PropertyData(entry);
        return GetPropertyLength(data) == 1 ? mem[data] : ReadWord(data);
    }

    private int GetPropertyAddress(int obj, int property)
    {
        int entry = FindProperty(obj, property);
        return entry == 0 ? 0 : PropertyData(entry);
    }

    private int GetNextProperty(int obj, int property)
    {
        if (!IsObject(obj))
            return 0;
        int entry;
        if (property == 0)
        {
            entry = FirstProperty(obj);
        }
        else
        {
            entry = FindProperty(obj, property);
            if (entry == 0)
                throw new ZMachineException($"get_next_prop: object {obj} has no property {property}");
            entry = NextProperty(entry);
        }
        return mem[entry] & 0x3F;
    }

    private void PutProperty(int obj, int property, int value)
    {
        if (!IsObject(obj))
            return;
        int entry = FindProperty(obj, property);
        if (entry == 0)
            throw new ZMachineException($"put_prop: object {obj} has no property {property}");
        int data = PropertyData(entry);
        if (GetPropertyLength(data) == 1)
            WriteByte(data, value);
        else
            WriteWord(data, value);
    }
}

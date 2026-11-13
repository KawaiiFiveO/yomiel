namespace Yomiel;

public sealed partial class ZMachine
{
    private void Step()
    {
        instructionStart = pc;
        int op = mem[pc++];
        if (op < 0x80)
        {
            // Long form: always 2OP, each operand a small constant or a variable.
            ops[0] = (op & 0x40) != 0 ? ReadVar(mem[pc++]) : mem[pc++];
            ops[1] = (op & 0x20) != 0 ? ReadVar(mem[pc++]) : mem[pc++];
            opCount = 2;
            Op2(op & 0x1F);
        }
        else if (op < 0xC0)
        {
            int type = op >> 4 & 3;
            if (type != 3)
            {
                ops[0] = ReadOperand(type);
                opCount = 1;
                Op1(op & 0x0F);
            }
            else if (op == 0xBE)
            {
                int ext = mem[pc++];
                opCount = 0;
                ReadOperands(mem[pc++]);
                OpExt(ext);
            }
            else
            {
                opCount = 0;
                Op0(op & 0x0F);
            }
        }
        else
        {
            int types = mem[pc++];
            int types2 = op == 0xEC || op == 0xFA ? mem[pc++] : 0xFF;
            opCount = 0;
            ReadOperands(types);
            if (opCount == 4)
                ReadOperands(types2);
            if ((op & 0x20) == 0)
                Op2(op & 0x1F);
            else
                OpVar(op & 0x1F);
        }
    }

    private void ReadOperands(int types)
    {
        for (int shift = 6; shift >= 0; shift -= 2)
        {
            int type = types >> shift & 3;
            if (type == 3)
                return;
            ops[opCount++] = ReadOperand(type);
        }
    }

    private int ReadOperand(int type)
    {
        switch (type)
        {
            case 0:
                int w = mem[pc] << 8 | mem[pc + 1];
                pc += 2;
                return w;
            case 1:
                return mem[pc++];
            default:
                return ReadVar(mem[pc++]);
        }
    }

    private ZMachineException Illegal(string form, int number) =>
        new($"illegal opcode {form}:{number} in version {Version}");

    private void Op2(int n)
    {
        int a = ops[0], b = ops[1];
        switch (n)
        {
            case 1: // je
                bool equal = false;
                for (int i = 1; i < opCount; i++)
                    equal |= ops[i] == a;
                Branch(equal);
                break;
            case 2: Branch((short)a < (short)b); break; // jl
            case 3: Branch((short)a > (short)b); break; // jg
            case 4: // dec_chk
            {
                int v = (short)(PeekVar(a) - 1);
                PokeVar(a, v);
                Branch(v < (short)b);
                break;
            }
            case 5: // inc_chk
            {
                int v = (short)(PeekVar(a) + 1);
                PokeVar(a, v);
                Branch(v > (short)b);
                break;
            }
            case 6: Branch(Parent(a) == b); break; // jin
            case 7: Branch((a & b) == b); break; // test
            case 8: Store(a | b); break; // or
            case 9: Store(a & b); break; // and
            case 10: Branch(TestAttribute(a, b)); break;
            case 11: SetAttribute(a, b, true); break;
            case 12: SetAttribute(a, b, false); break;
            case 13: PokeVar(a, b); break; // store
            case 14: InsertObject(a, b); break;
            case 15: Store(ReadWord((a + 2 * b) & 0xFFFF)); break; // loadw
            case 16: Store(mem[(a + b) & 0xFFFF]); break; // loadb
            case 17: Store(GetProperty(a, b)); break;
            case 18: Store(GetPropertyAddress(a, b)); break;
            case 19: Store(GetNextProperty(a, b)); break;
            case 20: Store((short)a + (short)b); break;
            case 21: Store((short)a - (short)b); break;
            case 22: Store((short)a * (short)b); break;
            case 23: // div
                if (b == 0)
                    throw new ZMachineException("division by zero");
                Store((short)a / (short)b);
                break;
            case 24: // mod
                if (b == 0)
                    throw new ZMachineException("division by zero");
                Store((short)a % (short)b);
                break;
            case 25: Call(a, opCount - 1, mem[pc++]); break; // call_2s
            case 26: Call(a, opCount - 1, -1); break; // call_2n
            case 27: SetColor(a, b); break; // set_colour
            case 28: Throw(a, b); break;
            default: throw Illegal("2OP", n);
        }
    }

    private void Op1(int n)
    {
        int a = ops[0];
        switch (n)
        {
            case 0: Branch(a == 0); break; // jz
            case 1: // get_sibling
            {
                int o = Sibling(a);
                Store(o);
                Branch(o != 0);
                break;
            }
            case 2: // get_child
            {
                int o = Child(a);
                Store(o);
                Branch(o != 0);
                break;
            }
            case 3: Store(Parent(a)); break;
            case 4: Store(GetPropertyLength(a)); break;
            case 5: PokeVar(a, PeekVar(a) + 1); break; // inc
            case 6: PokeVar(a, PeekVar(a) - 1); break; // dec
            case 7: PrintZString(a); break; // print_addr
            case 8: Call(a, 0, mem[pc++]); break; // call_1s
            case 9: RemoveObject(a); break;
            case 10: PrintObjectName(a); break;
            case 11: Return(a); break;
            case 12: pc += (short)a - 2; break; // jump
            case 13: PrintZString(UnpackString(a)); break; // print_paddr
            case 14: Store(PeekVar(a)); break; // load
            case 15: Call(a, 0, -1); break; // call_1n
            default: throw Illegal("1OP", n);
        }
    }

    private void Op0(int n)
    {
        switch (n)
        {
            case 0: Return(1); break; // rtrue
            case 1: Return(0); break; // rfalse
            case 2: pc = PrintZString(pc); break; // print
            case 3: // print_ret
                pc = PrintZString(pc);
                PrintZscii(13);
                Return(1);
                break;
            case 4: break; // nop
            case 7: Restart(); break;
            case 8: Return(Pop()); break; // ret_popped
            case 9: Store(fp); break; // catch
            case 10: // quit
                status = ZStatus.Halted;
                break;
            case 11: PrintZscii(13); break; // new_line
            case 12: break; // show_status: version 3 only, ignored as most interpreters do
            case 13: Branch(Verify()); break;
            case 15: Branch(true); break; // piracy
            default: throw Illegal("0OP", n);
        }
    }

    private void OpVar(int n)
    {
        int a = ops[0];
        switch (n)
        {
            case 0: Call(a, opCount - 1, mem[pc++]); break; // call_vs
            case 1: WriteWord((a + 2 * ops[1]) & 0xFFFF, ops[2]); break; // storew
            case 2: WriteByte((a + ops[1]) & 0xFFFF, ops[2]); break; // storeb
            case 3: PutProperty(a, ops[1], ops[2]); break;
            case 4: ReadLine(); break; // aread
            case 5: PrintZscii(a); break; // print_char
            case 6: // print_num
                foreach (char c in ((short)a).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    PrintZscii(c);
                break;
            case 7: Store(Random((short)a)); break;
            case 8: Push(a); break;
            case 9: PokeVar(a, Pop()); break; // pull
            case 10: SplitWindow(a); break;
            case 11: SelectWindow(a); break;
            case 12: Call(a, opCount - 1, mem[pc++]); break; // call_vs2
            case 13: EraseWindow((short)a); break;
            case 14: EraseLine(a); break;
            case 15: SetCursor((short)a, (short)ops[1]); break;
            case 16: GetCursor(a); break;
            case 17: SetTextStyle(a); break;
            case 18: Emit(ZOutputKind.BufferMode).Value = a; break;
            case 19: OutputStream((short)a, ops[1]); break;
            case 20: Emit(ZOutputKind.InputStream).Value = a; break;
            case 21: Emit(ZOutputKind.Sound).Value = opCount > 0 ? a : 1; break; // sound_effect
            case 22: ReadKey(); break; // read_char
            case 23: ScanTable(); break;
            case 24: Store(~a); break; // not
            case 25: Call(a, opCount - 1, -1); break; // call_vn
            case 26: Call(a, opCount - 1, -1); break; // call_vn2
            case 27: Tokenise(a, ops[1], opCount > 2 ? ops[2] : 0, opCount > 3 && ops[3] != 0); break;
            case 28: EncodeText(); break;
            case 29: CopyTable(a, ops[1], (short)ops[2]); break;
            case 30: PrintTable(); break;
            case 31: Branch(a <= frames[fp].ArgCount); break; // check_arg_count
            default: throw Illegal("VAR", n);
        }
    }

    private void OpExt(int n)
    {
        int a = ops[0];
        switch (n)
        {
            case 0: OpSave(); break;
            case 1: OpRestore(); break;
            case 2: // log_shift
            {
                int places = (short)ops[1];
                Store(places >= 16 || places <= -16 ? 0 : places >= 0 ? a << places : a >> -places);
                break;
            }
            case 3: // art_shift
            {
                int places = (short)ops[1];
                Store(places >= 0 ? (places >= 16 ? 0 : a << places) : (short)a >> System.Math.Min(-places, 15));
                break;
            }
            case 4: SetFont(a); break;
            case 9: SaveUndo(); break;
            case 10: RestoreUndo(); break;
            case 11: PrintUnicode(a); break;
            case 12: Store(CheckUnicode(a)); break;
            case 13: SetTrueColor((short)a, (short)ops[1]); break;
            default: throw Illegal("EXT", n);
        }
    }

    private void ScanTable()
    {
        int x = ops[0], table = ops[1], length = ops[2];
        int form = opCount > 3 ? ops[3] : 0x82;
        int field = form & 0x7F;
        bool words = (form & 0x80) != 0;
        for (int i = 0; i < length; i++)
        {
            int address = table + i * field;
            int value = words ? ReadWord(address) : mem[address];
            if (value == x)
            {
                Store(address);
                Branch(true);
                return;
            }
        }
        Store(0);
        Branch(false);
    }

    private void CopyTable(int first, int second, int size)
    {
        if (second == 0)
        {
            for (int i = 0; i < System.Math.Abs(size); i++)
                WriteByte(first + i, 0);
        }
        else if (size < 0 || first > second || first + size <= second)
        {
            // Negative size copies forwards even when the tables overlap.
            for (int i = 0; i < System.Math.Abs(size); i++)
                WriteByte(second + i, mem[first + i]);
        }
        else
        {
            for (int i = size - 1; i >= 0; i--)
                WriteByte(second + i, mem[first + i]);
        }
    }
}

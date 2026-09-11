using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Recupereaza citirile din bazinul de constante al compilatorului: <c>[adresa absoluta]</c> catre o
/// sectiune care nu se poate scrie. Octetii de acolo sunt fixati in fisier si nu se schimba la rulare,
/// deci valoarea chiar este cunoscuta - nu e o ghicitura care sa inlocuiasca un marker cu ceva gresit.
/// </summary>
public static class ConstantDataRecovery
{
    // Numarat pe out_w8 (150 de dll-uri): din cele 5.329 de markere "Unmanaged memory load" cu baza o
    // adresa absoluta, 689 cad in .rdata, iar 578 dintre ele stau la adrese pe care codul masina le
    // citeste pe 16 octeti (movaps, movdqa, xorps, andps) - adica bazinul de constante SIMD al lui msvc.
    // Exemplu: 0x183AB51E0 tine {1,1,1,1} in float si ajunge intr-un camp Color/Vector4; azi metoda scoate
    // Color(0,0,0,0) plus un marker.
    //
    // Cauza e ridicarea pe benzi din X86InstructionSet: un movaps de 16 octeti devine patru mutari de cate
    // 4 octeti, de la K, K+4, K+8 si K+12, iar transformarea in literal float exista doar pe drumul
    // movss/movsd, deci niciuna dintre cele patru nu devine constanta. Dovada independenta ca asa se
    // intampla: K+4, K+8 si K+12 nu au NICIO referinta rip-relativa in GameAssembly.dll - 0x183AB51E4,
    // 0x183AB51E8 si 0x183AB51EC apar de 21, 21 si 23 de ori in iesire si de zero ori in codul masina.
    // Ele exista numai in ISIL, fabricate de impartirea pe benzi.
    //
    // CPP2IL_RODATA_CONST=0 lasa citirile asa cum sunt.
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("CPP2IL_RODATA_CONST") != "0";

    private const uint ImageScnMemWrite = 0x80000000;

    private readonly record struct ReadOnlyRange(ulong Start, ulong End, long RawStart);

    private static readonly ConditionalWeakTable<Il2CppBinary, ReadOnlyRange[]> RangesByBinary = new();

    public static void Run(MethodAnalysisContext method)
    {
        if (!Enabled)
            return;

        var graph = method.ControlFlowGraph;
        if (graph == null)
            return;

        List<Instruction>? candidates = null;

        foreach (var instruction in graph.Instructions)
        {
            if (instruction.OpCode != OpCode.Move || instruction.Operands.Count != 2)
                continue;

            if (instruction.Operands[1] is not MemoryOperand { IsConstant: true })
                continue;

            if (instruction.Operands[0] is not LocalVariable destination || !IsXmmRegister(destination.Register.Name))
                continue;

            candidates ??= [];
            candidates.Add(instruction);
        }

        if (candidates == null)
            return;

        // Latimea citirii nu se vede din operand, iar o latime gresita ar scoate exact genul de valoare
        // pe jumatate adevarata pe care un marker cinstit o bate. Impartirea pe benzi se face insa doar
        // in metodele care chiar amesteca benzile (vezi permutesLanes din X86InstructionSet), si numai
        // acolo o mutare de 16 octeti a fost deja modelata ca patru de cate 4 - deci numai acolo stim ca
        // 4 e raspunsul. Semnul e local si sigur: registrele de banda 1-3 se cheama xmmN_1..xmmN_3 si nu
        // apar deloc altfel.
        if (!TracksLanes(graph))
            return;

        var binary = method.AppContext.Binary;
        var ranges = RangesByBinary.GetValue(binary, ComputeRanges);
        if (ranges.Length == 0)
            return;

        foreach (var instruction in candidates)
        {
            var constant = (MemoryOperand)instruction.Operands[1];

            if (!TryReadConstant(binary, ranges, unchecked((ulong)constant.Addend), out var value))
                continue;

            // Ramane un intreg, nu un float: `mov r32, m32` largeste cu zero, iar FloatLiteralRecovery
            // decide mai incolo daca bitii sunt un float sau un double, dupa tipul locului in care ajung.
            // Aici nu avem de unde sti, si ghicitul ar fi tot o minciuna.
            instruction.SetOperand(1, new Immediate(value));
        }
    }

    private static bool TracksLanes(ISILControlFlowGraph graph)
    {
        foreach (var instruction in graph.Instructions)
        foreach (var operand in instruction.Operands)
            if (operand is LocalVariable local && IsLaneRegister(local.Register.Name))
                return true;

        return false;
    }

    private static bool IsXmmRegister(string? name) => name != null && name.StartsWith("xmm", StringComparison.Ordinal);

    // xmmN_1, xmmN_2, xmmN_3 - benzile de sus, fabricate numai de impartirea pe benzi. Banda 0 pastreaza
    // numele registrului, deci nu se recunoaste dupa nume; de aia intrebarea se pune pe toata metoda.
    private static bool IsLaneRegister(string? name) =>
        IsXmmRegister(name) && name!.Length >= 6 && name[^2] == '_' && name[^1] is '1' or '2' or '3';

    private static bool TryReadConstant(Il2CppBinary binary, ReadOnlyRange[] ranges, ulong address, out long value)
    {
        foreach (var range in ranges)
        {
            if (address < range.Start || address + 4 > range.End)
                continue;

            var raw = binary.GetRawBinaryContent();
            var offset = range.RawStart + (long)(address - range.Start);

            if (offset < 0 || offset + 4 > raw.Length)
                break;

            value = U32(raw, (int)offset);
            return true;
        }

        value = 0;
        return false;
    }

    // Citite de mana, pe octeti: proiectul se compileaza si pentru netstandard2.0, unde BinaryPrimitives
    // vine dintr-un pachet in plus pe care nimic din Cpp2IL.Core nu il foloseste azi.
    private static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint U32(ReadOnlySpan<byte> data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static ulong U64(ReadOnlySpan<byte> data, int offset) =>
        U32(data, offset) | ((ulong)U32(data, offset + 4) << 32);

    /// <summary>
    /// Intervalele care nu se pot scrie SI au octeti in fisier. Amandoua conditiile conteaza: una fara
    /// alta nu spune nimic. O sectiune care se scrie (.data) poate arata altfel la rulare decat in fisier,
    /// iar coada neinitializata a unei sectiuni (.bss, adica partea de dincolo de SizeOfRawData) nu are
    /// octeti deloc - dar MapVirtualAddressToRaw tot returneaza un offset pentru ea, care nimereste in
    /// sectiunea urmatoare. De aici taierea la min(VirtualSize, SizeOfRawData).
    /// </summary>
    private static ReadOnlyRange[] ComputeRanges(Il2CppBinary binary)
    {
        var raw = binary.GetRawBinaryContent();

        if (raw.Length < 0x40 || raw[0] != (byte)'M' || raw[1] != (byte)'Z')
            return [];

        var peOffset = (int)U32(raw, 0x3C);
        if (peOffset <= 0 || peOffset + 24 > raw.Length)
            return [];

        if (raw[peOffset] != (byte)'P' || raw[peOffset + 1] != (byte)'E' || raw[peOffset + 2] != 0 || raw[peOffset + 3] != 0)
            return [];

        var coff = peOffset + 4;
        int sectionCount = U16(raw, coff + 2);
        int optionalSize = U16(raw, coff + 16);
        var optional = coff + 20;
        var table = optional + optionalSize;

        if (optionalSize < 32 || table + sectionCount * 40 > raw.Length)
            return [];

        var magic = U16(raw, optional);
        var imageBase = magic == 0x20B
            ? U64(raw, optional + 24)
            : U32(raw, optional + 28);

        var result = new List<ReadOnlyRange>();

        for (var i = 0; i < sectionCount; i++)
        {
            var entry = table + i * 40;
            var virtualSize = U32(raw, entry + 8);
            var virtualAddress = U32(raw, entry + 12);
            var rawSize = U32(raw, entry + 16);
            var rawPointer = U32(raw, entry + 20);
            var characteristics = U32(raw, entry + 36);

            if ((characteristics & ImageScnMemWrite) != 0)
                continue;

            var length = virtualSize == 0 ? rawSize : Math.Min(virtualSize, rawSize);

            if (length == 0 || (long)rawPointer + length > raw.Length)
                continue;

            result.Add(new ReadOnlyRange(imageBase + virtualAddress, imageBase + virtualAddress + length, rawPointer));
        }

        return result.ToArray();
    }
}

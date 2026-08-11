using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using de4dot.blocks;
using de4dot.blocks.cflow;
using de4dot.code.deobfuscators.ConfuserEx.x86;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using FieldAttributes = dnlib.DotNet.FieldAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using OpCodes = dnlib.DotNet.Emit.OpCodes;
using TypeAttributes = dnlib.DotNet.TypeAttributes;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public class ConstantDecrypterBase
    {
        private readonly InstructionEmulator _instructionEmulator = new InstructionEmulator();
        private X86Method _nativeMethod;

        public MethodDef Method { get; set; }
        public byte[] Decrypted { get; set; }
        public uint Magic1 { get; set; }
        public uint Magic2 { get; set; }
        public bool CanRemove { get; set; } = true;

        // native mode
        public MethodDef NativeMethod { get; internal set; }

        // normal mode
        public uint Num1 { get; internal set; }
        public uint Num2 { get; internal set; }

        private int? CalculateKey()
        {
            var popValue = _instructionEmulator.Peek();

            if (popValue == null || !popValue.IsInt32() || !(popValue as Int32Value).AllBitsValid())
                return null;

            _instructionEmulator.Pop();
            var result = _nativeMethod.Execute(((Int32Value) popValue).Value);
            return result;
        }

        private uint CalculateMagic(uint index)
        {
            uint uint_0;
            if (NativeMethod != null)
            {
                _instructionEmulator.Push(new Int32Value((int)index));
                _nativeMethod = new X86Method(NativeMethod, Method.Module as ModuleDefMD); //TODO: Possible null
                var key = CalculateKey();

                uint_0 = (uint)key.Value;
            }
            else
            {
                uint_0 = index * Num1 ^ Num2;
            }

            uint_0 &= 0x3fffffff;
            uint_0 <<= 2;
            return uint_0;
        }

        public string DecryptString(uint index)
        {
            index = CalculateMagic(index);
            var count = BitConverter.ToInt32(Decrypted, (int) index);
            return string.Intern(Encoding.UTF8.GetString(Decrypted, (int) index + 4, count));
        }

        public T DecryptConstant<T>(uint index)
        {
            index = CalculateMagic(index);
            var array = new T[1];
            Buffer.BlockCopy(Decrypted, (int) index, array, 0, Marshal.SizeOf(typeof(T)));
            return array[0];
        }

        public byte[] DecryptArray(uint index)
        {
            index = CalculateMagic(index);
            var count = BitConverter.ToInt32(Decrypted, (int) index);
            //int lengt = BitConverter.ToInt32(Decrypted, (int)index+4);  we actualy dont need that
            var buffer = new byte[count - 4];
            Buffer.BlockCopy(Decrypted, (int) index + 8, buffer, 0, count - 4);
            return buffer;
        }
    }

    public class ConstantsDecrypter
    {
        private readonly ISimpleDeobfuscator _deobfuscator;
        private readonly MethodDef _lzmaMethod;
        private readonly bool _isNewLzma;

        private readonly ModuleDef _module;

        private readonly string[] _strDecryptCalledMethods =
        {
            "System.Text.Encoding System.Text.Encoding::get_UTF8()",
            "System.String System.Text.Encoding::GetString(System.Byte[],System.Int32,System.Int32)",
            "System.Array System.Array::CreateInstance(System.Type,System.Int32)",
            "System.String System.String::Intern(System.String)",
            "System.Void System.Buffer::BlockCopy(System.Array,System.Int32,System.Array,System.Int32,System.Int32)",
            "System.Type System.Type::GetTypeFromHandle(System.RuntimeTypeHandle)",
            "System.Type System.Type::GetElementType()"
        };

        private byte[] _decryptedBytes;
        private FieldDef _decryptedField, _arrayField;
        internal TypeDef ArrayType;

        // Optional verbose tracing for diagnosing ConfuserEx constant detection; dormant unless
        // the DBG_CDEC environment variable is set to "1" (never in the shipped GUI).
        static readonly bool DBG = System.Environment.GetEnvironmentVariable("DBG_CDEC") == "1";

        public ConstantsDecrypter(ModuleDef module, MethodDef lzmaMethod, ISimpleDeobfuscator deobfsucator, bool isNewLzma)
        {
            _module = module;
            _lzmaMethod = lzmaMethod;
            _deobfuscator = deobfsucator;
            _isNewLzma = isNewLzma;
        }

        public bool CanRemoveLzma { get; private set; }

        public TypeDef Type => ArrayType;

        public MethodDef Method { get; private set; }
        public bool MethodIsInlined { get; private set; }

        public List<FieldDef> Fields => new List<FieldDef> {_decryptedField, _arrayField};

        public List<ConstantDecrypterBase> Decrypters { get; } = new List<ConstantDecrypterBase>();

        public bool Detected => Method != null && _decryptedBytes != null && Decrypters.Count != 0 &&
                                _decryptedField != null && _arrayField != null;

        public void Find()
        {
            var moduleCctor = DotNetUtils.GetModuleTypeCctor(_module);
            if (moduleCctor == null)
                return;
            foreach (var inst in moduleCctor.Body.Instructions)
            {
                if (inst.OpCode != OpCodes.Call)
                    continue;
                if (inst.Operand is not MethodDef method)
                    continue;
                if (!method.HasBody || !method.IsStatic)
                    continue;
                if (!DotNetUtils.IsMethod(method, "System.Void", "()"))
                    continue;

                if (ProcessPossibleInitMethod(method, moduleCctor))
	                return;
            }

            // The initialization code may also be inlined directly into the cctor.
            if (DotNetUtils.CallsMethod(moduleCctor,
	                "System.Void System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray(System.Array,System.RuntimeFieldHandle)"))
	            if (ProcessPossibleInitMethod(moduleCctor, moduleCctor)) {
		            MethodIsInlined = true;
	            }
        }

        private bool ProcessPossibleInitMethod(MethodDef method, MethodDef moduleCctor) {
	        _deobfuscator.Deobfuscate(method, SimpleDeobfuscatorFlags.Force);

	        if (DBG) {
		        var fenv = System.Environment.GetEnvironmentVariable("DBG_FULL");
		        int lim = fenv == "2" ? int.MaxValue : fenv == "1" ? 95 : 22;
		        Console.WriteLine($"[CDEC] after deob, init {method.Name} has {method.Body.Instructions.Count} instrs. First {(lim==int.MaxValue?method.Body.Instructions.Count:lim)}:");
		        var il = method.Body.Instructions;
		        for (int q = 0; q < System.Math.Min(lim, il.Count); q++)
			        Console.WriteLine($"[CDEC]   {q}: IL_{il[q].Offset:X4}: {il[q].OpCode} {il[q].Operand}");
	        }
	        if (!IsStringDecrypterInit(method, out var aField, out var dField, out var iStart, out var iEnd)) {
		        if (DBG) Console.WriteLine($"[CDEC] IsStringDecrypterInit FAILED for {method.Name}");
		        return false;
	        }
	        if (DBG) Console.WriteLine($"[CDEC] IsStringDecrypterInit OK: iStart={iStart} iEnd={iEnd}");
	        try
	        {
		        _decryptedBytes = DecryptArray(method, aField.InitialValue, iStart, iEnd);
	        }
	        catch (Exception e)
	        {
		        Console.WriteLine("ConfuserEx const decrypter found, but decryption failed: " + e.Message);
		        return false;
	        }

	        _arrayField = aField;
	        _decryptedField = dField;
	        ArrayType = DotNetUtils.GetType(_module, _arrayField.FieldSig.Type);
	        Method = method;
	        Decrypters.AddRange(FindStringDecrypters(moduleCctor.DeclaringType));
	        CanRemoveLzma = true;
	        return true;
        }

        private static int FindDecrypterStart(IList<Instruction> instructions) {
	        for (int i = 0; i < 10; i++) {
		        if (instructions[i].IsLdcI4() && instructions[i + 1].IsStloc() && instructions[i + 2].IsLdcI4()) {
			        return i;
		        }
	        }
	        return -1;
        }

        private bool IsStringDecrypterInit(MethodDef method, out FieldDef aField, out FieldDef dField, out int iStart, out int iEnd)
        {
            aField = null;
            dField = null;
            iStart = -1;
            iEnd = -1;
            var instructions = method.Body.Instructions;
            if (instructions.Count < 15)
                return false;

            iStart = FindDecrypterStart(instructions);
            if (iStart == -1)
	            return false;

            if (instructions[iStart].GetLdcI4Value() != instructions[iStart + 2].GetLdcI4Value())
                return false;
            if (instructions[iStart + 3].OpCode != OpCodes.Newarr)
                return false;
            if (instructions[iStart + 3].Operand.ToString() != "System.UInt32")
                return false;
            if (instructions[iStart + 4].OpCode != OpCodes.Dup)
                return false;
            if (instructions[iStart + 5].OpCode != OpCodes.Ldtoken)
                return false;
            aField = instructions[iStart + 5].Operand as FieldDef;
            if (aField?.InitialValue == null) { if (DBG) Console.WriteLine("[CDEC]   fail: aField null/no InitialValue"); return false; }
            if (aField.Attributes != (FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.HasFieldRVA)) {
                if (DBG) Console.WriteLine($"[CDEC]   fail: aField.Attributes={aField.Attributes}"); return false; }
            if (instructions[iStart + 6].OpCode != OpCodes.Call) { if (DBG) Console.WriteLine("[CDEC]   fail: iStart+6 not Call"); return false; }
            if (instructions[iStart + 6].Operand.ToString() !=
                "System.Void System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray(System.Array,System.RuntimeFieldHandle)"
            ) { if (DBG) Console.WriteLine("[CDEC]   fail: iStart+6 not InitializeArray"); return false; }
            if (!instructions[iStart + 7].IsStloc()) { if (DBG) Console.WriteLine("[CDEC]   fail: iStart+7 not stloc"); return false; }

            // The decrypted+decompressed constant pool is stored to a static System.Byte[]
            // field: "<Module>.byte_0 = lzmaDecompress(pool)". There may be OTHER stsfld
            // instructions before it (e.g. a stored Assembly reference), so pick the store
            // whose target field is System.Byte[] rather than the first stsfld found.
            var stsfld = instructions.Skip(iStart + 7).FirstOrDefault(inst =>
                inst.OpCode == OpCodes.Stsfld &&
                inst.Operand is FieldDef f && f.FieldType.FullName == "System.Byte[]");
            if (stsfld == null) { if (DBG) Console.WriteLine("[CDEC]   fail: no byte[] stsfld after init"); return false; }
            dField = (FieldDef)stsfld.Operand;

            iEnd = instructions.IndexOf(stsfld);
            // Validate this really is the constant initializer by confirming the LZMA
            // decompressor is invoked before the store (the pool is fed through it).
            // Don't require strict adjacency — control-flow deobfuscation leaves temporaries.
            bool callsLzma = false;
            for (int k = 0; k < iEnd; k++) {
                var oc = instructions[k].OpCode;
                if ((oc == OpCodes.Call || oc == OpCodes.Callvirt) && instructions[k].Operand == _lzmaMethod) {
                    callsLzma = true;
                    break;
                }
            }
            if (!callsLzma) { if (DBG) Console.WriteLine("[CDEC]   fail: lzma method not called before byte[] store"); return false; }

            return true;
        }

        // ConfuserEx normal-mode constant decryption. The obfuscated init method builds a
        // uint[] constant pool (RVA field, passed here as bytes), derives a 16-word key from
        // an xorshift RNG seed, then XOR-decrypts the pool with a running key feedback and
        // LZMA-decompresses the result. Rather than clone/patch/JIT-invoke the (control-flow
        // obfuscated) method — which is fragile and mis-decrypts on inlined variants — we run
        // the algorithm directly; only the RNG seed varies per build and is read from the IL.
        private byte[] DecryptArray(MethodDef method, byte[] encryptedArray, int iStart, int iEnd)
        {
            uint seed = ExtractSeed(method);

            int n = encryptedArray.Length / 4;
            var pool = new uint[n];
            Buffer.BlockCopy(encryptedArray, 0, pool, 0, n * 4);

            var key = new uint[16];
            uint s = seed;
            for (int i = 0; i < 16; i++) {
                s ^= s >> 12;
                s ^= s << 25;
                s ^= s >> 27;
                key[i] = s;
            }

            var outBytes = new byte[n * 4];
            int o = 0;
            for (int j = 0; j < n; j += 16) {
                for (int l = 0; l < 16 && j + l < n; l++) {
                    uint v = pool[j + l] ^ key[l];
                    outBytes[o++] = (byte)v;
                    outBytes[o++] = (byte)(v >> 8);
                    outBytes[o++] = (byte)(v >> 16);
                    outBytes[o++] = (byte)(v >> 24);
                    key[l] ^= v;
                }
            }

            if (System.Environment.GetEnvironmentVariable("DBG_CDEC") == "1")
                Console.WriteLine($"[DBG] seed={seed} n={n} isNewLzma={_isNewLzma} out16=" +
                    string.Join(" ", outBytes.Take(16).Select(b => b.ToString("X2"))));

            return Lzma.Decompress(outBytes, _isNewLzma);
        }

        // The xorshift seed is the constant assigned to the RNG state just before the
        // 16-iteration key-schedule loop that mixes it with shifts of 12, 25 and 27.
        private static uint ExtractSeed(MethodDef method)
        {
            var ins = method.Body.Instructions;
            var locals = method.Body.Variables;

            int rngIdx = -1;
            for (int i = 0; i < ins.Count; i++) {
                if (!ins[i].IsLdcI4() || ins[i].GetLdcI4Value() != 12)
                    continue;
                bool has25 = false, has27 = false;
                for (int j = i; j < System.Math.Min(ins.Count, i + 40); j++) {
                    if (!ins[j].IsLdcI4()) continue;
                    int v = ins[j].GetLdcI4Value();
                    if (v == 25) has25 = true;
                    if (v == 27) has27 = true;
                }
                if (has25 && has27) { rngIdx = i; break; }
            }
            if (rngIdx < 0)
                throw new Exception("ConfuserEx constant xorshift RNG not found");

            // The RNG state local is the one loaded immediately before "ldc.i4 12".
            Local seedLocal = null;
            for (int i = rngIdx - 1; i >= 0; i--) {
                if (ins[i].IsLdloc()) { seedLocal = ins[i].GetLocal(locals); break; }
            }
            if (seedLocal == null)
                throw new Exception("ConfuserEx constant RNG state local not found");

            // Its initializer: "ldc.i4 <seed> ; stloc seedLocal" before the RNG.
            for (int i = rngIdx - 1; i >= 1; i--) {
                if (ins[i].IsStloc() && ins[i].GetLocal(locals) == seedLocal && ins[i - 1].IsLdcI4())
                    return (uint)ins[i - 1].GetLdcI4Value();
            }
            throw new Exception("ConfuserEx constant RNG seed not found");
        }

        public void RemoveInlinedInitCode() {
	        if (!MethodIsInlined)
		        return;

	        var instructions = Method.Body.Instructions;

	        var iStart = FindDecrypterStart(instructions);
	        if (iStart == -1)
		        throw new Exception("Decryption start was found earlier but not anymore");

	        var stsfld = instructions.First(ins => ins.OpCode == OpCodes.Stsfld && ins.Operand == _decryptedField);
	        var target = instructions[instructions.IndexOf(stsfld) + 1];

	        instructions[iStart].OpCode = OpCodes.Br;
	        instructions[iStart].Operand = target;

	        var blocks = new Blocks(Method);
	        blocks.RemoveDeadBlocks();
	        blocks.GetCode(out var allInstructions, out var allExceptionHandlers);
	        DotNetUtils.RestoreBody(Method, allInstructions, allExceptionHandlers);
        }

        private IEnumerable<ConstantDecrypterBase> FindStringDecrypters(TypeDef type)
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                    continue;
                if (!method.Signature.ContainsGenericParameter)
                    continue;
                var sig = method.MethodSig;
                if (sig?.Params.Count != 1)
                    continue;
                if (sig.Params[0].GetElementType() is not (ElementType.U4 or ElementType.I4))
                    continue;
                if (sig.RetType.RemovePinnedAndModifiers() is not GenericMVar)
                    continue;
                if (sig.GenParamCount != 1)
                    continue;

                _deobfuscator.Deobfuscate(method, SimpleDeobfuscatorFlags.Force);

                if (DBG) Console.WriteLine($"[CDEC] decrypter candidate {method.Name}: {method.Body.Instructions.Count} instrs, native={IsNativeStringDecrypter(method, out _)}, normal={IsNormalStringDecrypter(method, out _, out _)}");

                if (IsNativeStringDecrypter(method, out MethodDef nativeMethod))
                {
                    yield return new ConstantDecrypterBase
                    {
                        Decrypted = _decryptedBytes,
                        Method = method,
                        NativeMethod = nativeMethod
                    };
                }
                if (IsNormalStringDecrypter(method, out int num1, out int num2))
                {
                    yield return new ConstantDecrypterBase
                    {
                        Decrypted = _decryptedBytes,
                        Method = method,
                        Num1 = (uint)num1,
                        Num2 = (uint)num2
                    };
                }
            }
        }

        private bool IsNormalStringDecrypter(MethodDef method, out int num1, out int num2)
        {
            num1 = 0;
            num2 = 0;
            var instr = method.Body.Instructions;
            if (instr.Count < 25)
                return false;

            var i = 0;
            if (instr[0].OpCode == OpCodes.Call && instr[0].Operand.ToString() ==
					"System.Reflection.Assembly System.Reflection.Assembly::GetExecutingAssembly()")
	            i = 8; // Skip Assembly.GetExecutingAssembly().Equals(Assembly.GetCallingAssembly()) check

            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4())
                return false;
            num1 = (int)instr[i++].Operand;
            if (instr[i++].OpCode != OpCodes.Mul)
                return false;
            if (!instr[i].IsLdcI4())
                return false;
            num2 = (int)instr[i++].Operand;
            if (instr[i++].OpCode != OpCodes.Xor)
                return false;

            if (!instr[i++].IsStarg()) //uint_0 = (uint_0 * 2857448701u ^ 1196001109u);
                return false;

            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4() || instr[i++].GetLdcI4Value() != 0x1E)
                return false;
            if (instr[i++].OpCode != OpCodes.Shr_Un)
                return false;
            if (!instr[i++].IsStloc()) //uint num = uint_0 >> 30;
                return false;

            foreach (var mtd in _strDecryptCalledMethods)
                if (!DotNetUtils.CallsMethod(method, mtd))
                    return false;
            //TODO: Implement
            //if (!DotNetUtils.LoadsField(method, decryptedField))
            //    return;
            return true;
        }

        private bool IsNativeStringDecrypter(MethodDef method, out MethodDef nativeMethod)
        {
            nativeMethod = null;
            var instr = method.Body.Instructions;
            if (instr.Count < 25)
                return false;

            var i = 0;

            if (!instr[i++].IsLdarg())
                return false;

            if (instr[i].OpCode != OpCodes.Call)
                return false;

            nativeMethod = instr[i++].Operand as MethodDef;

            if (nativeMethod == null || !nativeMethod.IsStatic || !nativeMethod.IsNative)
                return false;
            if (!DotNetUtils.IsMethod(nativeMethod, "System.Int32", "(System.Int32)"))
                return false;

            if (!instr[i++].IsStarg()) //uint_0 = (uint_0 * 2857448701u ^ 1196001109u);
                return false;

            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4() || instr[i++].GetLdcI4Value() != 0x1E)
                return false;
            if (instr[i++].OpCode != OpCodes.Shr_Un)
                return false;
            if (!instr[i++].IsStloc()) //uint num = uint_0 >> 30;
                return false;
            i++;
            //TODO: Implement
            //if (!instr[10].IsLdloca())
            //    return;
            if (instr[i++].OpCode != OpCodes.Initobj)
                return false;
            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4() || instr[i++].GetLdcI4Value() != 0x3FFFFFFF)
                return false;
            if (instr[i++].OpCode != OpCodes.And)
                return false;
            if (!instr[i++].IsStarg()) //uint_0 &= 1073741823u;
                return false;

            if (!instr[i++].IsLdarg())
                return false;
            if (!instr[i].IsLdcI4() || instr[i++].GetLdcI4Value() != 2)
                return false;
            if (instr[i++].OpCode != OpCodes.Shl)
                return false;
            if (!instr[i++].IsStarg()) //uint_0 <<= 2;
                return false;

            foreach (var mtd in _strDecryptCalledMethods)
                if (!DotNetUtils.CallsMethod(method, mtd))
                    return false;
            //TODO: Implement
            //if (!DotNetUtils.LoadsField(method, decryptedField))
            //    return;
            return true;
        }

        private static bool VerifyGenericArg(MethodSpec gim, ElementType etype)
        {
            var gims = gim?.GenericInstMethodSig;
            if (gims == null || gims.GenericArguments.Count != 1)
                return false;
            return gims.GenericArguments[0].GetElementType() == etype;
        }

        private static uint CastMagicParam(object param) => param switch {
	        uint u => u,
	        int i => unchecked((uint)i),
	        _ => throw new InvalidCastException("Expected Int32 or UInt32")
        };

        public string DecryptString(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.String))
                return null;
            return info.DecryptString(CastMagicParam(magic1));
        }

        public object DecryptSByte(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I1))
                return null;
            return info.DecryptConstant<sbyte>(CastMagicParam(magic1));
        }

        public object DecryptByte(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U1))
                return null;
            return info.DecryptConstant<byte>(CastMagicParam(magic1));
        }

        public object DecryptInt16(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I2))
                return null;
            return info.DecryptConstant<short>(CastMagicParam(magic1));
        }

        public object DecryptUInt16(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U2))
                return null;
            return info.DecryptConstant<ushort>(CastMagicParam(magic1));
        }

        public object DecryptInt32(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I4))
                return null;
            return info.DecryptConstant<int>(CastMagicParam(magic1));
        }

        public object DecryptUInt32(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U4))
                return null;
            return info.DecryptConstant<uint>(CastMagicParam(magic1));
        }

        public object DecryptInt64(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.I8))
                return null;
            return info.DecryptConstant<long>(CastMagicParam(magic1));
        }

        public object DecryptUInt64(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.U8))
                return null;
            return info.DecryptConstant<ulong>(CastMagicParam(magic1));
        }

        public object DecryptSingle(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.R4))
                return null;
            return info.DecryptConstant<float>(CastMagicParam(magic1));
        }

        public object DecryptDouble(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.R8))
                return null;
            return info.DecryptConstant<double>(CastMagicParam(magic1));
        }

        public object DecryptArray(ConstantDecrypterBase info, MethodSpec gim, object magic1)
        {
            if (!VerifyGenericArg(gim, ElementType.SZArray))
                return null;
            return info.DecryptArray(CastMagicParam(magic1));
        }
    }
}

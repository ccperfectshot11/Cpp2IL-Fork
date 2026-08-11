using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public class LzmaFinder
    {
        private readonly ISimpleDeobfuscator _deobfuscator;

        private readonly ModuleDef _module;

        public LzmaFinder(ModuleDef module, ISimpleDeobfuscator deobfuscator)
        {
            this._module = module;
            this._deobfuscator = deobfuscator;
        }

        public MethodDef Method { get; private set; }

        public List<TypeDef> Types { get; } = new List<TypeDef>();

        public bool FoundLzma => Method != null && Types.Count != 0;

        public bool IsNewSizeCode { get; private set; }

        public void Find()
        {
            var moduleType = DotNetUtils.GetModuleType(_module);
            if (moduleType == null)
                return;
            foreach (var method in moduleType.Methods)
            {
                if (!method.HasBody || !method.IsStatic)
                    continue;
                if (!DotNetUtils.IsMethod(method, "System.Byte[]", "(System.Byte[])"))
                    continue;
                _deobfuscator.Deobfuscate(method, SimpleDeobfuscatorFlags.Force);
                if (!IsLzmaMethod(method))
                    continue;
                Method = method;
                // The LZMA decoder is a nested type constructed inside this method.
                // Find its ctor structurally instead of assuming a fixed instruction index.
                var decoderType = FindDecoderType(method);
                if (decoderType != null)
                    ExtractNestedTypes(decoderType);
            }
        }

        // Structural (version-tolerant) recognizer for the ConfuserEx / Confuser.Core
        // constant+resource LZMA decompression helper: static byte[](byte[]) that
        //   1. wraps the input in a MemoryStream,
        //   2. builds a 5-byte LZMA "properties" buffer and reads it from the stream,
        //   3. constructs a nested LZMA decoder type and invokes it,
        //   4. reads the uncompressed size (4-byte int on Confuser.Core "new" builds,
        //      8-byte long on older ConfuserEx builds).
        // Rather than walk exact instruction offsets (which break under control-flow
        // obfuscation and across versions), we look for these markers anywhere in the body.
        private bool IsLzmaMethod(MethodDef method)
        {
            var instrs = method.Body.Instructions;
            if (instrs.Count < 20)
                return false;

            // 1. MemoryStream(byte[]) wrapping the input.
            if (!instrs.Any(x => x.OpCode == OpCodes.Newobj &&
                    x.Operand?.ToString() == "System.Void System.IO.MemoryStream::.ctor(System.Byte[])"))
                return false;

            // 2. 5-byte properties buffer: "ldc.i4.5 ; newarr System.Byte".
            bool hasProps = false;
            for (int i = 0; i + 1 < instrs.Count; i++) {
                if (instrs[i].IsLdcI4() && instrs[i].GetLdcI4Value() == 5 &&
                    instrs[i + 1].OpCode == OpCodes.Newarr &&
                    instrs[i + 1].Operand?.ToString() == "System.Byte") {
                    hasProps = true;
                    break;
                }
            }
            if (!hasProps)
                return false;

            // 3. reads the properties out of the stream.
            if (!instrs.Any(x => x.OpCode == OpCodes.Callvirt &&
                    x.Operand?.ToString() == "System.Int32 System.IO.Stream::Read(System.Byte[],System.Int32,System.Int32)"))
                return false;

            // 4. constructs a nested decoder type (the LZMA decoder class).
            if (FindDecoderType(method) == null)
                return false;

            // Size encoding: Confuser.Core "new" builds read a 4-byte int size — detect a
            // Stream::Read (or ReadByte) whose count operand is 4 (ldc.i4.4 shortly before a
            // Read). Older ConfuserEx builds read an 8-byte long, so this marker is absent.
            IsNewSizeCode = DetectIntSizeRead(instrs);
            return true;
        }

        // True if the uncompressed size is read as a 4-byte int (Confuser.Core "new" builds).
        private static bool DetectIntSizeRead(IList<Instruction> instrs)
        {
            for (int i = 0; i < instrs.Count; i++) {
                if (!(instrs[i].OpCode == OpCodes.Callvirt &&
                      instrs[i].Operand?.ToString() == "System.Int32 System.IO.Stream::Read(System.Byte[],System.Int32,System.Int32)"))
                    continue;
                // look back a few instructions for the read count "4".
                for (int j = i - 1; j >= 0 && j >= i - 5; j--) {
                    if (instrs[j].IsLdcI4() && instrs[j].GetLdcI4Value() == 4)
                        return true;
                }
            }
            return false;
        }

        // Finds the nested LZMA decoder type constructed inside the method body.
        private static TypeDef FindDecoderType(MethodDef method)
        {
            foreach (var inst in method.Body.Instructions) {
                if (inst.OpCode != OpCodes.Newobj)
                    continue;
                if (inst.Operand is not MethodDef ctor)
                    continue;
                var dt = ctor.DeclaringType;
                if (dt == null || !dt.IsNested)
                    continue;
                // skip framework value-type ctors etc.; the decoder is a module-nested class.
                if (dt.FullName.Contains("MemoryStream"))
                    continue;
                return dt;
            }
            return null;
        }

        private void ExtractNestedTypes(TypeDef type)
        {
            foreach (var method in type.Methods)
                if (method.HasBody)
                {
                    var instr = method.Body.Instructions;
                    foreach (var inst in instr)
                        if (inst.Operand is MethodDef)
                        {
                            var ntype = (inst.Operand as MethodDef).DeclaringType;
                            if (!ntype.IsNested)
                                continue;
                            if (Types.Contains(ntype))
                                continue;
                            Types.Add(ntype);
                            ExtractNestedTypes(ntype);
                        }
                }
        }
    }
}

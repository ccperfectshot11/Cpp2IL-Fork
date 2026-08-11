/*
    ConfuserEx AntiTamper remover.

    ConfuserEx AntiTamper injects into <Module> an Initialize method (called first
    from <Module>::.cctor) that decrypts the real method bodies at runtime and spins
    up a background integrity-check thread. When an assembly is dumped after those
    bodies have been decrypted in memory, the AntiTamper machinery itself is left
    behind as dead code. Its integrity-check thread body in particular keeps an
    unparseable (encrypted) IL body which dnlib cannot decode, so it reaches the
    metadata writer as garbage and produces "Error calculating max stack value /
    Instruction is null" errors during BeginWriteMethodBodies.

    This pass locates the <Module> methods whose bodies are invalid, neutralizes them
    so they can never reach the writer as garbage, removes the calls that reference
    them (including the Initialize call in <Module>::.cctor) and marks them for
    removal.
*/

using System.Collections.Generic;
using de4dot.blocks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.ConfuserEx
{
	internal class AntiTamperRemover
	{
		private readonly ModuleDefMD _module;
		private readonly List<MethodDef> _corruptMethods = new List<MethodDef>();

		public AntiTamperRemover(ModuleDefMD module)
		{
			_module = module;
		}

		public bool Detected => _corruptMethods.Count > 0;
		public IEnumerable<MethodDef> Methods => _corruptMethods;

		// True if the IL body could not be decoded by dnlib, i.e. it contains unknown
		// opcodes or operands that are required but missing. A legitimate method never
		// looks like this; only encrypted/garbage AntiTamper remnants do.
		private static bool HasInvalidBody(CilBody body)
		{
			foreach (var instr in body.Instructions)
			{
				var code = instr.OpCode.Code;
				if (code == Code.UNKNOWN1 || code == Code.UNKNOWN2)
					return true;

				switch (instr.OpCode.OperandType)
				{
					case OperandType.InlineBrTarget:
					case OperandType.ShortInlineBrTarget:
					case OperandType.InlineSwitch:
					case OperandType.InlineField:
					case OperandType.InlineMethod:
					case OperandType.InlineTok:
					case OperandType.InlineType:
					case OperandType.InlineSig:
					case OperandType.InlineString:
						if (instr.Operand == null)
							return true;
						break;
				}
			}
			return false;
		}

		public void Find()
		{
			var moduleType = DotNetUtils.GetModuleType(_module);
			if (moduleType == null)
				return;

			// Scan <Module> and its nested helper types (Struct0/Class0/... injected by
			// the AntiTamper native block). Real code never carries unparseable IL, so it
			// is safe to look at every method reachable from the module type.
			FindInType(moduleType);
		}

		private void FindInType(TypeDef type)
		{
			foreach (var method in type.Methods)
			{
				if (method.Body == null || !method.Body.HasInstructions)
					continue;
				if (HasInvalidBody(method.Body))
					_corruptMethods.Add(method);
			}
			foreach (var nested in type.NestedTypes)
				FindInType(nested);
		}

		// Replace a corrupt body with a minimal valid one so the writer can never see
		// the garbage IL, even if some reference to the method survives removal.
		private static void NeutralizeBody(MethodDef method)
		{
			var body = new CilBody();
			var retType = method.MethodSig?.RetType.RemovePinnedAndModifiers();
			if (retType != null && retType.ElementType != ElementType.Void)
			{
				if (retType.IsValueType)
				{
					var local = new Local(retType);
					body.Variables.Add(local);
					body.InitLocals = true;
					body.Instructions.Add(OpCodes.Ldloca_S.ToInstruction(local));
					body.Instructions.Add(OpCodes.Initobj.ToInstruction(retType.ToTypeDefOrRef()));
					body.Instructions.Add(OpCodes.Ldloc_0.ToInstruction());
				}
				else
				{
					body.Instructions.Add(OpCodes.Ldnull.ToInstruction());
				}
			}
			body.Instructions.Add(OpCodes.Ret.ToInstruction());
			body.UpdateInstructionOffsets();
			method.Body = body;
		}

		// Number of call/ldftn references to a method across the whole module. Used to
		// decide whether a neutralized AntiTamper method can also be safely removed:
		// removing a still-referenced method would leave a dangling operand (and, for
		// ldftn feeding a delegate/thread ctor, an unbalanced stack in the caller).
		private int ReferenceCount(MethodDef target)
		{
			int count = 0;
			foreach (var type in _module.GetTypes())
			{
				foreach (var method in type.Methods)
				{
					if (method.Body == null)
						continue;
					foreach (var instr in method.Body.Instructions)
						if (instr.Operand == target)
							count++;
				}
			}
			return count;
		}

		// Neutralize every corrupt body so nothing garbage reaches the writer, and
		// return the subset that is safe to remove outright (no surviving references).
		// Referenced methods stay as valid no-ops.
		public IEnumerable<MethodDef> Fix()
		{
			var removable = new List<MethodDef>();
			if (!Detected)
				return removable;

			foreach (var method in _corruptMethods)
			{
				bool unreferenced = ReferenceCount(method) == 0;
				NeutralizeBody(method);
				if (unreferenced)
					removable.Add(method);
			}
			return removable;
		}
	}
}

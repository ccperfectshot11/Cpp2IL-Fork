/*
    NetSpy addition to de4dot.

    Spices.Net (9Rays.Net) v5 randomizes its string-data transform on every obfuscation run
    (different offsets, XOR addends, whether the data is QuickLZ-compressed, whether the public
    key is reversed, etc.). Rather than pattern-match a fixed formula, this tiny IL interpreter
    executes the assembly's own transform method (a pure byte[] -> byte[] function) against the
    embedded encrypted data, reproducing whatever variant that build used.

    Only the small opcode/method set those transforms use is implemented. The one heavy helper,
    the QuickLZ decompressor core, is not interpreted: calls to a (byte[],int,byte[],int)->void
    method are routed to de4dot's own QuickLZBase (QuickLZ is a fixed algorithm), and the thin
    (byte[],byte[])->int framing helper around it is interpreted like everything else.
*/

using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.Spices_Net {
	class Spices5Emulator {
		readonly ModuleDefMD module;
		readonly byte[] publicKey;

		// sentinels for the reflection chain GetExecutingAssembly().GetName().GetPublicKey()
		static readonly object AsmSentinel = new object();
		static readonly object AsmNameSentinel = new object();

		public Spices5Emulator(ModuleDefMD module) {
			this.module = module;
			publicKey = module.Assembly?.PublicKey?.Data ?? Array.Empty<byte>();
		}

		public byte[] Run(MethodDef transform, byte[] input) {
			var result = Execute(transform, new object[] { input }, 0);
			return result as byte[];
		}

		object Execute(MethodDef method, object[] args, int depth) {
			if (depth > 20 || method?.Body == null)
				throw new NotSupportedException("Spices5 emulator: bad method");
			var instrs = method.Body.Instructions;
			var offToIdx = new Dictionary<uint, int>(instrs.Count);
			for (int i = 0; i < instrs.Count; i++)
				offToIdx[instrs[i].Offset] = i;

			var locals = new object[method.Body.Variables.Count];
			var stack = new Stack<object>();
			long guard = 0;
			int pc = 0;
			while (pc < instrs.Count) {
				if (++guard > 200_000_000L)
					throw new NotSupportedException("Spices5 emulator: guard tripped");
				var ins = instrs[pc];
				var code = ins.OpCode.Code;
				switch (code) {
				case Code.Nop: case Code.Castclass: case Code.Box: case Code.Unbox_Any: break;
				case Code.Ldarg_0: stack.Push(args[0]); break;
				case Code.Ldarg_1: stack.Push(args[1]); break;
				case Code.Ldarg_2: stack.Push(args[2]); break;
				case Code.Ldarg_3: stack.Push(args[3]); break;
				case Code.Ldarg: case Code.Ldarg_S: stack.Push(args[((Parameter)ins.Operand).Index]); break;
				case Code.Ldloc_0: stack.Push(locals[0]); break;
				case Code.Ldloc_1: stack.Push(locals[1]); break;
				case Code.Ldloc_2: stack.Push(locals[2]); break;
				case Code.Ldloc_3: stack.Push(locals[3]); break;
				case Code.Ldloc: case Code.Ldloc_S: stack.Push(locals[((Local)ins.Operand).Index]); break;
				case Code.Stloc_0: locals[0] = stack.Pop(); break;
				case Code.Stloc_1: locals[1] = stack.Pop(); break;
				case Code.Stloc_2: locals[2] = stack.Pop(); break;
				case Code.Stloc_3: locals[3] = stack.Pop(); break;
				case Code.Stloc: case Code.Stloc_S: locals[((Local)ins.Operand).Index] = stack.Pop(); break;
				case Code.Ldc_I4: case Code.Ldc_I4_S: stack.Push(ins.GetLdcI4Value()); break;
				case Code.Ldc_I4_0: stack.Push(0); break;
				case Code.Ldc_I4_1: stack.Push(1); break;
				case Code.Ldc_I4_2: stack.Push(2); break;
				case Code.Ldc_I4_3: stack.Push(3); break;
				case Code.Ldc_I4_4: stack.Push(4); break;
				case Code.Ldc_I4_5: stack.Push(5); break;
				case Code.Ldc_I4_6: stack.Push(6); break;
				case Code.Ldc_I4_7: stack.Push(7); break;
				case Code.Ldc_I4_8: stack.Push(8); break;
				case Code.Ldc_I4_M1: stack.Push(-1); break;
				case Code.Dup: stack.Push(stack.Peek()); break;
				case Code.Pop: stack.Pop(); break;
				case Code.Newarr: stack.Push(new byte[ToInt(stack.Pop())]); break;
				case Code.Ldlen: stack.Push(((Array)stack.Pop()).Length); break;
				case Code.Ldelem_U1: case Code.Ldelem_I1: case Code.Ldelem_U2: case Code.Ldelem_I2:
				case Code.Ldelem_U4: case Code.Ldelem_I4: {
					int idx = ToInt(stack.Pop());
					var arr = (Array)stack.Pop();
					object v = arr.GetValue(idx);
					int iv = v is byte b ? b : v is sbyte sb ? sb : Convert.ToInt32(v);
					if (code == Code.Ldelem_I1) iv = (sbyte)iv;
					stack.Push(iv);
					break;
				}
				case Code.Stelem_I1: case Code.Stelem_I2: case Code.Stelem_I4: {
					int val = ToInt(stack.Pop());
					int idx = ToInt(stack.Pop());
					var arr = (Array)stack.Pop();
					if (arr is byte[] ba) ba[idx] = (byte)val;
					else arr.SetValue(val, idx);
					break;
				}
				case Code.Add: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a + b); break; }
				case Code.Sub: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a - b); break; }
				case Code.Mul: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a * b); break; }
				case Code.Div: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a / b); break; }
				case Code.Rem: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a % b); break; }
				case Code.And: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a & b); break; }
				case Code.Or: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a | b); break; }
				case Code.Xor: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a ^ b); break; }
				case Code.Shl: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a << b); break; }
				case Code.Shr: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a >> b); break; }
				case Code.Shr_Un: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push((int)((uint)a >> b)); break; }
				case Code.Neg: stack.Push(-ToInt(stack.Pop())); break;
				case Code.Not: stack.Push(~ToInt(stack.Pop())); break;
				case Code.Conv_U1: stack.Push((int)(byte)ToInt(stack.Pop())); break;
				case Code.Conv_I1: stack.Push((int)(sbyte)ToInt(stack.Pop())); break;
				case Code.Conv_U2: stack.Push((int)(ushort)ToInt(stack.Pop())); break;
				case Code.Conv_I2: stack.Push((int)(short)ToInt(stack.Pop())); break;
				case Code.Conv_I4: case Code.Conv_U4: case Code.Conv_I8: case Code.Conv_U8: stack.Push(ToInt(stack.Pop())); break;
				case Code.Br: case Code.Br_S: pc = offToIdx[((Instruction)ins.Operand).Offset]; continue;
				case Code.Brtrue: case Code.Brtrue_S: if (ToInt(stack.Pop()) != 0) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break;
				case Code.Brfalse: case Code.Brfalse_S: if (ToInt(stack.Pop()) == 0) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break;
				case Code.Beq: case Code.Beq_S: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); if (a == b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Bne_Un: case Code.Bne_Un_S: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); if (a != b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Blt: case Code.Blt_S: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); if (a < b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Blt_Un: case Code.Blt_Un_S: { uint b = (uint)ToInt(stack.Pop()), a = (uint)ToInt(stack.Pop()); if (a < b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Bgt: case Code.Bgt_S: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); if (a > b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Bgt_Un: case Code.Bgt_Un_S: { uint b = (uint)ToInt(stack.Pop()), a = (uint)ToInt(stack.Pop()); if (a > b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Ble: case Code.Ble_S: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); if (a <= b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Ble_Un: case Code.Ble_Un_S: { uint b = (uint)ToInt(stack.Pop()), a = (uint)ToInt(stack.Pop()); if (a <= b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Bge: case Code.Bge_S: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); if (a >= b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Bge_Un: case Code.Bge_Un_S: { uint b = (uint)ToInt(stack.Pop()), a = (uint)ToInt(stack.Pop()); if (a >= b) { pc = offToIdx[((Instruction)ins.Operand).Offset]; continue; } break; }
				case Code.Ceq: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a == b ? 1 : 0); break; }
				case Code.Clt: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a < b ? 1 : 0); break; }
				case Code.Clt_Un: { uint b = (uint)ToInt(stack.Pop()), a = (uint)ToInt(stack.Pop()); stack.Push(a < b ? 1 : 0); break; }
				case Code.Cgt: { int b = ToInt(stack.Pop()), a = ToInt(stack.Pop()); stack.Push(a > b ? 1 : 0); break; }
				case Code.Cgt_Un: { uint b = (uint)ToInt(stack.Pop()), a = (uint)ToInt(stack.Pop()); stack.Push(a > b ? 1 : 0); break; }
				case Code.Call: case Code.Callvirt: HandleCall(ins.Operand as IMethod, stack, depth); break;
				case Code.Ret: return method.MethodSig.RetType.GetElementType() == ElementType.Void ? null : stack.Pop();
				default: throw new NotSupportedException("Spices5 emulator: opcode " + ins.OpCode);
				}
				pc++;
			}
			return null;
		}

		void HandleCall(IMethod m, Stack<object> stack, int depth) {
			if (m == null)
				throw new NotSupportedException("Spices5 emulator: null call");
			var declName = m.DeclaringType?.FullName;
			var name = m.Name?.String;

			// System.Array::Reverse(Array)
			if (declName == "System.Array" && name == "Reverse") {
				var a = (Array)stack.Pop();
				Array.Reverse(a);
				return;
			}
			// GetExecutingAssembly().GetName().GetPublicKey() chain -> the assembly's public key
			if (name == "GetExecutingAssembly") { stack.Push(AsmSentinel); return; }
			if (name == "GetName") { stack.Pop(); stack.Push(AsmNameSentinel); return; }
			if (name == "GetPublicKey" || name == "GetPublicKeyToken") {
				stack.Pop();
				stack.Push((byte[])publicKey.Clone());
				return;
			}
			// BitConverter::ToUInt32/ToInt32(byte[], int)
			if (declName == "System.BitConverter" && (name == "ToUInt32" || name == "ToInt32")) {
				int idx = ToInt(stack.Pop());
				var arr = (byte[])stack.Pop();
				stack.Push((int)BitConverter.ToUInt32(arr, idx));
				return;
			}
			// Array::Copy / Buffer::BlockCopy(src, srcOff, dst, dstOff, count)
			if ((declName == "System.Array" && name == "Copy") || (declName == "System.Buffer" && name == "BlockCopy")) {
				int count = ToInt(stack.Pop());
				int dstOff = ToInt(stack.Pop());
				var dst = (Array)stack.Pop();
				int srcOff = ToInt(stack.Pop());
				var src = (Array)stack.Pop();
				Array.Copy(src, srcOff, dst, dstOff, count);
				return;
			}

			var md = m as MethodDef;
			if (md == null || md.MethodSig == null)
				throw new NotSupportedException("Spices5 emulator: extern call " + m.FullName);

			// The QuickLZ decompressor core: (byte[] src, int srcOff, byte[] dst, int dstOff) -> void.
			// Route to de4dot's fixed QuickLZ (dstOff is always 0 here).
			var sig = md.MethodSig;
			if (sig.RetType.GetElementType() == ElementType.Void && sig.Params.Count == 4 &&
				IsByteArray(sig.Params[0]) && sig.Params[1].GetElementType() == ElementType.I4 &&
				IsByteArray(sig.Params[2]) && sig.Params[3].GetElementType() == ElementType.I4) {
				stack.Pop(); // dstOff (0)
				var dst = (byte[])stack.Pop();
				int srcOff = ToInt(stack.Pop());
				var src = (byte[])stack.Pop();
				QuickLZBase.Decompress(src, srcOff, dst);
				return;
			}

			// Otherwise it's an in-assembly helper (e.g. the framing (byte[],byte[])->int): recurse.
			int n = sig.Params.Count;
			var callArgs = new object[n];
			for (int i = n - 1; i >= 0; i--)
				callArgs[i] = stack.Pop();
			var ret = Execute(md, callArgs, depth + 1);
			if (sig.RetType.GetElementType() != ElementType.Void)
				stack.Push(ret);
		}

		static bool IsByteArray(TypeSig t) => t != null && t.FullName == "System.Byte[]";

		static int ToInt(object o) {
			switch (o) {
			case int i: return i;
			case byte b: return b;
			case sbyte sb: return sb;
			case bool bo: return bo ? 1 : 0;
			case null: return 0;
			default: return Convert.ToInt32(o);
			}
		}
	}
}

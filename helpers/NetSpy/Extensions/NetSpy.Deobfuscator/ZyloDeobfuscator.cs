/*
    Copyright (C) 2014-2026 de4dot@gmail.com

    This file is part of NetSpy / NetSpy

    NetSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    NetSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with NetSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace NetSpy.Deobfuscator {
	/// <summary>
	/// Dedicated cleaner for the "Zylofuscator" obfuscator, which de4dot does not
	/// recognize. Operates directly on a dnlib module: undoes integer mutation,
	/// inlines string proxies, decrypts injected strings and un-flattens control flow.
	/// Ported from the standalone Zylo deobfuscator.
	/// </summary>
	sealed class ZyloDeobfuscator {
		readonly ModuleDefMD mod;
		MethodDef? decoder;
		int nMut, nProxyInline, nProxyDel, nStrDec, nCfOk;

		ZyloDeobfuscator(ModuleDefMD mod) => this.mod = mod;

		/// <summary>Heuristically decide whether a module was protected with Zylofuscator.</summary>
		public static bool IsZylo(ModuleDefMD mod) {
			try {
				return HasZyloDecoder(mod) || CountFlattenedMethods(mod) >= 2;
			}
			catch {
				return false;
			}
		}

		/// <summary>Loads, cleans and writes the module. Returns true on success.</summary>
		public static bool Run(string inputPath, string outputPath) {
			var mod = ModuleDefMD.Load(inputPath);
			try {
				new ZyloDeobfuscator(mod).Deobfuscate();
				var opts = new ModuleWriterOptions(mod);
				opts.MetadataOptions.Flags &= ~MetadataFlags.KeepOldMaxStack;
				opts.Logger = DummyLogger.NoThrow;
				mod.Write(outputPath, opts);
				return true;
			}
			finally {
				mod.Dispose();
			}
		}

		void Deobfuscate() {
			// A0: inline int-proxy methods (0-arg static -> ldc.i4; ret). Zylo hides the switch-key
			// constants that drive its control flow behind these proxies, so they must be inlined
			// before the int-mutation fold and control-flow un-flatten can see the real constants.
			InlineIntProxies();
			// A: undo IntV2 integer-mutation (constant-fold Abs/neg/Min/Max chains)
			foreach (var m in AllMethods())
				UndoIntMutation(m);
			// B: inline ProxyString methods (0-arg static -> ldstr; ret)
			InlineStringProxies();
			// C: decrypt injected string-decoder chains
			FindDecoder();
			if (decoder is not null)
				foreach (var m in AllMethods())
					DecryptStrings(m);
			// D: un-flatten Zylo control flow
			foreach (var m in AllMethods().ToList())
				UndoControlFlow(m);
			// E: remove the injected decoder from <Module>
			if (decoder is not null && decoder.DeclaringType is not null)
				decoder.DeclaringType.Methods.Remove(decoder);
			// Recompute maxstack / shrink macros
			foreach (var m in AllMethods()) {
				if (m.Body is null)
					continue;
				try {
					m.Body.OptimizeMacros();
					m.Body.OptimizeBranches();
				}
				catch { }
			}
		}

		IEnumerable<MethodDef> AllMethods() =>
			mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody);

		// ---------- detection helpers ----------
		static bool HasZyloDecoder(ModuleDefMD mod) {
			var gt = mod.GlobalType;
			if (gt is null)
				return false;
			foreach (var m in gt.Methods) {
				var s = m.MethodSig;
				if (s is not null && s.RetType?.FullName == "System.String" && s.Params.Count == 6 &&
					s.Params[0].FullName == "System.String" &&
					s.Params.Skip(1).All(p => p.FullName == "System.Int32"))
					return true;
			}
			return false;
		}

		static int CountFlattenedMethods(ModuleDefMD mod) {
			int count = 0;
			foreach (var t in mod.GetTypes()) {
				foreach (var m in t.Methods) {
					if (!m.HasBody)
						continue;
					var ins = m.Body.Instructions;
					if (ins.Count >= 6 && ins[0].IsLdcI4() && ins[0].GetLdcI4Value() == 0 &&
						IsStloc(ins[1], out _) &&
						(ins[2].OpCode.Code == Code.Br || ins[2].OpCode.Code == Code.Br_S) &&
						ins[3].OpCode.Code == Code.Nop)
						count++;
				}
			}
			return count;
		}

		// ---------- Pass A: int mutation ----------
		static bool IsMathCall(Instruction i, string name, int pcount) {
			if (i.OpCode.Code != Code.Call)
				return false;
			var m = i.Operand as IMethod;
			return m is not null && m.Name == name && m.DeclaringType?.FullName == "System.Math" &&
				m.MethodSig is not null && m.MethodSig.Params.Count == pcount;
		}

		static bool Foldable(Instruction i) =>
			i.IsLdcI4() || i.OpCode.Code == Code.Neg ||
			IsMathCall(i, "Abs", 1) || IsMathCall(i, "Min", 2) || IsMathCall(i, "Max", 2);

		void UndoIntMutation(MethodDef m) {
			var ins = m.Body.Instructions;
			var targets = BranchTargets(m);
			int i = 0;
			while (i < ins.Count) {
				if (ins[i].IsLdcI4() && ins[i].GetLdcI4Value() > 0 &&
					i + 1 < ins.Count && IsMathCall(ins[i + 1], "Abs", 1)) {
					var stack = new Stack<int>();
					int j = i, lastGood = -1;
					long lastVal = 0;
					bool underflow = false;
					while (j < ins.Count && Foldable(ins[j]) && (j == i || !targets.Contains(ins[j]))) {
						var op = ins[j];
						if (op.IsLdcI4())
							stack.Push(op.GetLdcI4Value());
						else if (op.OpCode.Code == Code.Neg) {
							if (stack.Count < 1) { underflow = true; break; }
							stack.Push(-stack.Pop());
						}
						else if (IsMathCall(op, "Abs", 1)) {
							if (stack.Count < 1) { underflow = true; break; }
							stack.Push(Math.Abs(stack.Pop()));
						}
						else if (IsMathCall(op, "Min", 2)) {
							if (stack.Count < 2) { underflow = true; break; }
							int b = stack.Pop(), a = stack.Pop();
							stack.Push(Math.Min(a, b));
						}
						else if (IsMathCall(op, "Max", 2)) {
							if (stack.Count < 2) { underflow = true; break; }
							int b = stack.Pop(), a = stack.Pop();
							stack.Push(Math.Max(a, b));
						}
						j++;
						if (stack.Count == 1) { lastGood = j; lastVal = stack.Peek(); }
					}
					if (!underflow && lastGood > i + 1) {
						ins[i].OpCode = OpCodes.Ldc_I4;
						ins[i].Operand = (int)lastVal;
						for (int k = lastGood - 1; k > i; k--)
							ins.RemoveAt(k);
						nMut++;
						i++;
						continue;
					}
				}
				i++;
			}
		}

		// ---------- Pass B: proxy-string inline ----------
		void InlineIntProxies() {
			var gt = mod.GlobalType;
			if (gt is null)
				return;
			// Fold any int-mutation inside the proxy bodies first so they collapse to "ldc.i4 X; ret".
			foreach (var m in gt.Methods) {
				if (m.IsStatic && m.Body is not null && m.MethodSig?.Params.Count == 0 &&
					m.MethodSig.RetType?.FullName == "System.Int32")
					UndoIntMutation(m);
			}
			var map = new Dictionary<MethodDef, int>();
			foreach (var m in gt.Methods) {
				if (!m.IsStatic || m.Body is null)
					continue;
				var s = m.MethodSig;
				if (s is null || s.Params.Count != 0 || s.RetType?.FullName != "System.Int32")
					continue;
				var body = m.Body.Instructions.Where(x => x.OpCode.Code != Code.Nop).ToList();
				if (body.Count == 2 && body[0].IsLdcI4() && body[1].OpCode.Code == Code.Ret)
					map[m] = body[0].GetLdcI4Value();
			}
			// Iterate to a fixed point: proxies can call other proxies.
			bool changed = true;
			while (changed) {
				changed = false;
				foreach (var meth in AllMethods()) {
					foreach (var i in meth.Body.Instructions) {
						if (i.OpCode.Code == Code.Call && i.Operand is MethodDef md && map.TryGetValue(md, out var val)) {
							i.OpCode = OpCodes.Ldc_I4;
							i.Operand = val;
							changed = true;
						}
					}
				}
			}
			foreach (var m in map.Keys)
				gt.Methods.Remove(m);
		}

		void InlineStringProxies() {
			var gt = mod.GlobalType;
			if (gt is null)
				return;
			var map = new Dictionary<MethodDef, string>();
			foreach (var m in gt.Methods) {
				if (!m.IsStatic || m.Body is null)
					continue;
				var s = m.MethodSig;
				if (s is null || s.Params.Count != 0 || s.RetType?.FullName != "System.String")
					continue;
				var body = m.Body.Instructions.Where(x => x.OpCode.Code != Code.Nop).ToList();
				if (body.Count == 2 && body[0].OpCode.Code == Code.Ldstr && body[1].OpCode.Code == Code.Ret)
					map[m] = (string)body[0].Operand;
			}
			foreach (var meth in AllMethods()) {
				foreach (var i in meth.Body.Instructions) {
					if (i.OpCode.Code == Code.Call && i.Operand is MethodDef md && map.TryGetValue(md, out var str)) {
						i.OpCode = OpCodes.Ldstr;
						i.Operand = str;
						nProxyInline++;
					}
				}
			}
			foreach (var m in map.Keys) {
				gt.Methods.Remove(m);
				nProxyDel++;
			}
		}

		// ---------- Pass C: string decrypt ----------
		void FindDecoder() {
			var gt = mod.GlobalType;
			if (gt is null)
				return;
			foreach (var m in gt.Methods) {
				var s = m.MethodSig;
				if (s is not null && s.RetType?.FullName == "System.String" && s.Params.Count == 6 &&
					s.Params[0].FullName == "System.String" &&
					s.Params.Skip(1).All(p => p.FullName == "System.Int32")) {
					decoder = m;
					return;
				}
			}
		}

		static string Decrypt(string enc, int key) {
			var sb = new StringBuilder(enc.Length);
			foreach (char c in enc)
				sb.Append((char)(c - key));
			return sb.ToString();
		}

		void DecryptStrings(MethodDef m) {
			var ins = m.Body.Instructions;
			var targets = BranchTargets(m);
			for (int i = 0; i + 6 < ins.Count; i++) {
				if (ins[i].OpCode.Code != Code.Ldstr)
					continue;
				bool ok = true;
				for (int k = 1; k <= 5; k++)
					if (!ins[i + k].IsLdcI4()) { ok = false; break; }
				if (!ok)
					continue;
				if (ins[i + 6].OpCode.Code != Code.Call)
					continue;
				if (ins[i + 6].Operand is not IMethod im || decoder is null || im.MDToken != decoder.MDToken)
					continue;
				bool tgt = false;
				for (int k = 1; k <= 6; k++)
					if (targets.Contains(ins[i + k])) { tgt = true; break; }
				if (tgt)
					continue;
				int key = ins[i + 2].GetLdcI4Value();
				ins[i].Operand = Decrypt((string)ins[i].Operand, key);
				for (int k = 6; k >= 1; k--)
					ins.RemoveAt(i + k);
				nStrDec++;
			}
		}

		// ---------- Pass D: control-flow un-flatten ----------
		void UndoControlFlow(MethodDef m) {
			var ins = m.Body.Instructions;
			if (ins.Count < 6)
				return;
			if (!(ins[0].IsLdcI4() && ins[0].GetLdcI4Value() == 0))
				return;
			if (!IsStloc(ins[1], out var state))
				return;
			if (ins[2].OpCode.Code != Code.Br && ins[2].OpCode.Code != Code.Br_S)
				return;
			if (ins[3].OpCode.Code != Code.Nop)
				return;

			var blocks = new Dictionary<int, List<Instruction>>();
			int p = 4;
			int count = ins.Count;
			try {
				while (p + 3 < count) {
					if (!IsLdloc(ins[p], out var l) || l != state)
						return;
					if (!ins[p + 1].IsLdcI4())
						return;
					if (ins[p + 2].OpCode.Code != Code.Ceq)
						return;
					if (ins[p + 3].OpCode.Code != Code.Brfalse && ins[p + 3].OpCode.Code != Code.Brfalse_S)
						return;
					int n = ins[p + 1].GetLdcI4Value();
					if (ins[p + 3].Operand is not Instruction skip)
						return;
					int skipIdx = ins.IndexOf(skip);

					if (skipIdx > p + 4 && ins[skipIdx].OpCode.Code == Code.Nop &&
						IsStloc(ins[skipIdx - 1], out var st2) && st2 == state &&
						ins[skipIdx - 2].IsLdcI4()) {
						var body = new List<Instruction>();
						for (int k = p + 4; k < skipIdx - 2; k++)
							body.Add(ins[k]);
						blocks[n] = body;
						p = skipIdx + 1;
						continue;
					}
					else {
						if (p + 5 >= count)
							return;
						if (ins[p + 4].OpCode.Code != Code.Br && ins[p + 4].OpCode.Code != Code.Br_S)
							return;
						if (ins[p + 5] != skip)
							return;
						var lastBody = new List<Instruction>();
						for (int k = p + 6; k < count; k++)
							lastBody.Add(ins[k]);
						blocks[n] = lastBody;
						p = count;
						break;
					}
				}
			}
			catch {
				return;
			}

			int cnt = blocks.Count;
			for (int n = 0; n < cnt; n++)
				if (!blocks.ContainsKey(n))
					return;

			var rebuilt = new List<Instruction>();
			for (int n = 0; n < cnt; n++)
				rebuilt.AddRange(blocks[n]);

			var present = new HashSet<Instruction>(rebuilt);
			foreach (var i in rebuilt) {
				if (i.Operand is Instruction to && !present.Contains(to))
					return;
				if (i.Operand is Instruction[] arr && arr.Any(t => !present.Contains(t)))
					return;
			}
			foreach (var eh in m.Body.ExceptionHandlers) {
				foreach (var b in new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
					if (b is not null && !present.Contains(b))
						return;
			}
			if (rebuilt.Count == 0)
				return;

			ins.Clear();
			foreach (var i in rebuilt)
				ins.Add(i);
			if (!ins.Any(x => (IsLdloc(x, out var a) && a == state) || (IsStloc(x, out var b) && b == state)))
				m.Body.Variables.Remove(state);
			nCfOk++;
		}

		// ---------- helpers ----------
		static bool IsStloc(Instruction i, out Local? l) {
			l = null;
			switch (i.OpCode.Code) {
			case Code.Stloc:
			case Code.Stloc_S:
				l = i.Operand as Local;
				return l is not null;
			}
			return false;
		}

		static bool IsLdloc(Instruction i, out Local? l) {
			l = null;
			switch (i.OpCode.Code) {
			case Code.Ldloc:
			case Code.Ldloc_S:
				l = i.Operand as Local;
				return l is not null;
			}
			return false;
		}

		static HashSet<Instruction> BranchTargets(MethodDef m) {
			var set = new HashSet<Instruction>();
			foreach (var i in m.Body.Instructions) {
				if (i.Operand is Instruction to)
					set.Add(to);
				else if (i.Operand is Instruction[] arr)
					foreach (var t in arr)
						set.Add(t);
			}
			foreach (var eh in m.Body.ExceptionHandlers)
				foreach (var b in new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
					if (b is not null)
						set.Add(b);
			return set;
		}

		sealed class DummyLogger : ILogger {
			public static readonly DummyLogger NoThrow = new DummyLogger();
			public void Log(object? sender, LoggerEvent loggerEvent, string format, params object?[] args) { }
			public bool IgnoresEvent(LoggerEvent loggerEvent) => true;
		}
	}
}

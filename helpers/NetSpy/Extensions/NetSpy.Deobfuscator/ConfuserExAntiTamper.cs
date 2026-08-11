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
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.PE;

namespace NetSpy.Deobfuscator {
	/// <summary>
	/// Static unpacker for ConfuserEx (Normal-mode) anti-tamper, which encrypts method bodies
	/// and decrypts them at runtime in <c>&lt;Module&gt;.cctor</c>. de4dot only stubs the corrupt
	/// bodies, losing them; this reproduces ConfuserEx's exact decryption on the raw file bytes so
	/// the real method bodies are recovered before de4dot runs.
	///
	/// Algorithm ported from the ConfuserEx-Unpacker project (XenocodeRCE), matching the
	/// ConfuserEx AntiTamper.Normal runtime + NormalDeriver key derivation.
	/// </summary>
	static class ConfuserExAntiTamper {
		/// <summary>Returns true if the module looks like it has an added (tamper) section.</summary>
		public static bool IsTampered(ModuleDefMD module) {
			try {
				foreach (var section in module.Metadata.PEImage.ImageSectionHeaders) {
					switch (section.DisplayName) {
					case ".text":
					case ".rsrc":
					case ".reloc":
						continue;
					default:
						return true;
					}
				}
			}
			catch {
			}
			return false;
		}

		/// <summary>
		/// Tries to remove ConfuserEx anti-tamper from <paramref name="inputPath"/>, writing the
		/// recovered assembly to <paramref name="outputPath"/>. Returns true on success.
		/// </summary>
		public static bool TryUnpack(string inputPath, string outputPath) {
			ModuleDefMD? module = null;
			try {
				var raw = File.ReadAllBytes(inputPath);
				module = ModuleDefMD.Load(raw);
				if (!IsTampered(module))
					return false;

				var cctor = module.GlobalType?.FindStaticConstructor();
				if (cctor?.Body is null || cctor.Body.Instructions.Count == 0)
					return false;
				if (cctor.Body.Instructions[0].Operand is not MethodDef antiTamp)
					return false;

				// On the "maximum" preset the anti-tamper Initialize method is itself control-flow +
				// constant obfuscated, so the injected seed constants are computed through ConfuserEx's
				// switch CFG and can't be read directly. Deobfuscate its control flow first so the seeds
				// collapse back to plain "ldc.i4 <value>; stloc" pairs.
				de4dot.code.deobfuscators.ConfuserEx.ConfuserExCflow.Deobfuscate(antiTamp);

				var initialKeys = FindInitialKeys(antiTamp);
				if (initialKeys is null)
					return false;

				var sections = module.Metadata.PEImage.ImageSectionHeaders;
				var confSec = sections[0];

				using var input = new MemoryStream();
				input.Write(raw, 0, raw.Length);
				input.Position = 0;
				var reader = new BinaryReader(input);

				Hash1(input, reader, sections, confSec, initialKeys);
				var arrayKeys = GetArrayKeys(initialKeys);
				DecryptSection(reader, confSec, input, arrayKeys);

				input.Position = 0;
				var recovered = ModuleDefMD.Load(input);
				// drop the anti-tamper call from cctor
				var newCctor = recovered.GlobalType?.FindStaticConstructor();
				if (newCctor?.Body is not null && newCctor.Body.Instructions.Count > 0 &&
					newCctor.Body.Instructions[0].OpCode == OpCodes.Call)
					newCctor.Body.Instructions.RemoveAt(0);

				recovered.Write(outputPath);
				recovered.Dispose();
				return true;
			}
			catch (Exception ex) {
				DeobLog.Log($"ConfuserEx anti-tamper unpack failed for {inputPath}", ex);
				return false;
			}
			finally {
				module?.Dispose();
			}
		}

		// The four anti-tamper seed constants (z=KeyI1, x=KeyI2, c=KeyI3, v=KeyI4) are injected
		// into the init method as four CONSECUTIVE "ldc.i4 <big>; stloc <Vk>" pairs that store to
		// consecutive local slots, just before the section-scan loop. The exact slot numbers vary
		// between ConfuserEx builds (e.g. V_8..V_11 vs V_10..V_13), so we match this structure
		// rather than fixed slot names. Getting these wrong silently corrupts only the FIRST 16
		// decrypted uints (the constant pool, which sits at the start of the encrypted section) —
		// method bodies further in still decrypt because the key self-heals from the ciphertext —
		// which is exactly the bug the old V_10..V_13 heuristic caused.
		static uint[]? FindInitialKeys(MethodDef antiTamp) {
			var ins = antiTamp.Body.Instructions;
			var locals = antiTamp.Body.Variables;
			for (int i = 0; i + 7 < ins.Count; i++) {
				if (!LdcStloc(ins, locals, i, out int v0, out int l0)) continue;
				if (!LdcStloc(ins, locals, i + 2, out int v1, out int l1)) continue;
				if (!LdcStloc(ins, locals, i + 4, out int v2, out int l2)) continue;
				if (!LdcStloc(ins, locals, i + 6, out int v3, out int l3)) continue;
				if (l1 != l0 + 1 || l2 != l1 + 1 || l3 != l2 + 1) continue;
				if (!SeedLike(v0) || !SeedLike(v1) || !SeedLike(v2) || !SeedLike(v3)) continue;
				return new[] { (uint)v0, (uint)v1, (uint)v2, (uint)v3 };
			}
			return null;
		}

		static bool LdcStloc(System.Collections.Generic.IList<Instruction> ins, System.Collections.Generic.IList<Local> locals, int i, out int val, out int local) {
			val = 0; local = -1;
			if (!ins[i].IsLdcI4())
				return false;
			if (!ins[i + 1].IsStloc())
				return false;
			val = ins[i].GetLdcI4Value();
			var loc = ins[i + 1].GetLocal(locals);
			if (loc == null)
				return false;
			local = loc.Index;
			return true;
		}

		// The seed constants are random 32-bit values, never small loop counters / PE-header offsets.
		static bool SeedLike(int v) => v < -0x1000 || v > 0x1000;

		static void Hash1(Stream stream, BinaryReader reader, System.Collections.Generic.IList<ImageSectionHeader> sections, ImageSectionHeader confSec, uint[] k) {
			foreach (var header in sections) {
				if (header == confSec || header.DisplayName == "")
					continue;
				int num = (int)(header.SizeOfRawData >> 2);
				stream.Position = header.PointerToRawData;
				for (int i = 0; i < num; i++) {
					uint d = reader.ReadUInt32();
					uint n5 = ((k[0] ^ d) + k[1]) + (k[2] * k[3]);
					k[0] = k[1];
					k[1] = k[2];
					k[1] = k[3];
					k[3] = n5;
				}
			}
		}

		static uint[] GetArrayKeys(uint[] k) {
			var dst = new uint[0x10];
			var src = new uint[0x10];
			for (int i = 0; i < 0x10; i++) {
				dst[i] = k[3];
				src[i] = k[1];
				k[0] = (k[1] >> 5) | (k[1] << 0x1b);
				k[1] = (k[2] >> 3) | (k[2] << 0x1d);
				k[2] = (k[3] >> 7) | (k[3] << 0x19);
				k[3] = (k[0] >> 11) | (k[0] << 0x15);
			}
			// NormalDeriver: i%3 -> xor / mul / add
			var ret = new uint[0x10];
			for (int i = 0; i < 0x10; i++) {
				switch (i % 3) {
				case 0: ret[i] = dst[i] ^ src[i]; break;
				case 1: ret[i] = dst[i] * src[i]; break;
				case 2: ret[i] = dst[i] + src[i]; break;
				}
			}
			return ret;
		}

		static void DecryptSection(BinaryReader reader, ImageSectionHeader confSec, Stream stream, uint[] arrayKeys) {
			int num = (int)(confSec.SizeOfRawData >> 2);
			int ptr = (int)confSec.PointerToRawData;
			stream.Position = ptr;
			var result = new uint[num];
			for (uint i = 0; i < num; i++) {
				uint enc = reader.ReadUInt32();
				result[i] = enc ^ arrayKeys[i & 15];
				arrayKeys[i & 15] = enc + 0x3dbb2819;
			}
			var bytes = new byte[num << 2];
			Buffer.BlockCopy(result, 0, bytes, 0, bytes.Length);
			stream.Position = ptr;
			stream.Write(bytes, 0, bytes.Length);
		}
	}
}

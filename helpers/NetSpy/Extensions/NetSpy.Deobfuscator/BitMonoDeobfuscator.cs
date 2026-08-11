/*
    Dedicated cleaner for the "BitMono" obfuscator (github.com/sunnamed434/BitMono), which de4dot
    does not handle. BitMono is a modern free/open-source .NET obfuscator. This reverses the two
    protections that otherwise make the assembly completely unopenable / unreadable in NetSpy:

      * BitDotNet / BitDecompiler — corrupt the PE signature and zero the COR20 header (cb, runtime
        version, MetaData size) so strict parsers (dnlib, ILDasm) reject the file entirely.
      * StringsEncryption — replaces string literals with an AES-CBC (PBKDF2/Rfc2898, 1000 iters)
        decoder call taking three static byte[] fields (data, salt, key), populated from RVA blobs
        via RuntimeHelpers.InitializeArray in a cctor.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace NetSpy.Deobfuscator {
	sealed class BitMonoDeobfuscator {
		/// <summary>Returns true if the file looks like BitMono (corrupt COR20 header + AES string decoder).</summary>
		public static bool IsBitMono(string path) {
			try {
				var raw = File.ReadAllBytes(path);
				// BitMono corrupts the PE signature to 0x00014550 (should be 0x00004550) and/or zeroes cb.
				if (!TryFixHeaders(raw, out _))
					return false;
				using var ms = new MemoryStream(raw);
				var mod = ModuleDefMD.Load(ms);
				try { return FindDecoder(mod) != null; }
				finally { mod.Dispose(); }
			}
			catch {
				return false;
			}
		}

		public static bool Run(string inputPath, string outputPath) {
			var raw = File.ReadAllBytes(inputPath);
			bool headerFixed = TryFixHeaders(raw, out _);

			ModuleDefMD mod;
			using (var ms = new MemoryStream(raw))
				mod = ModuleDefMD.Load(ms);
			try {
				int strings = DecryptStrings(mod);
				DeobLog.Log($"BitMono: headerFixed={headerFixed}, decrypted {strings} string(s)");
				var opts = new ModuleWriterOptions(mod) { Logger = DummyLogger.NoThrow };
				opts.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
				mod.Write(outputPath, opts);
				return true;
			}
			finally {
				mod.Dispose();
			}
		}

		// ---------- header repair (reverse BitDotNet / BitDecompiler) ----------
		static bool TryFixHeaders(byte[] raw, out bool changed) {
			changed = false;
			using var ms = new MemoryStream(raw);
			using var r = new BinaryReader(ms);
			using var w = new BinaryWriter(ms);
			ms.Position = 0x3C;
			uint peHeader = r.ReadUInt32();
			if (peHeader + 0x100 >= raw.Length)
				return false;

			ms.Position = peHeader;
			uint sig = r.ReadUInt32();
			bool corrupt = sig != 0x00004550;
			// restore "PE\0\0"
			ms.Position = peHeader;
			w.Write(0x00004550u);

			ms.Position = peHeader + 0x6;
			ushort sections = r.ReadUInt16();
			ms.Position += 0x10;
			bool pe32Plus = r.ReadUInt16() == 0x20B;
			ms.Position += (pe32Plus ? 0x38 : 0x28) + 0xA6;
			uint dotNetRva = r.ReadUInt32();
			ms.Position += 0xC;
			uint cor20Ptr = 0;
			for (int i = 0; i < sections; i++) {
				ms.Position += 0xC;
				uint va = r.ReadUInt32();
				uint rawSize = r.ReadUInt32();
				uint rawPtr = r.ReadUInt32();
				ms.Position += 0x10;
				if (dotNetRva >= va && dotNetRva < va + rawSize && cor20Ptr == 0)
					cor20Ptr = dotNetRva + rawPtr - va;
			}
			if (cor20Ptr == 0 || cor20Ptr + 8 > raw.Length)
				return corrupt; // signature was corrupt even if we can't reach the COR20 header

			ms.Position = cor20Ptr;
			uint cb = r.ReadUInt32();
			if (cb != 0x48 || corrupt) {
				ms.Position = cor20Ptr;
				w.Write(0x48);       // cb
				w.Write((ushort)2);  // MajorRuntimeVersion
				w.Write((ushort)5);  // MinorRuntimeVersion
				changed = true;
			}
			return corrupt || changed;
		}

		// ---------- string decryption ----------
		static MethodDef FindDecoder(ModuleDef mod) {
			foreach (var t in mod.GetTypes())
				foreach (var m in t.Methods) {
					var s = m.MethodSig;
					if (m.IsStatic && s != null && s.RetType?.FullName == "System.String" && s.Params.Count == 3 &&
						s.Params.All(p => p.FullName == "System.Byte[]"))
						return m;
				}
			return null;
		}

		static int DecryptStrings(ModuleDef mod) {
			var decoder = FindDecoder(mod);
			if (decoder == null)
				return 0;

			// map static byte[] field -> its bytes (populated via cctor InitializeArray)
			var fieldBytes = new Dictionary<FieldDef, byte[]>();
			foreach (var t in mod.GetTypes())
				foreach (var m in t.Methods) {
					if (!m.HasBody) continue;
					var il = m.Body.Instructions;
					for (int k = 1; k < il.Count; k++) {
						if (il[k].OpCode != OpCodes.Stsfld || il[k].Operand is not FieldDef bf ||
							bf.FieldType?.FullName != "System.Byte[]")
							continue;
						for (int j = k - 1; j >= Math.Max(0, k - 6); j--)
							if (il[j].OpCode == OpCodes.Ldtoken && il[j].Operand is FieldDef rf && rf.InitialValue != null) {
								fieldBytes[bf] = rf.InitialValue;
								break;
							}
					}
				}

			int decrypted = 0;
			foreach (var t in mod.GetTypes())
				foreach (var m in t.Methods) {
					if (!m.HasBody) continue;
					var ins = m.Body.Instructions;
					var locals = m.Body.Variables;
					// local -> byte[] field ("ldsfld f; stloc L")
					var localMap = new Dictionary<int, FieldDef>();
					for (int k = 0; k + 1 < ins.Count; k++)
						if (ins[k].OpCode == OpCodes.Ldsfld && ins[k].Operand is FieldDef fd && ins[k + 1].IsStloc()) {
							var l = ins[k + 1].GetLocal(locals);
							if (l != null) localMap[l.Index] = fd;
						}

					byte[] ValueOf(FieldDef f) => f != null && fieldBytes.TryGetValue(f, out var b) ? b : f?.InitialValue;
					byte[] ArgBytes(Instruction x) {
						if (x.Operand is FieldDef f) return ValueOf(f);
						var lv = x.GetLocal(locals);
						return lv != null && localMap.TryGetValue(lv.Index, out var mf) ? ValueOf(mf) : null;
					}

					for (int i = 3; i < ins.Count; i++) {
						if (ins[i].OpCode != OpCodes.Call || ins[i].Operand != decoder)
							continue;
						var b1 = ArgBytes(ins[i - 3]);
						var b2 = ArgBytes(ins[i - 2]);
						var b3 = ArgBytes(ins[i - 1]);
						if (b1 == null || b2 == null || b3 == null)
							continue;
						string str;
						try { str = Decrypt(b1, b2, b3); }
						catch { continue; }
						// replace the 3 loads + call with: nop; nop; nop; ldstr
						ins[i - 3].OpCode = OpCodes.Nop; ins[i - 3].Operand = null;
						ins[i - 2].OpCode = OpCodes.Nop; ins[i - 2].Operand = null;
						ins[i - 1].OpCode = OpCodes.Nop; ins[i - 1].Operand = null;
						ins[i].OpCode = OpCodes.Ldstr; ins[i].Operand = str;
						decrypted++;
					}
				}
			return decrypted;
		}

		static string Decrypt(byte[] bytes, byte[] salt, byte[] key) {
			using var aes = Aes.Create();
			aes.KeySize = 256;
			aes.BlockSize = 128;
			using var derive = new Rfc2898DeriveBytes(key, salt, 1000);
			aes.Key = derive.GetBytes(32);
			aes.IV = derive.GetBytes(16);
			aes.Mode = CipherMode.CBC;
			using var ms = new MemoryStream();
			using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
				cs.Write(bytes, 0, bytes.Length);
			return Encoding.UTF8.GetString(ms.ToArray());
		}

		sealed class DummyLogger : ILogger {
			public static readonly DummyLogger NoThrow = new DummyLogger();
			public void Log(object sender, LoggerEvent loggerEvent, string format, params object[] args) { }
			public bool IgnoresEvent(LoggerEvent loggerEvent) => true;
		}
	}
}

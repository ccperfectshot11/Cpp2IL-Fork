# NetSpy

**NetSpy** is a .NET decompiler and assembly editor with a built‑in **automatic
deobfuscation engine**. It is a fork of
[dnSpyEx](https://github.com/dnSpyEx/dnSpy) (itself a continuation of the original
[dnSpy](https://github.com/dnSpy/dnSpy)), trimmed down to focus on
**deobfuscation + decompilation**: obfuscated assemblies open, deobfuscate and
decompile into **clean, compilable C#** with as little manual work as possible.

You can inspect and edit .NET / Unity assemblies even when no source code is
available — and when the assembly is protected by a known obfuscator, NetSpy tries to
undo that protection for you before you ever look at the code.

> **Note:** NetSpy is *not* a debugger. dnSpy's runtime debugger was removed from this
> fork to keep it lean and focused on static analysis. If you need live debugging, use
> upstream [dnSpyEx](https://github.com/dnSpyEx/dnSpy).

> NetSpy is a reverse‑engineering / security‑research tool. Only use it on assemblies
> you own or are authorized to analyze.

---

## What NetSpy adds on top of dnSpy

Everything dnSpy/dnSpyEx does minus the debugger (assembly editor, hex editor,
decompiler — see below) **plus**:

- **Automatic deobfuscation on load.** When you open an assembly, NetSpy detects the
  obfuscator that was used and runs the matching deobfuscation passes in‑process, then
  reloads the cleaned assembly. No separate command‑line step, no round‑trip through
  another tool.
- **String / constant / array decryption.** Encrypted strings, numeric constants and
  byte arrays are decrypted back to their real values.
- **Control‑flow recovery.** Flattened / switch‑based control flow is un‑flattened back
  into normal `if` / loops.
- **Proxy & junk removal.** Call/field proxies, anti‑debug, anti‑dump and anti‑tamper
  stubs are stripped out.
- **A hardened decompiler that produces *buildable* output.** The decompiler was
  patched so reconstructed `async` methods and iterator (`yield return`) state machines
  come out as valid C# instead of half‑reconstructed artifacts. In particular the
  Unity / MelonLoader iterator artifacts (`int num; if (num != 1) …` dead resume
  guards, and `for (…; …; i = num + 1)` pre‑increment temps) are cleaned up so the code
  compiles.
- **Robustness on malformed IL.** Extra null‑guards and stack‑reconciliation were added
  so intentionally broken/obfuscated IL no longer crashes the decompiler — it degrades
  gracefully instead of throwing.

The goal: **open it, and get code you can read and recompile.**

---

## Supported obfuscators

### Verified clean ✅

These were tested end‑to‑end on their **maximum / all‑protections** settings and produce
**zero decompiler exceptions with strings, numbers, arrays and control flow all
recovered**:

| Obfuscator | What NetSpy handles |
|---|---|
| **ConfuserEx / Confuser Core** (all versions, incl. the *Maximum* preset) | anti‑tamper (section decryption), constants protection (string/number/array decryption), control‑flow flattening, reference proxies, anti‑debug / anti‑dump |
| **Obfuscar** (rename + HideStrings + unicode + optimize) | symbol rename cleanup and string recovery (via de4dot) |
| **Zylofuscator** | int/string proxy inlining, integer‑mutation folding, control‑flow recovery, string decryption |
| **BitMono** (modern, AsmResolver‑based) | repairs the corrupted PE / .NET headers that make the file un‑openable, then decrypts the AES‑CBC encrypted strings |
| **Spices.Net / 9Rays.Net** ("Nine‑Rays", incl. *Full + Encrypt strings* max settings) | decrypts its per‑build‑randomized string encryption by *executing* each assembly's own embedded decryptor (a small IL emulator), plus rename cleanup and control‑flow recovery via de4dot |

NetSpy is built and tested primarily for **free / open‑source obfuscators**.
**Spices.Net** is the one commercial obfuscator it fully supports (see the note below).

### Obfuscator detection

NetSpy bundles the [de4dot](https://github.com/de4dot/de4dot) engine, so it can
**recognize** many other obfuscator families (Agile.NET, Babel.NET, Crypto Obfuscator,
DeepSea, Dotfuscator, .NET Reactor, Eazfuscator.NET, SmartAssembly, Xenocode and more)
and tell you what protected an assembly. Detection is not the same as full
deobfuscation, though — see the next section.

---

## Other paid / commercial obfuscators are not a target

Apart from **Spices.Net** (fully supported above), **NetSpy does not deobfuscate
paid / commercial obfuscators.** It is not a license cracker and does not try to be one.
That is a deliberate scope decision, and it also reflects what is technically possible:
deobfuscation is **best‑effort recovery, not magic**, and commercial products are
specifically engineered to defeat static tools.

- **Renaming is one‑way and lossy.** When an obfuscator renames `CalculatePlayerScore`
  to `a`, the original name is *gone* — it is not stored anywhere in the assembly.
  NetSpy can give methods and classes readable, consistent placeholder names, but it
  **cannot invent the original identifiers**. Expect meaningful logic with generic
  names even on the supported free obfuscators.

- **Commercial obfuscators are protected against.** Products like SmartAssembly,
  Dotfuscator, Crypto Obfuscator, Babel and similar are paid tools designed to resist
  exactly this kind of analysis, often behind a license. NetSpy is **not** a
  license‑bypass tool and does not target them — that is out of scope on purpose.

- **Virtualization (VM) obfuscators.** Tools like **Eazfuscator.NET (virtualization
  mode)**, **Agile.NET / SecureTeam virtualization**, and similar convert IL into a
  custom bytecode executed by an embedded virtual machine. The original IL no longer
  exists in the file; recovering it means writing a devirtualizer for that specific VM.
  This is out of scope for NetSpy.

- **Native / packed protection.** **.NET Reactor** (native stub), commercial packers and
  anything that JIT‑decrypts methods at runtime keep the real code encrypted on disk.
  Static analysis sees only the stub — recovering these usually requires dumping the
  process from memory at runtime, which is outside NetSpy's static‑only scope.

- **Custom / unknown obfuscators.** If an assembly uses a scheme NetSpy doesn't
  recognize, it will still open and decompile it, but without targeted deobfuscation.

**Rule of thumb:** free / open obfuscators (ConfuserEx, Obfuscar, Zylo, BitMono) →
clean, compilable output. Everything else (paid / commercial, VM, native) is not a
NetSpy target — it will still open and decompile the assembly, but without undoing that
protection.

---

## Inherited dnSpy features

### Assembly editor
- Edit all metadata
- Edit / add methods and classes in C# or Visual Basic with IntelliSense
- Low‑level IL editor and metadata‑table editor

### Hex editor
- Jump between decompiled code and its IL / PE bytes (and back with F12)
- Highlights .NET metadata and PE structures with tooltips
- Go to RVA / token / method body / heap offset, follow references

### Other
- BAML decompiler, search (classes/methods/strings), analyzer (callers/usages)
- Multiple tabs / tab groups, blue / light / dark / high‑contrast themes
- **Export to project** — dump a whole assembly to a compilable `.csproj`

---

## Building

Requires the .NET SDK (the projects target `net48` and `net10.0-windows`).

```powershell
git clone --recursive https://github.com/ccperfectshot11/NetSpy.git
cd NetSpy
./build.ps1 -NoMsbuild
# or, with the SDK directly:
dotnet build NetSpy.sln -c Release
```

Output binaries (under `NetSpy/NetSpy/bin/Release/<tfm>/`):

- `NetSpy.exe` — the GUI (x64) / `NetSpy-x86.exe` (x86)
- `NetSpy.Console.exe` — headless decompiler / project exporter
- `NetSpy.Deobfuscator.x.dll` — the deobfuscation extension

---

## Credits

NetSpy stands on the shoulders of these projects:

- [dnSpy](https://github.com/dnSpy/dnSpy) and [dnSpyEx](https://github.com/dnSpyEx/dnSpy) — the .NET reverse‑engineering suite this fork is based on
- [ILSpy](https://github.com/icsharpcode/ILSpy) — the C# / VB decompiler engine
- [de4dot](https://github.com/de4dot/de4dot) — the deobfuscation engine
- [dnlib](https://github.com/0xd4d/dnlib) — reads/writes (even obfuscated) .NET metadata
- [Roslyn](https://github.com/dotnet/roslyn) — the C# / VB compilers used by the editor
- [Iced](https://github.com/icedland/iced) — x86/x64 disassembler
- and the other open‑source libraries dnSpy already depends on

---

## License

NetSpy is licensed under [GPLv3](NetSpy/NetSpy/LicenseInfo/GPLv3.txt), the same license as
dnSpy. It inherits the licenses of all the bundled components listed above.

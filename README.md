# Cpp2C#

**WIP tool that turns an IL2CPP build back into a Unity project you can open, edit and compile.**

A fork of [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) by Sam Byass, which does the hard part:
parsing `global-metadata.dat`, locating everything in `GameAssembly.dll`, and lifting native code to
an intermediate form. Cpp2IL recovers the *shape* of the assemblies. This fork is about recovering
the **code inside the methods**, well enough that it compiles and behaves like the original.

Upstream's own README is kept as [README_CPP2IL.md](README_CPP2IL.md).

> **This is work in progress.** It is not finished, and it is honest about what it does not do. See
> the numbers below — they are measured on every commit, not estimated.

---

## What "recovered" means here

IL2CPP destroys the original IL at build time. It is not hidden or packed; it is gone. What survives
in the shipped game is native machine code plus metadata. So nothing can give you back the original
source: local variable names, comments and formatting do not exist anywhere any more.

What *can* be recovered is code that **does the same thing**. Type names, method names, field names
and signatures all survive in the metadata, so the output reads like the original project rather
than like `sub_1803196C0(a1, a2)`.

Two things are therefore tracked separately, because they are independent:

| | question | how it is measured |
|---|---|---|
| **Compilation** | is the recovered C# valid? | decompile every type, compile it with Roslyn, count methods that are clean *and* whose type compiles |
| **Behaviour** | does it do the same thing? | run the same method with the same seeded inputs in the recovered assemblies and in the live game, compare a SHA-256 over every input/output pair |

A method can compile perfectly and still be wrong. `Max(int, int)` compiled fine while returning
`a` unconditionally, because an argument had been lost. Only the second test catches that.

## Current state — Stumble Guys 0.64, Unity 2021.3.25f1, x86-64

| | |
|---|---|
| methods that recompile | **13,354 / 16,677 — 80.1%** (project started at 462, 2.8%) |
| of those with no placeholder left | 9,513 / 10,598 — 89.8% |
| behaviour identical to the running game | **1,118 / 1,545 — 72.4%** |
| unit tests | 76 / 76 |

The behaviour figure covers 1,545 of 72,199 methods with a body. That limit is ours, not the game's:
a method can only be compared if its arguments can be built identically in two processes, which
rules out anything taking a class instance. Widening that is active work.

## Status by platform

| | metadata | code recovery |
|---|---|---|
| **x86-64** | yes | **actively worked, the numbers above** |
| x86-32 | yes | lifts, largely untested |
| ARM64 | yes | lifts; known to collapse `LSR` into `ASR` |
| ARMv7 | yes | lifts, untested |
| WASM | yes | lifts; known to collapse unsigned shifts |

## Unity versions

Metadata parsing is inherited from Cpp2IL and spans Unity 5 through Unity 6. What is version-specific
in *this* fork is much narrower: `Il2CppClass` field offsets and the signatures of the il2cpp runtime
helper functions.

And those track the **IL2CPP version**, not the Unity version. 538 Unity releases from 2020 onward
collapse into six:

| IL2CPP | Unity |
|---|---|
| 24, 25, 26 | 2020.1 – 2020.2 |
| 27 | 2020.2 – 2021.2 |
| 28 | 2021.2 |
| **29** | **2021.2 – 6000.0** |

IL2CPP 29 alone covers everything from Unity 2021.2 to Unity 6, and it is the one being worked on.
The intention is to cover 24 through 29, which is every Unity version from 2020.

**What that needs, per version:** a sample game built with it, the matching `libil2cpp` source (it
ships inside every Unity Editor at `Editor/Data/il2cpp/libil2cpp`) to read the struct layouts from,
and a pass through the pipeline fixing what breaks. The cost is dominated by *testing*, not by
writing code — which is exactly the part that scales with people helping.

## Help is wanted, and the bar is low

Anyone is welcome to open issues and pull requests. Concretely useful things, roughly in order:

1. **A sample game for an IL2CPP version other than 29**, with the Unity version stated. Just knowing
   what breaks is progress.
2. **Testing on another architecture.** ARM64 and WASM lift but nobody has measured them. Two bugs in
   this fork's x86 work (signed-vs-unsigned shifts, operand width) are known to exist in the ARM64
   and WASM lifters too, in exactly the same shape.
3. **A method that compiles but behaves wrong.** Those are the valuable ones and the hardest to find.
4. **Fixes for the compile-error families below**, which are what stands between here and 100%.

### The remaining 3,323 methods, by error

| error | methods | what it is |
|---|---|---|
| CS0019 | 553 | operator applied to `IntPtr` where a real type was meant |
| CS0029 | 424 | conversion, most ending at `System.Exception` |
| CS1503 | 249 | wrong argument type at a call |
| CS1061 | 132 | member that does not exist on the type |
| ~20 more | ~700 | smaller families |

### How to work on it

Every change that can be measured sits behind an environment switch, so one build can be A/B tested:

```
CPP2IL_NO_HELPERS=1 CPP2IL_INIT_LOCALS=1 Cpp2IL.exe --game-path <game> \
  --output-as dll_il_recovery --output-to <out> \
  --use-processor attributeinjector,stablenamer,publicizer,sanitizenames \
  --processor-config "attr-injector-use-ez-diff=1`attr-injector-drop-address=1"

Cpp2IL.CompileCheck <out> Assembly-CSharp     # the compilation number
Cpp2IL.VerifyCheck  <out> --seed N --out p1.json   # behaviour, phase 1
```

Rules that exist because ignoring them has cost real time here:

- **Always read `Methods (all)` before comparing two runs.** The denominator has moved three times.
  A percentage without its denominator is not comparable to anything.
- **Measure one change at a time**, with its own switch. Two fixes landed together have twice been
  found to pull in opposite directions.
- **A marker removed by emitting something wrong is worse than the marker.** The marker is honest
  about not knowing.
- `Cpp2IL.VerifyCheck` and `Cpp2IL.VerifyCore` are **not in `Cpp2IL.slnx`** — a solution build skips
  them silently. Build them by project name.

## Licence

MIT, inherited from [Cpp2IL](https://github.com/SamboyCoding/Cpp2IL). See [LICENSE](LICENSE).

This tool is for working with software you are entitled to work with — your own builds, or modding
and preservation where that is permitted. It recovers nothing that the shipped binary does not
already contain.

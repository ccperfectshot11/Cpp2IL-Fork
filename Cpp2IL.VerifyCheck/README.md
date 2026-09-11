# Cpp2IL.VerifyCheck - differential fuzzing

`Cpp2IL.BodyScan` counts the holes the decompiler admits to. `Cpp2IL.CompileCheck` counts the methods
whose C# a compiler accepts. Neither can see a method that decompiles cleanly, recompiles cleanly, runs
happily and returns the wrong number.

This tool measures that axis. It picks the recovered methods that are self-contained enough to call,
feeds each one the same deterministic flood of inputs, and hashes every input/output pair into a single
digest - a *signature*. Two hosts that produce the same signature for a method agree about its behaviour
bit for bit.

* **Phase 1** (implemented here) computes signatures from a Cpp2IL `--output-to` directory.
* **Phase 2** (`Cpp2IL.VerifyMod`) computes the same signatures from the **real native methods inside
  the running game**, via MelonLoader + Il2CppInterop.

`--compare` of the two files is the actual verification. A Phase 1 signature on its own only proves a
method is a function of its arguments; it is the comparison that says whether it is the *right*
function.

## Running it

```
dotnet build Cpp2IL.VerifyCheck -c Release

Cpp2IL.VerifyCheck <dllDir> [dllNameSubstring] [options]
  --seed N          run seed, default 20260910. Same seed => same inputs, on any runtime.
  --iterations N    random inputs per method, default 10000 (the edge sweep is on top)
  --edge-cap N      cap on the edge-case cartesian product per method, default 2048
  --out FILE        signature json, default verifycheck-signatures.json
  --select-only     report the selection and stop
  --max N           only fuzz the first N selected methods
  --timeout S       seconds one method may take before the run is abandoned, default 30
  --no-statics      drop methods that read static fields instead of flagging them

Cpp2IL.VerifyCheck --compare <phase1.json> <phase2.json>
```

Two side files sit next to `--out`: `.inflight` is the crash journal (the method being invoked right
now) and `.skip` is everything that killed or hung a previous run. An access violation cannot be caught
and `Thread.Abort` does not exist on .NET Core, so surviving a hostile body means recording it before
calling it and skipping it next time. Delete both to start clean.

### Comutatoare de mediu

Fiecare latire a selectiei sta dupa cate o variabila, ca o rulare A/B sa se poata face pe acelasi binar.
Raportul de selectie isi tipareste starea pe linia `Widenings`, ca sa nu ramana doua fisiere cu numere
diferite si fara explicatie de unde vin.

| variabila | implicit | ce face |
|---|---|---|
| `CPP2IL_VERIFY_ENUMS` | pornit (`=0` opreste) | accepta un enum ca pe intregul de sub el - la parametru, la receptor si la tipul intors. Masurat pe `out_w56`: selectia trece de la 1.395 la 1.828 de metode. Simetria e verificata pe metadate: toate cele 1.761 de enum-uri recuperate exista si in joc, cu acelasi tip de baza. |
| `CPP2IL_VERIFY_BCL_LEAF` | oprit (`=1` porneste) | lasa apelurile catre cativa membri **puri** ai bibliotecii gazdei (`IntPtr.Zero`, `Math.Abs/Min/Max/Sign/Floor/Ceiling/Truncate/Round/Sqrt`, `BitConverter`) sa fie frunze ale grafului, in loc sa ceara ca tinta sa fie si ea in lista alba. Masurat: +151 singur, +227 peste enum-uri. Oprit implicit pentru ca egalitatea bit-cu-bit intre .NET si mscorlib-ul IL2CPP se poate doar masura, nu deduce. |
| `CPP2IL_VERIFY_IL2CPP_GLOBAL_NS` | pornit (`=0` opreste) | **faza 2**: taie prefixul `Il2Cpp.` de la tipurile pe care jocul le tine fara namespace. Fara el niciun astfel de tip nu se potriveste niciodata - masurat, 185 de metode pierdute din 247. |
| `CPP2IL_VERIFY_INDEX_CTORS` | pornit (`=0` opreste) | **faza 2**: indexeaza si constructorii, pe care `GetMethods` nu ii intoarce niciodata. Fara el, 92 de chei `.ctor` cerute si zero raspunse. |

Ultimele doua sunt citite in procesul JOCULUI, deci trebuie puse inainte sa porneasca el - `run-phase2.ps1`
lasa mediul sa se mosteneasca, la fel ca `CPP2IL_VERIFY_AUTO`.

## What gets selected

A method qualifies when **all** of this holds:

1. it has a CIL body and is either `static`, or an **instance method whose declaring type is a struct
   made transitively of primitives** (Tier 2). The receiver of such a method is one more value the
   harness can generate from the seed, so it is fuzzed exactly like a parameter - the same `ValueShape`
   test decides both. Any other receiver (a class, or a struct that reaches a reference) stays out: it
   would need a heap graph the Phase 2 host could not build identically;
2. every parameter type and the return type is a primitive (`bool char sbyte byte short ushort int uint
   long ulong float double`), a struct made of them transitively, or an **enum over an integer**
   (`CPP2IL_VERIFY_ENUMS`) - no pointers, no by-ref, no arrays, no generics, no strings. An enum is
   admitted because it is an integer at runtime and the two hosts genuinely agree on it: checked against
   the game's own metadata, all 1,761 enums in the recovered build exist in `Il2CppAssemblies`, match by
   normalised name, and have the same underlying integer - zero exceptions. A `void` **static** is excluded because a signature over one would hash the
   inputs and nothing else; a `void` **instance** method is not, because it still has somewhere to put an
   answer - its receiver, which is read back after the call. One that turns out never to write there is
   flagged `noObservableOutput` and dropped from `--compare` rather than counted as agreement;
3. the body carries no decompiler marker - `NoteDecompilerIssue`, the `Console.WriteLine` fallback with
   the same message text, a `NativeMethod_0x*` callee, or a bare marker `ldstr` (the same list
   `Cpp2IL.CompileCheck` matches on, restated over CIL rather than over decompiled C#);
4. the body contains no `calli jmp ldftn ldvirtftn localloc cpblk initblk newarr arglist mkrefany
   refanyval ldsflda stsfld`, and no pointer locals;
5. every `call`/`callvirt`/`newobj` target resolves and is itself in the whitelist. The whitelist is a
   *greatest* fixed point over all body-safe methods, which is what lets mutually recursive maths
   helpers in.

`ldsfld` is allowed and **flagged** (`readsStatics`). Refusing it would drop most of `FPMath`, which
reads `FPLut`'s tables; flagging it records that the comparison for those methods also rests on the two
hosts' class constructors having produced the same tables. The flag is **transitive**: a method whose own
body is clean but which calls one that reads a static carries it too, because the comparison for that
method rests on the same cctor.

### Ce ramane pe dinafara, si de ce

Numarul de comportament descrie o felie, si felia trebuie spusa cinstit. Numarate pe `out_w56`, peste
cele 72.199 de metode cu corp, primul motiv de respingere al fiecareia:

| motiv | metode |
|---|---|
| metoda de instanta pe o **clasa** | 32.993 |
| parametru care nu e numai-primitive | 28.644 |
| generic | 3.330 |
| metoda de instanta pe o structura care ajunge la o referinta | 1.380 |
| tip intors care nu e numai-primitive | 1.198 |
| apel nerezolvabil / in afara listei albe | 1.860 |
| constructor static | 878 |
| static care nu intoarce nimic | 516 |

Doua dintre acestea nu sunt lene, ci asimetrie adevarata, si de aceea raman nefacute:

* **clasele** (32.993 receptori + 14.895 parametri, adica doua treimi din tot codul). Il2CppInterop nu
  proiecteaza o clasa a jocului ca pe o clasa cu campurile ei, ci ca pe un invelis peste un pointer, cu
  campurile ajunse proprietati. `ValueShape` merge pe campurile de instanta, deci cele doua gazde ar
  descrie obiecte diferite sub acelasi nume. Ar trebui o forma bazata pe NUME de campuri, plus un
  constructor recuperat - adica cod neverificat - rulat ca sa cladeasca receptorul. Alta treaba, nu una
  ascunsa aici;
* **tablourile** (777 de parametri). Il2CppInterop proiecteaza `T[]` ca `Il2CppStructArray<T>`, deci
  cheia din faza 2 ar arata altfel decat cea din faza 1 si perechea nu s-ar forma niciodata.

Doua au fost masurate si lasate pentru ca nu merita pretul, nu pentru ca ar fi imposibile:

* **`string`** ar aduce +40 de metode singur si +166 peste celelalte latiri, desi 4.710 metode sunt
  *intai* respinse din cauza lui - restul sunt oprite si de altceva. Ar cere o frunza de tip referinta
  in `ValueShape`, care azi este numai pe tipuri valoare;
* **`ref`/`out` pe primitive** ar aduce +14. Reflection scrie inapoi in tabloul de argumente, deci
  mecanismul exista, dar 14 metode nu platesc canalul de iesire in plus din hash.

## What a signature is

For each method, in order: the edge sweep (a mixed-radix walk over each leaf's interesting values, so a
two-argument method gets every *pair* of them) and then the random sweep. Per iteration:

```
0x10, [0x12, <every receiver leaf>,]   <every argument leaf: kind byte + 64-bit raw value>,
      0x00, 0x11, <every output leaf>          (returned; 0x03 instead of 0x11 for a void return)
   or 0x01, <UTF-8 exception type name>        (threw)
      [0x13, <every receiver leaf, AFTER the call>]
```

absorbed into a rolling SHA-256, of which the first 16 bytes are reported as hex. Points that matter:

* **The receiver is an input and an output.** A struct instance method can write through its `this`, so
  the receiver is absorbed once before the call (`0x12`) and once after it (`0x13`) - on the throwing
  path too, since a body that half-wrote its receiver and then threw did something observable. Without
  the second absorb a `void Normalize()` would hash its inputs and nothing else. A fresh receiver box is
  built each iteration, so one call's mutation never becomes the next call's input. Its leaves come
  first, which under the edge cap means the receiver is the part that varies fastest and a trailing
  argument may never leave its first edge value - the random sweep is what covers those.
* **Raw bits, never text.** Formatting a float would fold `-0.0` into `0`, collapse NaN payloads and
  round the last digits away, and each of those is a real behavioural difference.
* **Exception type names only.** Messages carry addresses, member names and the current culture, none of
  which the other host reproduces.
* **`System.Random` is not used anywhere.** Its algorithm differs between .NET versions and again on
  Mono, so the inputs are generated by a SplitMix64 written out in full.
* **The per-method seed comes from the run seed and the method key**, never from its position in the
  list, so adding one method does not change every signature after it.
* **`ConstantOutput` is measured over the RETURNED value**, not over the receiver: a pure getter leaves
  the receiver holding the fuzzed input, so folding that in would make every instance method look like it
  varied and would switch the check off on exactly the methods Tier 2 adds. Where there is no returned
  value the receiver is the answer, so there it is the thing compared. What the receiver did is reported
  separately, as `mutatesReceiver` / `mutatedCount`.
* Every method is swept **twice**; if the digests differ the method is not a function of its arguments
  and is marked `nonDeterministic`, which excludes it from the comparison rather than reporting it as a
  mismatch.

`FuzzInputs` deliberately spends most of its draws on values a game would produce: uniform 64-bit
garbage makes a float a NaN nine times out of ten and every sanity branch goes untaken. One strategy
generates `n * 65536 + jitter` because Photon `FP` is Q16.16 in a `long` - that is what makes `FPMath.Sin`
run its polynomial instead of its overflow guard.

## Phase 2: the same signature from the running game

`Cpp2IL.VerifyMod` is that mod. Build it, drop `Cpp2IL.VerifyMod.dll` and `Cpp2IL.VerifyCore.dll` into
the game's `Mods` folder alongside a Phase 1 file renamed `verifycheck-phase1.json`, and start the game.
**Press F9 in-game** to start the sweep; it writes `verifycheck-phase2.json` next to itself. It is
deliberately not automatic: the sweep calls real game code with NaN, denormals and `int.MinValue`, which
is exactly what nothing in a shipped game is written to survive, and minutes of that at startup would
look like the game had frozen. It keeps the same `.inflight` / `.skip` journal as Phase 1, written before
each call, so a method that takes the process down is named and skipped on the next run.

```
dotnet build Cpp2IL.VerifyMod -c Release -p:GameDir=<game>
Cpp2IL.VerifyCheck <dllDir> --out phase1.json
copy phase1.json <game>\Modserifycheck-phase1.json
<start the game, wait for the log line, quit>
Cpp2IL.VerifyCheck --compare phase1.json <game>\Modserifycheck-phase2.json
```

It resolves a key by indexing every method in the game's own Il2CppInterop assemblies under the same
`MethodKeys.For`, rather than resolving keys one at a time - a key names a type by its normalised name
and there is no reverse lookup from that back to a `Type`. Its own assemblies are excluded from that
index, or it would find the recovered methods sitting next to it and compare them against themselves.

The design below is what it implements, and what a port to another game would need:

1. **Reference `Cpp2IL.VerifyCore`** (it multi-targets `netstandard2.0` for this reason and has no
   package references, so it drops into a Mods folder as one file). Do **not** re-implement input
   generation or hashing - two implementations drift, and then every diff is a diff in the harness.

2. **Read the Phase 1 file for the method list.** Each entry's `key` is
   `Namespace.Type::Method(ParamType,...)->ReturnType`, and for an instance method the receiver is named
   in it as a first pseudo-parameter: `FPVector2::get_Magnitude(this:Photon.Deterministic.FPVector2)->FP`.
   It is there so a static and an instance overload taking the same arguments cannot file under one
   identity, and so the key alone says whether a receiver has to be generated. Keys are built by
   `MethodKeys` from names and shapes only:
   no metadata tokens (the two toolchains hand them out independently) and no assembly names
   (Il2CppInterop prefixes those). `MethodKeys.Normalise` also strips a leading `Il2Cpp` from type names,
   which is how the game's `Il2CppSystem.Single` and the recovered `System.Single` end up as one key.
   Skip entries with `supported: false`, `nonDeterministic: true`, `abortedAfter > 0` or
   `noObservableOutput: true`.

3. **Resolve each key to the real native method.** Il2CppInterop's generated assemblies carry the
   address in a `NativeMethodInfoPtr_<name>` static field per method, or it can be looked up with
   `il2cpp_class_get_method_from_name`. Two ways to call it:

   * *Simplest:* hand the Il2CppInterop-generated `MethodInfo` to `MethodFuzzer.Run(MethodBase, plan)`
     and let interop marshal. Costs a managed trampoline per call but needs no unsafe code.
   * *Faithful:* call the native thunk directly through
     `MethodFuzzer.Run(key, receiverType, parameterTypes, returnType, invoke, plan)` (pass `null` as the
     receiver type for a static). Every selected signature is
     blittable by construction - that is *why* the selector insists on primitive-only value types - so
     no marshalling is involved. The IL2CPP calling convention for a static method is
     `ret Method(arg1, ..., argN, Il2CppMethodInfo* method)`: the extra trailing argument is the method
     pointer from step 3 and must be passed or the callee reads garbage. An instance method on a struct
     takes the receiver FIRST and by pointer - `ret Method(void* this, arg1, ..., Il2CppMethodInfo*)` -
     pointing at the unboxed struct data, which for an Il2CppInterop box is the object pointer plus two
     words. That pointer is how the callee mutates the receiver, so the buffer has to be copied back into
     the box `MethodFuzzer` handed you before you return: it reads the receiver out of that box after the
     call and a mutation lost on the way back reads as a behavioural difference. Build one
     `Marshal.GetDelegateForFunctionPointer` per distinct shape and cache it.

4. **Use the same plan.** `seed`, `randomIterations` and `maxEdgeIterations` from the Phase 1 file's
   `plan` object. A different plan is a different experiment; the plan is folded into the digest, so a
   mismatched plan shows up as every method disagreeing rather than as a silent apples-to-oranges result.

5. **Write with `SignatureJson.Write(writer, SignatureJson.PhaseNative, ...)`**, pull the file off the
   device, and run `Cpp2IL.VerifyCheck --compare phase1.json phase2.json`.

Known caveats to design around, in order of how likely they are to bite:

* **Class constructors.** For any method with `readsStatics: true` the two hosts must have run equivalent
  cctors. In the game the real `FPLut` tables are loaded before anything else touches `FPMath`; in Phase
  1 the recovered cctor runs, or throws. Compare those methods as a separate bucket.
* **The game must be the build the DLLs came from.** A signature is over a specific 0.64 binary.
* **Calling a native method 10,000 times per method on the main thread will hang the game's watchdog.**
  Run the sweep from a coroutine spread over frames, or from a MelonLoader background thread after
  attaching it to the IL2CPP domain (`il2cpp_thread_attach`) - an unattached thread calling into IL2CPP
  crashes the process.
* **Field order.** `ValueShape` sorts a struct's fields by metadata token, i.e. declaration order, on
  both sides. If Il2CppInterop ever reorders, the shapes silently disagree; a mismatch on *every* struct
  method at once is the symptom.

## What Phase 1 already tells you on its own

Three failure classes need no game at all, and the run over `out_ot` found all three:

* **`threw on EVERY input` with `InvalidProgramException`** - the JIT refuses the recovered body. It is
  not a wrong answer, it is not an answer.
* **`CONSTANT OUTPUT`** - it runs, it never throws, and it returns the same value whatever it is handed.
  A body whose logic did not survive. This is the closest Phase 1 gets to a behavioural verdict.
* **`NON-DETERMINISTIC`** - the same inputs gave different answers on the second pass, so the method is
  not a function and no comparison with it can mean anything.

And one that only exists once instance methods are in: a method that **takes the whole process down**.
The crash journal names it, the next run skips it, and the list in `<out>.skip` is a list of recovered
bodies that fault on an argument the runtime cannot protect itself from - the strongest evidence Phase 1
produces on its own that a body is wrong.

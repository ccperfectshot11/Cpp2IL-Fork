# IL2CPPDecompiler

Takes an IL2CPP game folder and produces its assemblies, its C# source, and its assets.

```
IL2CPPDecompiler --path "<game folder>" --output "<output folder>"
```

```
<output>/
  dlls/     assemblies with recovered method bodies
  source/   decompiled C#, one subfolder per assembly
  assets/   assets as a Unity project
```

## How it works

Three stages, in order:

1. **Cpp2IL** reads `GameAssembly` and `global-metadata.dat` and rebuilds managed assemblies,
   recovering method bodies from the compiled machine code.
2. **NetSpy** decompiles those assemblies to C#. Engine and runtime assemblies (`UnityEngine.*`,
   `System.*`, `mscorlib`, `Microsoft.*`, `Mono.*`) are skipped.
3. **AssetRipper** exports the assets as a Unity project. It runs with
   `ScriptExportMode=DllExportWithoutRenaming`, so it does not decompile scripts a second time,
   while still loading them - otherwise prefabs and MonoBehaviours lose their references.

## Building

The three tools are built separately; the pipeline runs their binaries.

```
dotnet build Cpp2IL/Cpp2IL.csproj -c Release
dotnet build helpers/NetSpy/NetSpy/NetSpy.Console/NetSpy.Console.csproj -c Release
dotnet build helpers/AssetRipper/Source/AssetRipper.GUI.Free/AssetRipper.GUI.Free.csproj -c Release
dotnet build IL2CPPDecompiler/IL2CPPDecompiler.csproj -c Release
```

Binary locations are compiled in as defaults and can be overridden with the environment variables
`CPP2IL_DLL`, `NETSPY_DLL` and `ASSETRIPPER_DLL`.

NetSpy must be run from its own output directory: it loads its C# decompiler
(`NetSpy.Decompiler.ILSpy.Core`) from there, and the `net48` build cannot find it on its own.

## What the recovered C# is, and is not

Method bodies are rebuilt from optimised machine code, not read from source. The logic is
generally followable, but this is not the original source and does not compile. Local variable
names, inlined methods and the original loop shapes are gone from the binary and cannot be
recovered. Where a construct could not be translated, the output calls
`Cpp2ILHelpers.NoteDecompilerIssue` rather than silently emitting something wrong.

On one 24,160-method assembly, 57.4% of methods came out with no such marker.

## Components and licences

This repository vendors three separate projects. Their licences apply to their own directories.

| Component | Origin | Licence |
|---|---|---|
| Cpp2IL | [SamboyCoding/Cpp2IL](https://github.com/SamboyCoding/Cpp2IL) | MIT, © 2020 Sam Byass |
| NetSpy (`helpers/NetSpy`) | fork of [dnSpyEx](https://github.com/dnSpyEx/dnSpy) | GPLv3 |
| AssetRipper (`helpers/AssetRipper`) | [AssetRipper/AssetRipper](https://github.com/AssetRipper/AssetRipper) | GPLv3 |

Because two components are GPLv3, anything distributed that includes them has to comply with the
GPL: keep the licence and copyright notices, mark modifications, and provide the source.

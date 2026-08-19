using AsmResolver.DotNet;
using AssetRipper.Export.Scripts;
using AssetRipper.Import.Configuration;
using AssetRipper.Import.Logging;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.ProjectDecompiler;

namespace AssetRipper.Export.UnityProjects.Scripts;

internal class ScriptDecompiler
{
	private readonly ILSpyAssemblyResolver assemblyResolver;
	public LanguageVersion LanguageVersion { get; set; } = LanguageVersion.CSharp7_3;
	public ScriptContentLevel ScriptContentLevel { get; set; } = ScriptContentLevel.Level2;
	public ScriptingBackend ScriptingBackend { get; set; } = ScriptingBackend.Unknown;
	public bool FullyQualifiedTypeNames { get; set; } = false;

	public ScriptDecompiler(IAssemblyManager assemblyManager) : this(new ILSpyAssemblyResolver(assemblyManager), assemblyManager.ScriptingBackend) { }
	private ScriptDecompiler(ILSpyAssemblyResolver assemblyResolver, ScriptingBackend scriptingBackend)
	{
		this.assemblyResolver = assemblyResolver;
		ScriptingBackend = scriptingBackend;
	}

	public void DecompileWholeProject(AssemblyDefinition assembly, string outputFolder, FileSystem fileSystem)
	{
		// Serialize the (Cpp2IL-produced) AsmResolver assembly to bytes, then decompile it
		// with our NetSpy engine instead of ILSpy. NetSpy is isolated in NetSpyAdapter so its
		// ICSharpCode.Decompiler namespace does not clash with AssetRipper's modern ILSpy.
		byte[] bytes;
		using (MemoryStream ms = new())
		{
			assembly.ManifestModule!.Write(ms);
			bytes = ms.ToArray();
		}

		try
		{
			NetSpyAdapter.NetSpyDecompiler.DecompileAssembly(bytes, (relativePath, code) =>
			{
				if (relativePath.EndsWith("UnitySourceGeneratedAssemblyMonoScriptTypes_v1.cs", StringComparison.Ordinal))
				{
					return;
				}

				string fullPath = fileSystem.Path.Join(outputFolder, relativePath);
				string? dir = fileSystem.Path.GetDirectoryName(fullPath);
				if (!string.IsNullOrEmpty(dir))
				{
					fileSystem.Directory.Create(dir);
				}
				fileSystem.File.WriteAllText(fullPath, code);
			});
		}
		catch (Exception exception)
		{
			Logger.Error(exception);
		}
	}
}

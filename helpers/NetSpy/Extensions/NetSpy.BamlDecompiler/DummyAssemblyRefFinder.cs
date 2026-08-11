using dnlib.DotNet;

namespace NetSpy.BamlDecompiler {
	sealed class DummyAssemblyRefFinder : IAssemblyRefFinder {
		readonly IAssembly assemblyDef;

		public DummyAssemblyRefFinder(IAssembly assemblyDef) => this.assemblyDef = assemblyDef;

		public AssemblyRef FindAssemblyRef(TypeRef nonNestedTypeRef) => assemblyDef.ToAssemblyRef();
	}
}

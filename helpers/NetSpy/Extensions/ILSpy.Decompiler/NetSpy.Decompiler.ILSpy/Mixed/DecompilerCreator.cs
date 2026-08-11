using System.Collections.Generic;
using System.ComponentModel.Composition;
using NetSpy.Contracts.Decompiler;
using NetSpy.Decompiler.ILSpy.Core.Mixed;
using NetSpy.Decompiler.ILSpy.Core.Settings;

namespace NetSpy.Decompiler.ILSpy.Mixed {
	[Export(typeof(IDecompilerCreator))]
	sealed class MyDecompilerCreator : IDecompilerCreator {
		readonly DecompilerSettingsService decompilerSettingsService;

		[ImportingConstructor]
		MyDecompilerCreator(DecompilerSettingsService decompilerSettingsService) => this.decompilerSettingsService = decompilerSettingsService;

		public IEnumerable<IDecompiler> Create() => new DecompilerProvider(decompilerSettingsService).Create();
	}
}

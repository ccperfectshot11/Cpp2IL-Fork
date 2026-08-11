/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of NetSpy

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
using System.ComponentModel.Composition;
using System.Linq;
using NetSpy.Contracts.Debugger.Breakpoints.Code;
using NetSpy.Contracts.Debugger.Breakpoints.Code.TextEditor;
using NetSpy.Contracts.Text.Editor;

namespace NetSpy.Debugger.Breakpoints.Code.TextEditor {
	abstract class DbgBreakpointGlyphTextMarkerLocationProviderService {
		public abstract GlyphTextMarkerLocationInfo? GetLocation(DbgCodeBreakpoint breakpoint);
	}

	[Export(typeof(DbgBreakpointGlyphTextMarkerLocationProviderService))]
	sealed class DbgBreakpointGlyphTextMarkerLocationProviderServiceImpl : DbgBreakpointGlyphTextMarkerLocationProviderService {
		readonly Lazy<DbgBreakpointGlyphTextMarkerLocationProvider, IDbgBreakpointGlyphTextMarkerLocationProviderMetadata>[] dbgBreakpointGlyphTextMarkerLocationProviders;

		[ImportingConstructor]
		DbgBreakpointGlyphTextMarkerLocationProviderServiceImpl([ImportMany] IEnumerable<Lazy<DbgBreakpointGlyphTextMarkerLocationProvider, IDbgBreakpointGlyphTextMarkerLocationProviderMetadata>> dbgBreakpointGlyphTextMarkerLocationProviders) =>
			this.dbgBreakpointGlyphTextMarkerLocationProviders = dbgBreakpointGlyphTextMarkerLocationProviders.OrderBy(a => a.Metadata.Order).ToArray();

		public override GlyphTextMarkerLocationInfo? GetLocation(DbgCodeBreakpoint breakpoint) {
			if (breakpoint is null)
				throw new ArgumentNullException(nameof(breakpoint));
			foreach (var lz in dbgBreakpointGlyphTextMarkerLocationProviders) {
				var loc = lz.Value.GetLocation(breakpoint);
				if (loc is not null)
					return loc;
			}
			return null;
		}
	}
}

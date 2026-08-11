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
using System.ComponentModel.Composition;
using NetSpy.Contracts.Debugger.Breakpoints.Code;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Menus;
using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.Breakpoints.Code.TextEditor {
	abstract class GlyphMarginCommand : MenuItemBase<DbgCodeBreakpoint> {
		protected sealed override object CachedContextKey => ContextKey;
		static readonly object ContextKey = new object();

		protected GlyphMarginOperations GlyphMarginOperations => glyphMarginOperations.Value;
		readonly Lazy<GlyphMarginOperations> glyphMarginOperations;

		protected GlyphMarginCommand(Lazy<GlyphMarginOperations> glyphMarginOperations) => this.glyphMarginOperations = glyphMarginOperations;

		protected sealed override DbgCodeBreakpoint? CreateContext(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(MenuConstants.GUIDOBJ_GLYPHMARGIN_GUID))
				return null;
			return context.Find<DbgCodeBreakpoint>();
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.GLYPHMARGIN_GUID, Header = "res:DeleteBreakpointCommand", Icon = DsImagesAttribute.CheckDot, Group = MenuConstants.GROUP_GLYPHMARGIN_DEBUG_CODEBPS_SETTINGS, Order = 0)]
	sealed class DeleteBreakpointGlyphMarginCommand : GlyphMarginCommand {
		[ImportingConstructor]
		DeleteBreakpointGlyphMarginCommand(Lazy<GlyphMarginOperations> glyphMarginOperations) : base(glyphMarginOperations) { }

		public override void Execute(DbgCodeBreakpoint breakpoint) => GlyphMarginOperations.Remove(breakpoint);
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.GLYPHMARGIN_GUID, Icon = DsImagesAttribute.ToggleAllBreakpoints, InputGestureText = "res:ShortCutKeyCtrlF9", Group = MenuConstants.GROUP_GLYPHMARGIN_DEBUG_CODEBPS_SETTINGS, Order = 10)]
	sealed class ToggleBreakpointGlyphMarginCommand : GlyphMarginCommand {
		[ImportingConstructor]
		ToggleBreakpointGlyphMarginCommand(Lazy<GlyphMarginOperations> glyphMarginOperations) : base(glyphMarginOperations) { }

		public override void Execute(DbgCodeBreakpoint breakpoint) => GlyphMarginOperations.Toggle(breakpoint);
		public override string? GetHeader(DbgCodeBreakpoint breakpoint) => breakpoint.IsEnabled ? NetSpy_Debugger_Resources.DisableBreakpointCommand2 : NetSpy_Debugger_Resources.EnableBreakpointCommand2;
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.GLYPHMARGIN_GUID, Header = "res:SettingsCommand2", Icon = DsImagesAttribute.Settings, Group = MenuConstants.GROUP_GLYPHMARGIN_DEBUG_CODEBPS_EDIT, Order = 0)]
	sealed class EditSettingsGlyphMarginCommand : GlyphMarginCommand {
		[ImportingConstructor]
		EditSettingsGlyphMarginCommand(Lazy<GlyphMarginOperations> glyphMarginOperations) : base(glyphMarginOperations) { }

		public override void Execute(DbgCodeBreakpoint breakpoint) => GlyphMarginOperations.EditSettings(breakpoint);
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.GLYPHMARGIN_GUID, Header = "res:ExportCommand", Icon = DsImagesAttribute.Open, Group = MenuConstants.GROUP_GLYPHMARGIN_DEBUG_CODEBPS_EXPORT, Order = 0)]
	sealed class ExportGlyphMarginCommand : GlyphMarginCommand {
		[ImportingConstructor]
		ExportGlyphMarginCommand(Lazy<GlyphMarginOperations> glyphMarginOperations) : base(glyphMarginOperations) { }

		public override void Execute(DbgCodeBreakpoint breakpoint) => GlyphMarginOperations.Export(breakpoint);
	}
}

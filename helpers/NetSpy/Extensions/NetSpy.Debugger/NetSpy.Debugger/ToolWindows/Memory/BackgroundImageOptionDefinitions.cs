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

using NetSpy.Contracts.BackgroundImage;
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Debugger.Properties;
using VSTE = Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.Debugger.ToolWindows.Memory {
	static class BackgroundImageOptionDefinitions {
		[ExportBackgroundImageOptionDefinition(BackgroundImageOptionDefinitionConstants.AttrOrder_HexEditorDebuggerMemory)]
		sealed class HexEditorProcessMemory : IBackgroundImageOptionDefinition {
			public string Id => "Hex Editor - Memory Window";
			public string DisplayName => NetSpy_Debugger_Resources.BgImgDisplayName_DebuggerMemory;
			public double UIOrder => BackgroundImageOptionDefinitionConstants.UIOrder_HexEditorDebuggerMemory;
			public bool UserVisible => true;
			public DefaultImageSettings? GetDefaultImageSettings() => null;
			public bool IsSupported(VSTE.ITextView textView) => false;
			public bool IsSupported(HexView hexView) => hexView.Roles.Contains(PredefinedHexViewRoles.HexEditorGroupDebuggerMemory);
		}
	}
}

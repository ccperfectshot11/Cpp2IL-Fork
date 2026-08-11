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

using NetSpy.Contracts.Settings.CodeEditor;
using NetSpy.Contracts.Settings.Dialog;
using NetSpy.Contracts.Text;

namespace NetSpy.Roslyn.Compiler {
	static class CodeEditorOptionsDefinitions {
#pragma warning disable CS0169
		[ExportCodeEditorOptionsDefinition("C#", ContentTypes.CSharpRoslyn, AppSettingsConstants.GUID_CODE_EDITOR_CSHARP_ROSLYN)]
		static readonly CodeEditorOptionsDefinition? csharpCodeEditorOptionsDefinition;

		[ExportCodeEditorOptionsDefinition("Visual Basic", ContentTypes.VisualBasicRoslyn, AppSettingsConstants.GUID_CODE_EDITOR_VISUAL_BASIC_ROSLYN)]
		static readonly CodeEditorOptionsDefinition? visualBasicCodeEditorOptionsDefinition;
#pragma warning restore CS0169
	}
}

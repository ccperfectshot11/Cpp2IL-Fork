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
using NetSpy.Contracts.Menus;
using NetSpy.Contracts.Scripting;
using NetSpy.Contracts.Settings.AppearanceCategory;
using NetSpy.Contracts.Text;
using NetSpy.Contracts.Text.Editor;
using NetSpy.Scripting.Roslyn.Common;

namespace NetSpy.Scripting.Roslyn.CSharp {
	interface ICSharpContent : IScriptContent {
	}

	[Export(typeof(ICSharpContent))]
	sealed class CSharpContent : ScriptContent, ICSharpContent {
		[ImportingConstructor]
		CSharpContent(IReplEditorProvider replEditorProvider, CSharpReplSettingsImpl replSettings, IServiceLocator serviceLocator)
			: base(replEditorProvider, CreateReplEditorOptions(), replSettings, serviceLocator, AppearanceCategoryConstants.TextEditor) {
		}

		protected override ScriptControlVM CreateScriptControlVM(IReplEditor replEditor, IServiceLocator serviceLocator, ReplSettings replSettings) =>
			new CSharpControlVM(replEditor, replSettings, serviceLocator);

		static ReplEditorOptions CreateReplEditorOptions() {
			var options = new ReplEditorOptions {
				MenuGuid = new Guid(MenuConstants.GUIDOBJ_REPL_TEXTEDITORCONTROL_GUID),
				ContentTypeString = ContentTypes.ReplCSharpRoslyn,
			};
			options.Roles.Add(PredefinedDsTextViewRoles.CSharpRepl);
			return options;
		}
	}
}

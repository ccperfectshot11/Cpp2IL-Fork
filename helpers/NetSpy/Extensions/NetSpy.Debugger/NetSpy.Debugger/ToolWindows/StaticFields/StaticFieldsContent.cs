/*
    Copyright (C) 2022 ElektroKill

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
using NetSpy.Contracts.Controls;
using NetSpy.Contracts.Text;
using NetSpy.Contracts.Text.Classification;
using NetSpy.Debugger.Evaluation.UI;
using NetSpy.Debugger.Evaluation.ViewModel;

namespace NetSpy.Debugger.ToolWindows.StaticFields {
	[Export(typeof(StaticFieldsContent))]
	sealed class StaticFieldsContent : VariablesWindowContentBase {
		public static readonly Guid VariablesWindowGuid = new Guid("B8715D35-8F1B-439C-AF7B-1849AF5E7130");

		[ImportingConstructor]
		StaticFieldsContent(IWpfCommandService wpfCommandService, VariablesWindowVMFactory variablesWindowVMFactory) =>
			Initialize(wpfCommandService, variablesWindowVMFactory, CreateVariablesWindowVMOptions());

		VariablesWindowVMOptions CreateVariablesWindowVMOptions() {
			var options = new VariablesWindowVMOptions() {
				VariablesWindowValueNodesProvider = new StaticFieldsVariablesWindowValueNodesProvider(),
				WindowContentType = ContentTypes.StaticFieldsWindow,
				NameColumnName = PredefinedTextClassifierTags.StaticFieldsWindowName,
				ValueColumnName = PredefinedTextClassifierTags.StaticFieldsWindowValue,
				TypeColumnName = PredefinedTextClassifierTags.StaticFieldsWindowType,
				VariablesWindowKind = VariablesWindowKind.StaticFields,
				VariablesWindowGuid = VariablesWindowGuid,
			};
			return options;
		}
	}
}

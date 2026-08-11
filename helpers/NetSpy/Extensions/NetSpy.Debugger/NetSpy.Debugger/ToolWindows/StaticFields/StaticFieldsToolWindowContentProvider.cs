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
using NetSpy.Contracts.ToolWindows.App;
using NetSpy.Debugger.Evaluation.UI;
using NetSpy.Debugger.Properties;

namespace NetSpy.Debugger.ToolWindows.StaticFields {
	[Export(typeof(IToolWindowContentProvider))]
	sealed class StaticFieldsToolWindowContentProvider : VariablesWindowToolWindowContentProviderBase {
		public static readonly Guid THE_GUID = new Guid("F46A6B46-4DA1-46BC-A279-2E2292069BE9");
		readonly Lazy<StaticFieldsContent> staticFieldsContent;

		[ImportingConstructor]
		StaticFieldsToolWindowContentProvider(Lazy<StaticFieldsContent> staticFieldsContent)
			: base(1, THE_GUID, AppToolWindowConstants.DEFAULT_CONTENT_ORDER_BOTTOM_DEBUGGER_STATIC_FIELDS) => this.staticFieldsContent = staticFieldsContent;

		protected override string GetWindowTitle(int windowIndex) {
			if (windowIndex != 0)
				throw new ArgumentOutOfRangeException(nameof(windowIndex));
			return NetSpy_Debugger_Resources.Window_StaticFields;
		}

		protected override Lazy<IVariablesWindowContent> CreateVariablesWindowContent(int windowIndex) {
			if (windowIndex != 0)
				throw new ArgumentOutOfRangeException(nameof(windowIndex));
			return new Lazy<IVariablesWindowContent>(() => staticFieldsContent.Value);
		}
	}
}

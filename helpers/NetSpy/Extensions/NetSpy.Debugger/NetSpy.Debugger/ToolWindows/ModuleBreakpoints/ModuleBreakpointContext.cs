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

using NetSpy.Contracts.Debugger.Text.NetSpy;
using NetSpy.Contracts.Text.Classification;
using NetSpy.Contracts.ToolWindows.Search;
using NetSpy.Debugger.UI;
using Microsoft.VisualStudio.Text.Classification;

namespace NetSpy.Debugger.ToolWindows.ModuleBreakpoints {
	interface IModuleBreakpointContext {
		UIDispatcher UIDispatcher { get; }
		IClassificationFormatMap ClassificationFormatMap { get; }
		ITextElementProvider TextElementProvider { get; }
		DbgTextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		ModuleBreakpointFormatter Formatter { get; }
		bool SyntaxHighlight { get; }
		SearchMatcher SearchMatcher { get; }
	}

	sealed class ModuleBreakpointContext : IModuleBreakpointContext {
		public UIDispatcher UIDispatcher { get; }
		public IClassificationFormatMap ClassificationFormatMap { get; }
		public ITextElementProvider TextElementProvider { get; }
		public DbgTextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		public ModuleBreakpointFormatter Formatter { get; set; }
		public bool SyntaxHighlight { get; set; }
		public SearchMatcher SearchMatcher { get; }

		public ModuleBreakpointContext(UIDispatcher uiDispatcher, IClassificationFormatMap classificationFormatMap, ITextElementProvider textElementProvider, SearchMatcher searchMatcher, ModuleBreakpointFormatter formatter) {
			UIDispatcher = uiDispatcher;
			ClassificationFormatMap = classificationFormatMap;
			TextElementProvider = textElementProvider;
			TextClassifierTextColorWriter = new DbgTextClassifierTextColorWriter();
			SearchMatcher = searchMatcher;
			Formatter = formatter;
		}
	}
}

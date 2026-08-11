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
using NetSpy.Contracts.ToolWindows.Search;
using NetSpy.Debugger.Text;
using NetSpy.Debugger.UI;
using NetSpy.Debugger.UI.Wpf;
using Microsoft.VisualStudio.Text.Classification;

namespace NetSpy.Debugger.ToolWindows.Threads {
	interface IThreadContext {
		UIDispatcher UIDispatcher { get; }
		IClassificationFormatMap ClassificationFormatMap { get; }
		ITextBlockContentInfoFactory TextBlockContentInfoFactory { get; }
		DbgTextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		int UIVersion { get; }
		ThreadFormatter Formatter { get; }
		bool SyntaxHighlight { get; }
		bool UseHexadecimal { get; }
		bool DigitSeparators { get; }
		bool FullString { get; }
		SearchMatcher SearchMatcher { get; }
		ClassifiedTextWriter ClassifiedTextWriter { get; }
	}

	sealed class ThreadContext : IThreadContext {
		public UIDispatcher UIDispatcher { get; }
		public IClassificationFormatMap ClassificationFormatMap { get; }
		public ITextBlockContentInfoFactory TextBlockContentInfoFactory { get; }
		public DbgTextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		public int UIVersion { get; set; }
		public ThreadFormatter Formatter { get; set; }
		public bool SyntaxHighlight { get; set; }
		public bool UseHexadecimal { get; set; }
		public bool DigitSeparators { get; set; }
		public bool FullString { get; set; }
		public SearchMatcher SearchMatcher { get; }
		public ClassifiedTextWriter ClassifiedTextWriter { get; }

		public ThreadContext(UIDispatcher uiDispatcher, IClassificationFormatMap classificationFormatMap, ITextBlockContentInfoFactory textBlockContentInfoFactory, SearchMatcher searchMatcher, ThreadFormatter formatter) {
			UIDispatcher = uiDispatcher;
			ClassificationFormatMap = classificationFormatMap;
			TextBlockContentInfoFactory = textBlockContentInfoFactory;
			TextClassifierTextColorWriter = new DbgTextClassifierTextColorWriter();
			SearchMatcher = searchMatcher;
			ClassifiedTextWriter = new ClassifiedTextWriter();
			Formatter = formatter;
		}
	}
}

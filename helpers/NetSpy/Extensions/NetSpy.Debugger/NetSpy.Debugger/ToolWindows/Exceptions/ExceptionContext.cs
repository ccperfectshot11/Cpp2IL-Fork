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

using NetSpy.Contracts.Debugger.Exceptions;
using NetSpy.Contracts.Debugger.Text.NetSpy;
using NetSpy.Contracts.Text.Classification;
using NetSpy.Contracts.ToolWindows.Search;
using NetSpy.Debugger.Exceptions;
using NetSpy.Debugger.UI;
using Microsoft.VisualStudio.Text.Classification;

namespace NetSpy.Debugger.ToolWindows.Exceptions {
	interface IExceptionContext {
		UIDispatcher UIDispatcher { get; }
		IClassificationFormatMap ClassificationFormatMap { get; }
		ITextElementProvider TextElementProvider { get; }
		DbgTextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		ExceptionFormatter Formatter { get; }
		bool SyntaxHighlight { get; }
		DbgExceptionSettingsService ExceptionSettingsService { get; }
		DbgExceptionFormatterService ExceptionFormatterService { get; }
		SearchMatcher SearchMatcher { get; }
	}

	sealed class ExceptionContext : IExceptionContext {
		public UIDispatcher UIDispatcher { get; }
		public IClassificationFormatMap ClassificationFormatMap { get; }
		public ITextElementProvider TextElementProvider { get; }
		public DbgTextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		public ExceptionFormatter Formatter { get; set; }
		public bool SyntaxHighlight { get; set; }
		public DbgExceptionSettingsService ExceptionSettingsService { get; }
		public DbgExceptionFormatterService ExceptionFormatterService { get; }
		public SearchMatcher SearchMatcher { get; }

		public ExceptionContext(UIDispatcher uiDispatcher, IClassificationFormatMap classificationFormatMap, ITextElementProvider textElementProvider, DbgExceptionSettingsService exceptionSettingsService, DbgExceptionFormatterService exceptionFormatterService, SearchMatcher searchMatcher, ExceptionFormatter formatter) {
			UIDispatcher = uiDispatcher;
			ClassificationFormatMap = classificationFormatMap;
			TextElementProvider = textElementProvider;
			TextClassifierTextColorWriter = new DbgTextClassifierTextColorWriter();
			ExceptionSettingsService = exceptionSettingsService;
			ExceptionFormatterService = exceptionFormatterService;
			SearchMatcher = searchMatcher;
			Formatter = formatter;
		}
	}
}

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

using NetSpy.Contracts.Bookmarks;
using NetSpy.Contracts.Text.Classification;
using NetSpy.Contracts.ToolWindows.Search;
using NetSpy.UI;
using Microsoft.VisualStudio.Text.Classification;

namespace NetSpy.Bookmarks.ToolWindows.Bookmarks {
	interface IBookmarkContext {
		UIDispatcher UIDispatcher { get; }
		IClassificationFormatMap ClassificationFormatMap { get; }
		ITextElementProvider TextElementProvider { get; }
		TextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		BookmarkFormatter Formatter { get; }
		BookmarkLocationFormatterOptions FormatterOptions { get; }
		bool SyntaxHighlight { get; }
		SearchMatcher SearchMatcher { get; }
	}

	sealed class BookmarkContext : IBookmarkContext {
		public UIDispatcher UIDispatcher { get; }
		public IClassificationFormatMap ClassificationFormatMap { get; }
		public ITextElementProvider TextElementProvider { get; }
		public TextClassifierTextColorWriter TextClassifierTextColorWriter { get; }
		public BookmarkFormatter Formatter { get; set; }
		public BookmarkLocationFormatterOptions FormatterOptions { get; set; }
		public bool SyntaxHighlight { get; set; }
		public SearchMatcher SearchMatcher { get; }

		public BookmarkContext(UIDispatcher uiDispatcher, IClassificationFormatMap classificationFormatMap, ITextElementProvider textElementProvider, SearchMatcher searchMatcher, BookmarkFormatter formatter) {
			UIDispatcher = uiDispatcher;
			ClassificationFormatMap = classificationFormatMap;
			TextElementProvider = textElementProvider;
			TextClassifierTextColorWriter = new TextClassifierTextColorWriter();
			SearchMatcher = searchMatcher;
			Formatter = formatter;
		}
	}
}

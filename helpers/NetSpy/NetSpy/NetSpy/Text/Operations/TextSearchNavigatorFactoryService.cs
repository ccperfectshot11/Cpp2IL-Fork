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
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;

namespace NetSpy.Text.Operations {
	[Export(typeof(ITextSearchNavigatorFactoryService))]
	sealed class TextSearchNavigatorFactoryService : ITextSearchNavigatorFactoryService {
		readonly ITextSearchService2 textSearchService2;

		[ImportingConstructor]
		TextSearchNavigatorFactoryService(ITextSearchService2 textSearchService2) => this.textSearchService2 = textSearchService2;

		public ITextSearchNavigator CreateSearchNavigator(ITextBuffer buffer) {
			if (buffer is null)
				throw new ArgumentNullException(nameof(buffer));
			return new TextSearchNavigator(buffer, textSearchService2);
		}
	}
}

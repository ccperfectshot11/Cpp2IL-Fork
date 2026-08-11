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
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using NetSpy.Contracts.Bookmarks;
using NetSpy.Contracts.Text;

namespace NetSpy.Bookmarks.Impl {
	abstract class BookmarkLocationFormatterService {
		public abstract BookmarkLocationFormatter GetFormatter(BookmarkLocation location);
	}

	[Export(typeof(BookmarkLocationFormatterService))]
	sealed class BookmarkLocationFormatterServiceImpl : BookmarkLocationFormatterService {
		readonly Lazy<BookmarkLocationFormatterProvider, IBookmarkLocationFormatterProviderMetadata>[] bookmarkLocationFormatterProviders;

		[ImportingConstructor]
		BookmarkLocationFormatterServiceImpl([ImportMany] IEnumerable<Lazy<BookmarkLocationFormatterProvider, IBookmarkLocationFormatterProviderMetadata>> bookmarkLocationFormatterProviders) =>
			this.bookmarkLocationFormatterProviders = bookmarkLocationFormatterProviders.ToArray();

		public override BookmarkLocationFormatter GetFormatter(BookmarkLocation location) {
			if (location is null)
				throw new ArgumentNullException(nameof(location));
			var type = location.Type;
			foreach (var lz in bookmarkLocationFormatterProviders) {
				if (Array.IndexOf(lz.Metadata.Types, type) >= 0) {
					var formatter = lz.Value.Create(location);
					if (formatter is not null)
						return formatter;
				}
			}
			return NullBookmarkLocationFormatter.Instance;
		}
	}

	sealed class NullBookmarkLocationFormatter : BookmarkLocationFormatter {
		public static readonly NullBookmarkLocationFormatter Instance = new NullBookmarkLocationFormatter();
		NullBookmarkLocationFormatter() { }
		public override void WriteLocation(ITextColorWriter output, BookmarkLocationFormatterOptions options) { }
		public override void WriteModule(ITextColorWriter output) { }
		public override void Dispose() { }
	}
}

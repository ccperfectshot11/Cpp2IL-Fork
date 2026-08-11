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
using NetSpy.Contracts.Settings.Dialog;
using NetSpy.Properties;

namespace NetSpy.Bookmarks.Settings {
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class BookmarksAppSettingsPageProvider : IAppSettingsPageProvider {
		readonly BookmarksSettingsImpl bookmarksSettingsImpl;

		[ImportingConstructor]
		BookmarksAppSettingsPageProvider(BookmarksSettingsImpl bookmarksSettingsImpl) => this.bookmarksSettingsImpl = bookmarksSettingsImpl;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new BookmarksAppSettingsPage(bookmarksSettingsImpl);
		}
	}

	sealed class BookmarksAppSettingsPage : AppSettingsPage {
		readonly BookmarksSettingsImpl _global_settings;

		internal static readonly Guid PageGuid = new Guid("908F7AAE-07DE-49AE-803D-7CB14065E128");
		public override Guid Guid => PageGuid;
		public BookmarksSettingsBase Settings { get; }
		public override double Order => AppSettingsConstants.ORDER_BOOKMARKS;
		public override string Title => NetSpy_Resources.BookmarksOptDlgTab;
		public override object? UIObject => this;

		public BookmarksAppSettingsPage(BookmarksSettingsImpl bookmarksSettingsImpl) {
			_global_settings = bookmarksSettingsImpl;
			Settings = bookmarksSettingsImpl.Clone();
		}

		public override void OnApply() => Settings.CopyTo(_global_settings);
	}
}

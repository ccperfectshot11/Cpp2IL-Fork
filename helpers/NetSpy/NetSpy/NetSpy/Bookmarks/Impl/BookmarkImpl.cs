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
using System.Collections.ObjectModel;
using NetSpy.Contracts.Bookmarks;
using NetSpy.Properties;

namespace NetSpy.Bookmarks.Impl {
	sealed class BookmarkImpl : Bookmark {
		public override int Id { get; }
		public override BookmarkLocation Location { get; }

		public override BookmarkSettings Settings {
			get {
				lock (lockObj)
					return settings;
			}
			set => owner.Modify(this, value);
		}
		BookmarkSettings settings;

		public override bool IsEnabled {
			get => Settings.IsEnabled;
			set {
				var settings = Settings;
				if (settings.IsEnabled == value)
					return;
				settings.IsEnabled = value;
				Settings = settings;
			}
		}

		public override string Name {
			get => Settings.Name;
			set {
				var settings = Settings;
				settings.Name = value;
				Settings = settings;
			}
		}

		public override ReadOnlyCollection<string> Labels {
			get => Settings.Labels;
			set {
				var settings = Settings;
				settings.Labels = value;
				Settings = settings;
			}
		}

		readonly object lockObj;
		readonly BookmarksServiceImpl owner;

		public BookmarkImpl(BookmarksServiceImpl owner, int id, BookmarkLocation location, BookmarkSettings settings) {
			lockObj = new object();
			this.owner = owner ?? throw new ArgumentNullException(nameof(owner));
			Id = id;
			Location = location ?? throw new ArgumentNullException(nameof(location));
			this.settings = settings;
			if (this.settings.Name is null)
				this.settings.Name = string.Format(NetSpy_Resources.BookmarkDefaultName, id.ToString());
		}

		internal void WriteSettings_BMThread(BookmarkSettings newSettings) {
			owner.Dispatcher.VerifyAccess();
			lock (lockObj) {
				settings = newSettings;
				if (settings.Name is null)
					settings.Name = string.Empty;
				if (settings.Labels is null)
					settings.Labels = emptyLabels;
			}
		}
		static readonly ReadOnlyCollection<string> emptyLabels = new ReadOnlyCollection<string>(Array.Empty<string>());

		public override void Remove() => owner.Remove(this);

		protected override void CloseCore() {
			owner.Dispatcher.VerifyAccess();
			Location.Close();
		}
	}
}

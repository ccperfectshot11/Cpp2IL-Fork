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
using NetSpy.Contracts.Bookmarks.DotNet;
using NetSpy.Contracts.Metadata;

namespace NetSpy.Bookmarks.DotNet {
	sealed class DotNetMethodBodyBookmarkLocationImpl : DotNetMethodBodyBookmarkLocation, IDotNetBookmarkLocation {
		public override string Type => PredefinedBookmarkLocationTypes.DotNetBody;
		public override ModuleId Module { get; }
		public override uint Token { get; }
		public override uint Offset { get; }

		public DotNetBookmarkLocationFormatter? Formatter { get; set; }

		public DotNetMethodBodyBookmarkLocationImpl(ModuleId module, uint token, uint offset) {
			Module = module;
			Token = token;
			Offset = offset;
		}

		protected override void CloseCore() { }

		public override bool Equals(object? obj) =>
			obj is DotNetMethodBodyBookmarkLocationImpl other &&
			Module == other.Module &&
			Token == other.Token &&
			Offset == other.Offset;

		public override int GetHashCode() => Module.GetHashCode() ^ (int)Token ^ (int)Offset;
	}
}

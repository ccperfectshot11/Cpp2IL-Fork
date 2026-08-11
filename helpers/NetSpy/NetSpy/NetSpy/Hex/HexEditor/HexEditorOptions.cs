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
using NetSpy.Contracts.Resources;
using NetSpy.Contracts.Settings.HexEditor;
using NetSpy.Contracts.Settings.HexGroups;
using NetSpy.Hex.Settings;

namespace NetSpy.Hex.HexEditor {
	sealed class HexEditorOptions : CommonEditorOptions {
		public Guid Guid { get; }
		public string Name { get; }

		HexEditorOptions(HexViewOptionsGroup group, string subGroup, Guid guid, string? name)
			: base(group, subGroup) {
			Guid = guid;
			Name = name ?? throw new ArgumentOutOfRangeException(nameof(name));
		}

		public static HexEditorOptions? TryCreate(HexViewOptionsGroup group, IHexEditorOptionsDefinitionMetadata md) {
			if (group is null)
				throw new ArgumentNullException(nameof(group));
			if (md is null)
				throw new ArgumentNullException(nameof(md));

			if (md.SubGroup is null)
				return null;
			var subGroup = md.SubGroup;
			if (subGroup is null)
				return null;

			if (md.Guid is null)
				return null;
			if (!Guid.TryParse(md.Guid, out var guid))
				return null;

			if (md.Name is null)
				return null;

			return new HexEditorOptions(group, subGroup, guid, ResourceHelper.GetString(md.Type.Assembly, md.Name));
		}
	}
}

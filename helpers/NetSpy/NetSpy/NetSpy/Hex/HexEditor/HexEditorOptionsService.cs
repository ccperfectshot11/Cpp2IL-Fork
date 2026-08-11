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
using NetSpy.Contracts.Settings.HexEditor;
using NetSpy.Contracts.Settings.HexGroups;

namespace NetSpy.Hex.HexEditor {
	abstract class HexEditorOptionsService {
		public abstract HexEditorOptions[] Options { get; }
	}

	[Export(typeof(HexEditorOptionsService))]
	sealed class HexEditorOptionsServiceImpl : HexEditorOptionsService {
		public override HexEditorOptions[] Options { get; }

		[ImportingConstructor]
		HexEditorOptionsServiceImpl(HexViewOptionsGroupService hexViewOptionsGroupService, [ImportMany] IEnumerable<Lazy<HexEditorOptionsDefinition, IHexEditorOptionsDefinitionMetadata>> hexEditorOptionsDefinitions) {
			var group = hexViewOptionsGroupService.GetGroup(PredefinedHexViewGroupNames.HexEditor);
			Options = hexEditorOptionsDefinitions.Select(a => HexEditorOptions.TryCreate(group, a.Metadata)).OfType< HexEditorOptions>().ToArray();
		}
	}
}

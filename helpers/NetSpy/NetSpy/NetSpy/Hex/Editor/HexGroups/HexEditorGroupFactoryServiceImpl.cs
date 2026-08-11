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
using System.Linq;
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Editor.HexGroups;

namespace NetSpy.Hex.Editor.HexGroups {
	[Export(typeof(HexEditorGroupFactoryService))]
	sealed class HexEditorGroupFactoryServiceImpl : HexEditorGroupFactoryService {
		readonly HexEditorFactoryService hexEditorFactoryService;

		[ImportingConstructor]
		HexEditorGroupFactoryServiceImpl(HexEditorFactoryService hexEditorFactoryService) => this.hexEditorFactoryService = hexEditorFactoryService;

		public override WpfHexViewHost Create(HexBuffer buffer, string group, string subGroup, Guid? menuGuid) {
			if (buffer is null)
				throw new ArgumentNullException(nameof(buffer));
			if (group is null)
				throw new ArgumentNullException(nameof(group));
			if (subGroup is null)
				throw new ArgumentNullException(nameof(subGroup));

			var roles = hexEditorFactoryService.CreateTextViewRoleSet(hexEditorFactoryService.DefaultRoles.Concat(new[] { group, subGroup }));
			var options = new HexViewCreatorOptions {
				MenuGuid = menuGuid,
			};
			var hexView = hexEditorFactoryService.Create(buffer, roles, options);
			GetDefaultLocalOptions(hexView).WriteTo(hexView);
			return hexEditorFactoryService.CreateHost(hexView, false);
		}

		public override LocalGroupOptions GetDefaultLocalOptions(HexView hexView) {
			if (hexView is null)
				throw new ArgumentNullException(nameof(hexView));
			var options = new LocalGroupOptions();
			options.ShowOffsetColumn = true;
			options.ShowValuesColumn = true;
			options.ShowAsciiColumn = true;
			options.StartPosition = hexView.Buffer.Span.Start;
			options.EndPosition = hexView.Buffer.Span.End;
			options.BasePosition = HexPosition.Zero;
			options.UseRelativePositions = false;
			options.OffsetBitSize = 0;
			options.HexValuesDisplayFormat = HexValuesDisplayFormat.HexByte;
			options.BytesPerLine = 0;
			return options;
		}
	}
}

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

using System.ComponentModel.Composition;
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.Hex.Files;
using NetSpy.Contracts.Hex.Files.NetSpy;
using NetSpy.Contracts.Hex.Files.DotNet;
using NetSpy.Contracts.Images;

namespace NetSpy.Hex.Files.NetSpy {
	[Export(typeof(HexFileImageReferenceProvider))]
	sealed class DotNetHexFileImageReferenceProvider : HexFileImageReferenceProvider {
		public override ImageReference? GetImage(ComplexData structure, HexPosition position) {
			if (structure is MultiResourceUnicodeNameAndOffsetData nameOffset)
				return GetImageReference(nameOffset);

			return null;
		}

		ImageReference? GetImageReference(MultiResourceUnicodeNameAndOffsetData nameOffset) {
			var name = nameOffset.ResourceName.Data.String.Data.ReadValue();
			return ImageReferenceUtils.GetImageReference(name);
		}
	}
}

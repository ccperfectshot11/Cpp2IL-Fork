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
using NetSpy.Contracts.Hex.Editor;
using VSUTIL = Microsoft.VisualStudio.Utilities;

namespace NetSpy.Hex.Editor {
	[Export(typeof(HexSpaceReservationStackProvider))]
	sealed class HexSpaceReservationStackProviderImpl : HexSpaceReservationStackProvider {
		readonly string[] spaceReservationManagerNames;

		[ImportingConstructor]
		HexSpaceReservationStackProviderImpl([ImportMany] IEnumerable<Lazy<HexSpaceReservationManagerDefinition, VSUTIL.IOrderable>> spaceReservationManagerDefinitions) => spaceReservationManagerNames = VSUTIL.Orderer.Order(spaceReservationManagerDefinitions).Select(a => a.Metadata.Name).ToArray();

		public override HexSpaceReservationStack Create(WpfHexView wpfHexView) {
			if (wpfHexView is null)
				throw new ArgumentNullException(nameof(wpfHexView));
			return wpfHexView.Properties.GetOrCreateSingletonProperty(typeof(HexSpaceReservationStackImpl), () => new HexSpaceReservationStackImpl(wpfHexView, spaceReservationManagerNames));
		}
	}
}

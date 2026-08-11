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
using NetSpy.Contracts.Hex.Adornments;
using NetSpy.Contracts.Hex.Editor;

namespace NetSpy.Hex.Adornments {
	[Export(typeof(HexToolTipProviderFactory))]
	sealed class HexToolTipProviderFactoryImpl : HexToolTipProviderFactory {
		public override HexToolTipProvider GetToolTipProvider(HexView hexView) {
			if (hexView is null)
				throw new ArgumentNullException(nameof(hexView));
			var wpfHexView = hexView as WpfHexView;
			if (wpfHexView is null)
				throw new ArgumentException();
			return wpfHexView.Properties.GetOrCreateSingletonProperty(typeof(HexToolTipProviderImpl), () => new HexToolTipProviderImpl(wpfHexView));
		}
	}
}

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
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.Hex.Editor;

namespace NetSpy.Hex.Editor {
	static class SelectionUtilities {
		public static HexBufferSpan GetLineAnchorSpan(HexSelection selection) {
			if (selection is null)
				throw new ArgumentNullException(nameof(selection));
			if (selection.IsEmpty)
				return selection.HexView.Caret.ContainingHexViewLine.BufferSpan;
			var anchorExtent = selection.HexView.GetHexViewLineContainingBufferPosition(selection.AnchorPoint).BufferSpan;
			if (selection.AnchorPoint >= selection.ActivePoint) {
				if (anchorExtent.Start == selection.AnchorPoint && selection.AnchorPoint > selection.HexView.BufferLines.BufferStart)
					anchorExtent = selection.HexView.GetHexViewLineContainingBufferPosition(selection.AnchorPoint - 1).BufferSpan;
			}
			return anchorExtent;
		}
	}
}

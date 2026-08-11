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

using System.Collections.Generic;
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.MVVM;

namespace NetSpy.AsmEditor.Hex.PE {
	abstract class HexVM : ViewModelBase {
		public abstract string Name { get; }
		public abstract IEnumerable<HexField> HexFields { get; }
		public HexSpan Span { get; }

		protected HexVM(HexSpan span) => Span = span;

		public virtual void OnBufferChanged(NormalizedHexChangeCollection changes) {
			foreach (var field in HexFields)
				field.OnBufferChanged(changes);
		}
	}
}

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

using System.Collections.ObjectModel;
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.Hex.Files;
using NetSpy.Contracts.Hex.Files.DotNet;

namespace NetSpy.Hex.Files.DotNet {
	sealed class InvalidMethodBodyImpl : InvalidMethodBody {
		public override StructField<VirtualArrayData<ByteData>> Instructions { get; }

		protected override BufferField[] Fields { get; }

		public InvalidMethodBodyImpl(DotNetMethodProvider methodProvider, HexBufferSpan span, ReadOnlyCollection<uint> tokens)
			: base(methodProvider, span, tokens) {
			var buffer = span.Buffer;
			var pos = span.Start.Position;
			Instructions = new StructField<VirtualArrayData<ByteData>>(TinyMethodBodyImpl.InstructionsFieldName, ArrayData.CreateVirtualByteArray(HexBufferSpan.FromBounds(span.Start, span.End), TinyMethodBodyImpl.InstructionsFieldName));
			Fields = new BufferField[] {
				Instructions,
			};
		}
	}
}

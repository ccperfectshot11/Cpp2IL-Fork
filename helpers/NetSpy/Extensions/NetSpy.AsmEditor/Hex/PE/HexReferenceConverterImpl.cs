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
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Hex.Editor;

namespace NetSpy.AsmEditor.Hex.PE {
	[Export(typeof(HexReferenceConverter))]
	sealed class HexReferenceConverterImpl : HexReferenceConverter {
		readonly BufferToDocumentNodeService bufferToDocumentNodeService;

		[ImportingConstructor]
		HexReferenceConverterImpl(BufferToDocumentNodeService bufferToDocumentNodeService) => this.bufferToDocumentNodeService = bufferToDocumentNodeService;

		public override object? Convert(HexView hexView, object reference) {
			if (reference is HexFieldReference fieldRef)
				return ConvertFieldReference(fieldRef);

			return reference;
		}

		DocumentTreeNodeData? ConvertFieldReference(HexFieldReference fieldRef) {
			var peNode = bufferToDocumentNodeService.FindPENode(fieldRef.File);
			if (peNode is null)
				return null;

			return peNode.FindNode(fieldRef.Structure, fieldRef.Field);
		}
	}
}

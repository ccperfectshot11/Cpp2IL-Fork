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
using NetSpy.AsmEditor.Hex.PE;
using NetSpy.AsmEditor.Properties;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Text;

namespace NetSpy.AsmEditor.Hex.Nodes {
	sealed class ImageOptionalHeader64Node : HexNode {
		public override Guid Guid => new Guid(DocumentTreeViewConstants.IMGOPTHEADER64_NODE_GUID);
		public override NodePathName NodePathName => new NodePathName(Guid);
		public override object VMObject => imageOptionalHeader64VM;
		protected override ImageReference IconReference => DsImages.BinaryFile;

		protected override IEnumerable<HexVM> HexVMs {
			get { yield return imageOptionalHeader64VM; }
		}

		readonly ImageOptionalHeader64VM imageOptionalHeader64VM;

		public ImageOptionalHeader64Node(ImageOptionalHeader64VM optHdr)
			: base(optHdr.Span) => imageOptionalHeader64VM = optHdr;

		protected override void WriteCore(ITextColorWriter output, DocumentNodeWriteOptions options) =>
			output.Write(BoxedTextColor.HexPeOptionalHeader64, NetSpy_AsmEditor_Resources.HexNode_OptHeader64);
	}
}

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
	sealed class ImageOptionalHeader32Node : HexNode {
		protected override ImageReference IconReference => DsImages.BinaryFile;
		public override Guid Guid => new Guid(DocumentTreeViewConstants.IMGOPTHEADER32_NODE_GUID);
		public override NodePathName NodePathName => new NodePathName(Guid);
		public override object VMObject => imageOptionalHeader32VM;

		protected override IEnumerable<HexVM> HexVMs {
			get { yield return imageOptionalHeader32VM; }
		}

		readonly ImageOptionalHeader32VM imageOptionalHeader32VM;

		public ImageOptionalHeader32Node(ImageOptionalHeader32VM optHdr)
			: base(optHdr.Span) => imageOptionalHeader32VM = optHdr;

		protected override void WriteCore(ITextColorWriter output, DocumentNodeWriteOptions options) =>
			output.Write(BoxedTextColor.HexPeOptionalHeader32, NetSpy_AsmEditor_Resources.HexNode_OptHeader32);
	}
}

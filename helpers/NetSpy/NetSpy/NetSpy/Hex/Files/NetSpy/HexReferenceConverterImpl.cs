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
using dnlib.DotNet;
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents;
using NetSpy.Contracts.Documents.Tabs;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Files;

namespace NetSpy.Hex.Files.NetSpy {
	[Export(typeof(HexReferenceConverter))]
	sealed class HexReferenceConverterImpl : HexReferenceConverter {
		readonly BufferToDocumentNodeService bufferToDocumentNodeService;

		[ImportingConstructor]
		HexReferenceConverterImpl(BufferToDocumentNodeService bufferToDocumentNodeService) => this.bufferToDocumentNodeService = bufferToDocumentNodeService;

		public override object? Convert(HexView hexView, object reference) {
			if (reference is HexMethodReference methodRef)
				return ConvertMethodReference(methodRef);

			return reference;
		}

		MethodStatementReference? ConvertMethodReference(HexMethodReference methodRef) {
			var docNode = bufferToDocumentNodeService.Find(methodRef.File);
			if (docNode is null)
				return null;
			var module = docNode.Document.ModuleDef;
			if (module is null)
				return null;
			var method = module.ResolveToken(methodRef.Token) as MethodDef;
			if (method is null)
				return null;

			return new MethodStatementReference(method, methodRef.Offset);
		}
	}

	sealed class HexMethodReference {
		public HexBufferFile File { get; }
		public uint Token { get; }
		public uint? Offset { get; }

		public HexMethodReference(HexBufferFile file, uint token, uint? offset) {
			File = file ?? throw new ArgumentNullException(nameof(file));
			Token = token;
			Offset = offset;
		}
	}

	abstract class BufferToDocumentNodeService {
		public abstract DsDocumentNode? Find(HexBufferFile file);
	}

	[Export(typeof(BufferToDocumentNodeService))]
	sealed class BufferToDocumentNodeServiceImpl : BufferToDocumentNodeService {
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		BufferToDocumentNodeServiceImpl(IDocumentTabService documentTabService) => this.documentTabService = documentTabService;

		public override DsDocumentNode? Find(HexBufferFile file) {
			if (file is null)
				throw new ArgumentNullException(nameof(file));
			if (file.Filename == string.Empty)
				return null;
			var doc = documentTabService.DocumentTreeView.DocumentService.Find(new FilenameKey(file.Filename));
			if (doc is null)
				return null;
			return documentTabService.DocumentTreeView.FindNode(doc);
		}
	}
}

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
using NetSpy.Contracts.Decompiler;
using NetSpy.Contracts.Documents.Tabs.DocViewer;
using NetSpy.Contracts.Text;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Documents.Tabs.DocViewer {
	[Export(typeof(IDocumentWriterProvider))]
	[Name(PredefinedDocumentWriterProviderNames.DefaultXmlXaml)]
	[ContentType(ContentTypes.Xaml)]
	[ContentType(ContentTypes.Xml)]
	sealed class XmlDocumentWriterProvider : IDocumentWriterProvider {
		public IDocumentWriter? Create(IContentType contentType) => new XmlDocumentWriter(contentType.IsOfType(ContentTypes.Xaml));
	}

	sealed class XmlDocumentWriter : IDocumentWriter {
		readonly bool isXaml;

		public XmlDocumentWriter(bool isXaml) => this.isXaml = isXaml;

		public void Write(IDecompilerOutput output, string text) {
			try {
				var parser = new XmlParser(text, isXaml);
				parser.Parse();
				parser.WriteTo(output);
			}
			catch (Exception ex) {
				output.WriteLine($"<!-- Error parsing XML/XAML: {ex.Message} -->", BoxedTextColor.XmlComment);
				output.Write(text, BoxedTextColor.Text);
			}
		}
	}
}

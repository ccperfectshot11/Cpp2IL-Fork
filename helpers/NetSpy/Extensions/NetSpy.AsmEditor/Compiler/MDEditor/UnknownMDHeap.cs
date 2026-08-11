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
using dnlib.DotNet.MD;

namespace NetSpy.AsmEditor.Compiler.MDEditor {
	sealed class UnknownMDHeap : MDHeap {
		readonly MetadataEditor mdEditor;
		readonly DotNetStream stream;

		public UnknownMDHeap(MetadataEditor mdEditor, DotNetStream stream) {
			this.mdEditor = mdEditor ?? throw new ArgumentNullException(nameof(mdEditor));
			this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
		}

		public override bool MustRewriteHeap() => false;
		public override bool ExistsInMetadata => stream.StreamHeader is not null;
	}
}

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
using System.ComponentModel.Composition;
using System.Linq;
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.Hex.Files;
using VSUTIL = Microsoft.VisualStudio.Utilities;

namespace NetSpy.Hex.Files {
	[Export(typeof(HexBufferFileServiceFactory))]
	sealed class HexBufferFileServiceFactoryImpl : HexBufferFileServiceFactory {
		public override event EventHandler<BufferFileServiceCreatedEventArgs>? BufferFileServiceCreated;
		readonly Lazy<StructureProviderFactory, VSUTIL.IOrderable>[] structureProviderFactories;
		readonly Lazy<BufferFileHeadersProviderFactory>[] bufferFileHeadersProviderFactories;

		[ImportingConstructor]
		HexBufferFileServiceFactoryImpl([ImportMany] IEnumerable<Lazy<StructureProviderFactory, VSUTIL.IOrderable>> structureProviderFactories, [ImportMany] IEnumerable<Lazy<BufferFileHeadersProviderFactory>> bufferFileHeadersProviderFactories) {
			this.structureProviderFactories = VSUTIL.Orderer.Order(structureProviderFactories).ToArray();
			this.bufferFileHeadersProviderFactories = bufferFileHeadersProviderFactories.ToArray();
		}

		public override HexBufferFileService Create(HexBuffer buffer) {
			if (buffer is null)
				throw new ArgumentNullException(nameof(buffer));
			if (buffer.Properties.TryGetProperty(typeof(HexBufferFileServiceImpl), out HexBufferFileServiceImpl impl))
				return impl;
			impl = new HexBufferFileServiceImpl(buffer, structureProviderFactories, bufferFileHeadersProviderFactories);
			buffer.Properties.AddProperty(typeof(HexBufferFileServiceImpl), impl);
			BufferFileServiceCreated?.Invoke(this, new BufferFileServiceCreatedEventArgs(impl));
			return impl;
		}
	}
}

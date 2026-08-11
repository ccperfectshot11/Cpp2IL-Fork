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
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Hex.MEF;
using VSUTIL = Microsoft.VisualStudio.Utilities;

namespace NetSpy.Hex.Editor {
	[Export(typeof(HexAdornmentLayerDefinitionService))]
	sealed class HexAdornmentLayerDefinitionServiceImpl : HexAdornmentLayerDefinitionService {
		readonly Lazy<HexAdornmentLayerDefinition, IAdornmentLayersMetadata>[] adornmentLayerDefinitions;

		[ImportingConstructor]
		HexAdornmentLayerDefinitionServiceImpl([ImportMany] IEnumerable<Lazy<HexAdornmentLayerDefinition, IAdornmentLayersMetadata>> adornmentLayerDefinitions) => this.adornmentLayerDefinitions = VSUTIL.Orderer.Order(adornmentLayerDefinitions).ToArray();

		public override MetadataAndOrder<IAdornmentLayersMetadata>? GetLayerDefinition(string name) {
			for (int i = 0; i < adornmentLayerDefinitions.Length; i++) {
				var def = adornmentLayerDefinitions[i];
				if (StringComparer.Ordinal.Equals(name, def.Metadata.Name))
					return new MetadataAndOrder<IAdornmentLayersMetadata>(def.Metadata, i);
			}
			return null;
		}
	}
}

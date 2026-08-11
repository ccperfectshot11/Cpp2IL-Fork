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
using NetSpy.Text.MEF;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Text.Editor {
	[Export(typeof(IAdornmentLayerDefinitionService))]
	sealed class AdornmentLayerDefinitionService : IAdornmentLayerDefinitionService {
		readonly Lazy<AdornmentLayerDefinition, IAdornmentLayersMetadata>[] adornmentLayerDefinitions;

		[ImportingConstructor]
		AdornmentLayerDefinitionService([ImportMany] IEnumerable<Lazy<AdornmentLayerDefinition, IAdornmentLayersMetadata>> adornmentLayerDefinitions) => this.adornmentLayerDefinitions = Orderer.Order(adornmentLayerDefinitions).ToArray();

		public MetadataAndOrder<IAdornmentLayersMetadata>? GetLayerDefinition(string name) {
			for (int i = 0; i < adornmentLayerDefinitions.Length; i++) {
				var def = adornmentLayerDefinitions[i];
				if (StringComparer.Ordinal.Equals(name, def.Metadata.Name))
					return new MetadataAndOrder<IAdornmentLayersMetadata>(def.Metadata, i);
			}
			return null;
		}
	}
}

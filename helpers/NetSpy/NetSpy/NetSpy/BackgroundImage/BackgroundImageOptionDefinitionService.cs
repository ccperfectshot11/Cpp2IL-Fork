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
using NetSpy.Contracts.BackgroundImage;
using NetSpy.Contracts.Hex.Editor;
using Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.BackgroundImage {
	interface IBackgroundImageOptionDefinitionService {
		Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata> GetOptionDefinition(IWpfTextView wpfTextView);
		Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata> GetOptionDefinition(WpfHexView wpfHexView);
		Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata>[] AllSettings { get; }
	}

	[Export(typeof(IBackgroundImageOptionDefinitionService))]
	sealed class BackgroundImageOptionDefinitionService : IBackgroundImageOptionDefinitionService {
		readonly Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata>[] backgroundImageOptionDefinitions;

		public Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata>[] AllSettings => backgroundImageOptionDefinitions;

		[ImportingConstructor]
		BackgroundImageOptionDefinitionService([ImportMany] IEnumerable<Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata>> backgroundImageOptionDefinitions) => this.backgroundImageOptionDefinitions = backgroundImageOptionDefinitions.OrderBy(a => a.Metadata.Order).ToArray();

		public Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata> GetOptionDefinition(IWpfTextView wpfTextView) {
			foreach (var lz in backgroundImageOptionDefinitions) {
				if (lz.Value.IsSupported(wpfTextView))
					return lz;
			}
			throw new InvalidOperationException();
		}

		public Lazy<IBackgroundImageOptionDefinition, IBackgroundImageOptionDefinitionMetadata> GetOptionDefinition(WpfHexView wpfHexView) {
			foreach (var lz in backgroundImageOptionDefinitions) {
				if (lz.Value.IsSupported(wpfHexView))
					return lz;
			}
			throw new InvalidOperationException();
		}
	}
}

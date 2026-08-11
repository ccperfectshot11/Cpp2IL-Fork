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
using System.Windows;
using System.Windows.Controls;
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Formatting;
using NetSpy.Contracts.Hex.Tagging;
using NetSpy.Contracts.Images;
using VSUTIL = Microsoft.VisualStudio.Utilities;

namespace NetSpy.Hex.Editor {
	[Export(typeof(HexGlyphFactoryProvider))]
	[VSUTIL.Name(PredefinedHexGlyphFactoryProviderNames.HexImageReference)]
	[HexTagType(typeof(HexImageReferenceTag))]
	sealed class HexImageReferenceGlyphFactoryProvider : HexGlyphFactoryProvider {
		public override HexGlyphFactory? GetGlyphFactory(WpfHexView view, WpfHexViewMargin margin) => new HexImageReferenceGlyphFactory();
	}

	sealed class HexImageReferenceGlyphFactory : HexGlyphFactory {
		public override UIElement? GenerateGlyph(WpfHexViewLine line, HexGlyphTag tag) {
			var glyphTag = tag as HexImageReferenceTag;
			if (glyphTag is null)
				return null;

			const double DEFAULT_IMAGE_LENGTH = 16;
			const double EXTRA_LENGTH = 2;
			double imageLength = Math.Min(DEFAULT_IMAGE_LENGTH, line.Height + EXTRA_LENGTH);

			var image = new DsImage {
				Width = imageLength,
				Height = imageLength,
				ImageReference = glyphTag.ImageReference,
			};
			Panel.SetZIndex(image, glyphTag.ZIndex);
			return image;
		}
	}
}

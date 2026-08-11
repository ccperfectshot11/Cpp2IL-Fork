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
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Tagging;

namespace NetSpy.Hex.Editor {
	[Export(typeof(HexViewTaggerProvider))]
	[HexTagType(typeof(HexToolTipStructureSpanTag))]
	sealed class HexToolTipStructureSpanTaggerProvider : HexViewTaggerProvider {
		readonly HexStructureInfoAggregatorFactory hexStructureInfoAggregatorFactory;

		[ImportingConstructor]
		HexToolTipStructureSpanTaggerProvider(HexStructureInfoAggregatorFactory hexStructureInfoAggregatorFactory) => this.hexStructureInfoAggregatorFactory = hexStructureInfoAggregatorFactory;

		public override IHexTagger<T>? CreateTagger<T>(HexView hexView, HexBuffer buffer) =>
			new HexToolTipStructureSpanTagger(hexStructureInfoAggregatorFactory.Create(hexView)) as IHexTagger<T>;
	}

	sealed class HexToolTipStructureSpanTagger : HexTagger<HexToolTipStructureSpanTag> {
		readonly HexStructureInfoAggregator hexStructureInfoAggregator;

		public HexToolTipStructureSpanTagger(HexStructureInfoAggregator hexStructureInfoAggregator) => this.hexStructureInfoAggregator = hexStructureInfoAggregator ?? throw new ArgumentNullException(nameof(hexStructureInfoAggregator));

		public override event EventHandler<HexBufferSpanEventArgs>? TagsChanged { add { } remove { } }

		public override IEnumerable<IHexTextTagSpan<HexToolTipStructureSpanTag>> GetTags(HexTaggerContext context) {
			yield break;
		}

		public override IEnumerable<IHexTagSpan<HexToolTipStructureSpanTag>> GetTags(NormalizedHexBufferSpanCollection spans) {
			foreach (var span in spans) {
				var position = span.Start;
				foreach (var info in hexStructureInfoAggregator.GetFields(position)) {
					object? toolTip = null, reference = null;
					if (info.Value.IsCurrentField) {
						toolTip = info.Provider.GetToolTip(position);
						reference = info.Provider.GetReference(position);
					}
					yield return new HexTagSpan<HexToolTipStructureSpanTag>(span, HexSpanSelectionFlags.Selection,
						new HexToolTipStructureSpanTag(info.Value.BufferSpan, toolTip, reference));
				}
			}
		}
	}
}

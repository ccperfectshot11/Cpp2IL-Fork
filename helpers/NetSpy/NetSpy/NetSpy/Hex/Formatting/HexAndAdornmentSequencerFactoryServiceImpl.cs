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
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Formatting;
using NetSpy.Contracts.Hex.Tagging;

namespace NetSpy.Hex.Formatting {
	[Export(typeof(HexAndAdornmentSequencerFactoryService))]
	sealed class HexAndAdornmentSequencerFactoryServiceImpl : HexAndAdornmentSequencerFactoryService {
		readonly HexViewTagAggregatorFactoryService hexViewTagAggregatorFactoryService;

		[ImportingConstructor]
		HexAndAdornmentSequencerFactoryServiceImpl(HexViewTagAggregatorFactoryService hexViewTagAggregatorFactoryService) => this.hexViewTagAggregatorFactoryService = hexViewTagAggregatorFactoryService;

		public override HexAndAdornmentSequencer Create(HexView view) {
			if (view is null)
				throw new ArgumentNullException(nameof(view));
			return view.Properties.GetOrCreateSingletonProperty(typeof(HexAndAdornmentSequencer), () => new HexAndAdornmentSequencerImpl(view, hexViewTagAggregatorFactoryService.CreateTagAggregator<HexSpaceNegotiatingAdornmentTag>(view)));
		}
	}
}

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
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Tagging;

namespace NetSpy.Hex.Tagging {
	sealed class HexViewTagAggregator<T> : TagAggregator<T> where T : HexTag {
		readonly HexTaggerFactory hexTaggerFactory;
		readonly HexView hexView;

		public HexViewTagAggregator(HexTaggerFactory hexTaggerFactory, HexView hexView)
			: base(hexView.Buffer) {
			this.hexTaggerFactory = hexTaggerFactory;
			this.hexView = hexView;
			hexView.Closed += HexView_Closed;
			Initialize();
		}

		void HexView_Closed(object? sender, EventArgs e) => Dispose();

		public override void Dispose() {
			base.Dispose();
			hexView.Closed -= HexView_Closed;
		}

		protected override bool CanRaiseBatchedTagsChanged => !hexView.InLayout;
		protected override IEnumerable<IHexTagger<T>> CreateTaggers() => hexTaggerFactory.Create<T>(hexView, Buffer);
	}
}

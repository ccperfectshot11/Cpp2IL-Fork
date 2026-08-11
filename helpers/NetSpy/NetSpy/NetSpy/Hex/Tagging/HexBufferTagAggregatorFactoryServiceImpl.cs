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
using NetSpy.Contracts.Hex;
using NetSpy.Contracts.Hex.Tagging;

namespace NetSpy.Hex.Tagging {
	[Export(typeof(HexBufferTagAggregatorFactoryService))]
	sealed class HexBufferTagAggregatorFactoryServiceImpl : HexBufferTagAggregatorFactoryService {
		readonly HexTaggerFactory hexTaggerFactory;

		[ImportingConstructor]
		HexBufferTagAggregatorFactoryServiceImpl(HexTaggerFactory hexTaggerFactory) => this.hexTaggerFactory = hexTaggerFactory;

		public override HexTagAggregator<T> CreateTagAggregator<T>(HexBuffer buffer) {
			if (buffer is null)
				throw new ArgumentNullException(nameof(buffer));
			return new HexBufferTagAggregator<T>(hexTaggerFactory, buffer).HexTagAggregator;
		}
	}
}

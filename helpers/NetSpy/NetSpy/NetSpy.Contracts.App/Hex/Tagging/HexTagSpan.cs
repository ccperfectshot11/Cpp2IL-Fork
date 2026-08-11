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

namespace NetSpy.Contracts.Hex.Tagging {
	/// <summary>
	/// Hex tag and span
	/// </summary>
	/// <typeparam name="T">Tag type</typeparam>
	public interface IHexTagSpan<out T> where T : HexTag {
		/// <summary>
		/// Gets the span
		/// </summary>
		HexBufferSpan Span { get; }

		/// <summary>
		/// Gets the flags
		/// </summary>
		HexSpanSelectionFlags Flags { get; }

		/// <summary>
		/// Gets the tag
		/// </summary>
		T Tag { get; }
	}

	/// <summary>
	/// Hex tag and span
	/// </summary>
	/// <typeparam name="T">Tag type</typeparam>
	public class HexTagSpan<T> : IHexTagSpan<T> where T : HexTag {
		/// <summary>
		/// Gets the span
		/// </summary>
		public HexBufferSpan Span { get; }

		/// <summary>
		/// Gets the flags
		/// </summary>
		public HexSpanSelectionFlags Flags { get; }

		/// <summary>
		/// Gets the tag
		/// </summary>
		public T Tag { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="span">Span</param>
		/// <param name="flags">Flags</param>
		/// <param name="tag">Tag</param>
		public HexTagSpan(HexBufferSpan span, HexSpanSelectionFlags flags, T tag) {
			if (span.IsDefault)
				throw new ArgumentException();
			Span = span;
			Flags = flags;
			Tag = tag ?? throw new ArgumentNullException(nameof(tag));
		}
	}
}

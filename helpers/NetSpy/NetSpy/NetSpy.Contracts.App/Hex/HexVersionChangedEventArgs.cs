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

namespace NetSpy.Contracts.Hex {
	/// <summary>
	/// Hex version changed event args
	/// </summary>
	public abstract class HexVersionChangedEventArgs : EventArgs {
		/// <summary>
		/// Version before the change
		/// </summary>
		public HexVersion BeforeVersion { get; }

		/// <summary>
		/// Version after the change
		/// </summary>
		public HexVersion AfterVersion { get; }

		/// <summary>
		/// Edit tag passed to <see cref="HexBuffer.CreateEdit(int?, object)"/>
		/// </summary>
		public object? EditTag { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="beforeVersion">Version before the change</param>
		/// <param name="afterVersion">Version after the change</param>
		/// <param name="editTag">Edit tag</param>
		protected HexVersionChangedEventArgs(HexVersion beforeVersion, HexVersion afterVersion, object? editTag) {
			BeforeVersion = beforeVersion ?? throw new ArgumentNullException(nameof(beforeVersion));
			AfterVersion = afterVersion ?? throw new ArgumentNullException(nameof(afterVersion));
			EditTag = editTag;
		}
	}
}

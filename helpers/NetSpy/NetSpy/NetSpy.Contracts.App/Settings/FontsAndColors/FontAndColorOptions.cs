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

using NetSpy.Contracts.Settings.AppearanceCategory;

namespace NetSpy.Contracts.Settings.FontsAndColors {
	/// <summary>
	/// Font and color options
	/// </summary>
	public abstract class FontAndColorOptions {
		/// <summary>
		/// Constructor
		/// </summary>
		protected FontAndColorOptions() { }

		/// <summary>
		/// Name shown in the UI
		/// </summary>
		public abstract string? DisplayName { get; }

		/// <summary>
		/// Unique name, eg. <see cref="AppearanceCategoryConstants.TextEditor"/>
		/// </summary>
		public abstract string Name { get; }

		/// <summary>
		/// Font option
		/// </summary>
		public abstract FontOption FontOption { get; }

		/// <summary>
		/// Saves all settings
		/// </summary>
		public abstract void OnApply();

		/// <summary>
		/// Called after the dialog box is closed
		/// </summary>
		public virtual void OnClosed() { }
	}
}

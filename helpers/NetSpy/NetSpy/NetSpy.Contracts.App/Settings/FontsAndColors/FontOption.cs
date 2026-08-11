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
using System.Windows.Media;
using NetSpy.Contracts.Settings.Fonts;

namespace NetSpy.Contracts.Settings.FontsAndColors {
	/// <summary>
	/// Font option
	/// </summary>
	public class FontOption {
		/// <summary>
		/// Font type
		/// </summary>
		public FontType FontType { get; }

		/// <summary>
		/// Gets/sets the font family
		/// </summary>
		public FontFamily FontFamily { get; set; }

		/// <summary>
		/// Gets/sets the font size
		/// </summary>
		public double FontSize { get; set; }

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="fontType">Font type</param>
		/// <param name="fontFamily">Font family</param>
		public FontOption(FontType fontType, FontFamily fontFamily) {
			FontType = fontType;
			FontFamily = fontFamily ?? throw new ArgumentOutOfRangeException(nameof(fontFamily));
		}
	}
}

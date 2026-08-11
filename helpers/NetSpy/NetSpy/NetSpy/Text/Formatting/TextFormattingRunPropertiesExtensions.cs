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

using System.Windows.Markup;
using Microsoft.VisualStudio.Text.Formatting;

namespace NetSpy.Text.Formatting {
	static class TextFormattingRunPropertiesExtensions {
		static readonly XmlLanguage language = XmlLanguage.GetLanguage("en-US");

		public static string GetFontName(this TextFormattingRunProperties props) {
			if (props.TypefaceEmpty)
				return string.Empty;
			if (!props.Typeface.FontFamily.FamilyNames.TryGetValue(language, out string? name))
				name = null;
			return name ?? string.Empty;
		}
	}
}

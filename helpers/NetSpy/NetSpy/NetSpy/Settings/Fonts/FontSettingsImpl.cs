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
using NetSpy.Contracts.Controls;
using NetSpy.Contracts.Settings.Fonts;

namespace NetSpy.Settings.Fonts {
	sealed class FontSettingsImpl : FontSettings {
		public override ThemeFontSettings ThemeFontSettings { get; }
		public override Guid ThemeGuid { get; }

		public override FontFamily FontFamily {
			get => fontFamily;
			set {
				if (value is null)
					throw new ArgumentNullException(nameof(value));
				if (fontFamily.Source != value.Source) {
					fontFamily = value;
					OnPropertyChanged(nameof(FontFamily));
				}
			}
		}
		FontFamily fontFamily;

		public override double FontSize {
			get => fontSize;
			set {
				var newValue = FontUtilities.FilterFontSize(value);
				if (fontSize != newValue) {
					fontSize = newValue;
					OnPropertyChanged(nameof(FontSize));
				}
			}
		}
		double fontSize;

		public FontSettingsImpl(ThemeFontSettings owner, Guid themeGuid, FontFamily fontFamily, double fontSize) {
			ThemeFontSettings = owner ?? throw new ArgumentNullException(nameof(owner));
			ThemeGuid = themeGuid;
			this.fontFamily = fontFamily ?? throw new ArgumentNullException(nameof(fontFamily));
			FontSize = fontSize;
		}
	}
}

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
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using NetSpy.Contracts.Settings.AppearanceCategory;
using NetSpy.Contracts.Settings.Fonts;
using NetSpy.Contracts.Text.Classification;
using NetSpy.Contracts.Themes;
using NetSpy.Text.Classification;
using Microsoft.VisualStudio.Text.Classification;

namespace NetSpy.Settings.AppearanceCategory {
	interface ITextAppearanceCategory {
		event EventHandler? SettingsChanged;
		ResourceDictionary CreateResourceDictionary(ITheme theme);
	}

	sealed class TextAppearanceCategory : ITextAppearanceCategory {
		public string? DisplayName => def.DisplayName;
		public string Category => def.Category;
		public bool IsUserVisible => def.IsUserVisible;
		public ThemeFontSettings ThemeFontSettings { get; }

		readonly TextAppearanceCategoryDefinition def;
		ResourceDictionary? resourceDictionary;
		FontSettings? activeFontSettings;

		public TextAppearanceCategory(TextAppearanceCategoryDefinition def, ThemeFontSettings themeFontSettings) {
			this.def = def ?? throw new ArgumentNullException(nameof(def));
			ThemeFontSettings = themeFontSettings ?? throw new ArgumentNullException(nameof(themeFontSettings));
			themeFontSettings.PropertyChanged += ThemeFontSettings_PropertyChanged;
			UpdateActive();
		}

		void ThemeFontSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == nameof(ThemeFontSettings.Active))
				UpdateActive();
		}

		void UpdateActive() {
			var newActive = ThemeFontSettings.Active;
			if (activeFontSettings == newActive)
				return;
			if (activeFontSettings is not null)
				activeFontSettings.PropertyChanged -= ActiveFontSettings_PropertyChanged;
			activeFontSettings = newActive;
			activeFontSettings.PropertyChanged += ActiveFontSettings_PropertyChanged;
		}

		void ActiveFontSettings_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			if (e.PropertyName == nameof(activeFontSettings.FontFamily) || e.PropertyName == nameof(activeFontSettings.FontSize))
				RaiseSettingsChanged();
		}

		public void ClearCache() => resourceDictionary = null;
		public void OnThemeChanged() => RaiseSettingsChanged();
		public event EventHandler? SettingsChanged;

		void RaiseSettingsChanged() {
			ClearCache();
			SettingsChanged?.Invoke(this, EventArgs.Empty);
		}

		public ResourceDictionary CreateResourceDictionary(ITheme theme) {
			Debug.Assert(theme.Guid == activeFontSettings?.ThemeGuid);
			if (resourceDictionary is null)
				resourceDictionary = CreateResourceDictionaryCore(theme);
			return resourceDictionary;
		}

		ResourceDictionary CreateResourceDictionaryCore(ITheme theme) {
			Debug2.Assert(activeFontSettings is not null);
			var res = new ResourceDictionary();

			var tc = theme.GetColor(def.ColorType);
			var foreground = tc.Foreground;
			var background = tc.Background;
			var typeface = new Typeface(activeFontSettings.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal, ClassificationFontUtils.DefaultFallbackFontFamily);
			// Round to an integer so the IFormattedLine property sizes (Height etc) are integers
			var renderingSize = Math.Round(activeFontSettings.FontSize);

			res[ClassificationFormatDefinition.TypefaceId] = typeface;
			res[EditorFormatDefinition.ForegroundBrushId] = foreground;
			res[EditorFormatDefinition.BackgroundBrushId] = background;
			res[ClassificationFormatDefinition.FontRenderingSizeId] = renderingSize;
			res[EditorFormatMapConstants.TextViewBackgroundId] = background;

			return res;
		}
	}
}

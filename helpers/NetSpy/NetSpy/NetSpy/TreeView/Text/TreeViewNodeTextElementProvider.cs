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

using System.ComponentModel.Composition;
using System.Windows;
using NetSpy.Contracts.Settings.AppearanceCategory;
using NetSpy.Contracts.Text.Classification;
using NetSpy.Contracts.TreeView.Text;
using Microsoft.VisualStudio.Text.Classification;

namespace NetSpy.TreeView.Text {
	[Export(typeof(ITreeViewNodeTextElementProvider))]
	sealed class TreeViewNodeTextElementProvider : ITreeViewNodeTextElementProvider {
		readonly ITextElementProvider textElementProvider;
		readonly IClassificationFormatMap classificationFormatMap;

		[ImportingConstructor]
		TreeViewNodeTextElementProvider(ITextElementProvider textElementProvider, IClassificationFormatMapService classificationFormatMapService) {
			this.textElementProvider = textElementProvider;
			classificationFormatMap = classificationFormatMapService.GetClassificationFormatMap(AppearanceCategoryConstants.UIMisc);
		}

		public FrameworkElement CreateTextElement(TreeViewNodeClassifierContext context, string contentType, TextElementFlags flags) =>
			textElementProvider.CreateTextElement(classificationFormatMap, context, contentType, flags);
	}
}

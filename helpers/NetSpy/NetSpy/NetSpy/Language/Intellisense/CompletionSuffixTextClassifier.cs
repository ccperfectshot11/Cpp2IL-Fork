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
using System.ComponentModel.Composition;
using NetSpy.Contracts.Language.Intellisense.Classification;
using NetSpy.Contracts.Text;
using NetSpy.Contracts.Text.Classification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Language.Intellisense {
	[Export(typeof(ITextClassifierProvider))]
	[ContentType(ContentTypes.CompletionSuffix)]
	sealed class CompletionSuffixTextClassifierProvider : ITextClassifierProvider {
		readonly IThemeClassificationTypeService themeClassificationTypeService;

		[ImportingConstructor]
		CompletionSuffixTextClassifierProvider(IThemeClassificationTypeService themeClassificationTypeService) => this.themeClassificationTypeService = themeClassificationTypeService;

		public ITextClassifier? Create(IContentType contentType) => new CompletionSuffixTextClassifier(themeClassificationTypeService);
	}

	sealed class CompletionSuffixTextClassifier : ITextClassifier {
		readonly IClassificationType completionSuffixClassificationType;

		public CompletionSuffixTextClassifier(IThemeClassificationTypeService themeClassificationTypeService) {
			if (themeClassificationTypeService is null)
				throw new ArgumentNullException(nameof(themeClassificationTypeService));
			completionSuffixClassificationType = themeClassificationTypeService.GetClassificationType(TextColor.CompletionSuffix);
		}

		public IEnumerable<TextClassificationTag> GetTags(TextClassifierContext context) {
			if (!context.Colorize)
				yield break;
			if (!(context is CompletionSuffixClassifierContext))
				yield break;
			yield return new TextClassificationTag(new Span(0, context.Text.Length), completionSuffixClassificationType);
		}
	}
}

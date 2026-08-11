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
using NetSpy.Contracts.Text.Classification;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Roslyn.Text.Classification {
	[Export(typeof(ITaggedTextElementProviderService))]
	sealed class TaggedTextElementProviderService : ITaggedTextElementProviderService {
		readonly IClassificationFormatMapService classificationFormatMapService;
		readonly ITextElementProvider textElementProvider;

		[ImportingConstructor]
		TaggedTextElementProviderService(IClassificationFormatMapService classificationFormatMapService, ITextElementProvider textElementProvider) {
			this.classificationFormatMapService = classificationFormatMapService;
			this.textElementProvider = textElementProvider;
		}

		public ITaggedTextElementProvider Create(IContentType contentType, string category) {
			if (contentType is null)
				throw new ArgumentNullException(nameof(contentType));
			if (category is null)
				throw new ArgumentNullException(nameof(category));
			return new TaggedTextElementProvider(contentType, classificationFormatMapService.GetClassificationFormatMap(category), textElementProvider);
		}
	}
}

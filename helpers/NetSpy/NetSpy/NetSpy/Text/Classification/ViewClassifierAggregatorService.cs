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
using NetSpy.Contracts.Text.Tagging;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;

namespace NetSpy.Text.Classification {
	[Export(typeof(ISynchronousViewClassifierAggregatorService))]
	[Export(typeof(IViewClassifierAggregatorService))]
	sealed class ViewClassifierAggregatorService : ISynchronousViewClassifierAggregatorService {
		readonly ISynchronousViewTagAggregatorFactoryService synchronousViewTagAggregatorFactoryService;
		readonly IClassificationTypeRegistryService classificationTypeRegistryService;

		[ImportingConstructor]
		ViewClassifierAggregatorService(ISynchronousViewTagAggregatorFactoryService synchronousViewTagAggregatorFactoryService, IClassificationTypeRegistryService classificationTypeRegistryService) {
			this.synchronousViewTagAggregatorFactoryService = synchronousViewTagAggregatorFactoryService;
			this.classificationTypeRegistryService = classificationTypeRegistryService;
		}

		public IClassifier GetClassifier(ITextView textView) {
			if (textView is null)
				throw new ArgumentNullException(nameof(textView));
			return new ViewClassifierAggregator(synchronousViewTagAggregatorFactoryService, classificationTypeRegistryService, textView);
		}

		public ISynchronousClassifier GetSynchronousClassifier(ITextView textView) {
			if (textView is null)
				throw new ArgumentNullException(nameof(textView));
			return new ViewClassifierAggregator(synchronousViewTagAggregatorFactoryService, classificationTypeRegistryService, textView);
		}
	}
}

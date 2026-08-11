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
using NetSpy.Contracts.Text.Tagging;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace NetSpy.Text.Tagging {
	[Export(typeof(ISynchronousViewTagAggregatorFactoryService))]
	[Export(typeof(IViewTagAggregatorFactoryService))]
	sealed class ViewTagAggregatorFactoryService : ISynchronousViewTagAggregatorFactoryService {
		readonly ITaggerFactory taggerFactory;

		[ImportingConstructor]
		ViewTagAggregatorFactoryService(ITaggerFactory taggerFactory) => this.taggerFactory = taggerFactory;

		public ITagAggregator<T> CreateTagAggregator<T>(ITextView textView) where T : ITag => CreateTagAggregator<T>(textView, TagAggregatorOptions.None);
		public ITagAggregator<T> CreateTagAggregator<T>(ITextView textView, TagAggregatorOptions options) where T : ITag {
			if (textView is null)
				throw new ArgumentNullException(nameof(textView));
			return new TextViewTagAggregator<T>(taggerFactory, textView, options);
		}

		public ISynchronousTagAggregator<T> CreateSynchronousTagAggregator<T>(ITextView textView) where T : ITag => CreateSynchronousTagAggregator<T>(textView, TagAggregatorOptions.None);
		public ISynchronousTagAggregator<T> CreateSynchronousTagAggregator<T>(ITextView textView, TagAggregatorOptions options) where T : ITag {
			if (textView is null)
				throw new ArgumentNullException(nameof(textView));
			return new TextViewTagAggregator<T>(taggerFactory, textView, options);
		}
	}
}

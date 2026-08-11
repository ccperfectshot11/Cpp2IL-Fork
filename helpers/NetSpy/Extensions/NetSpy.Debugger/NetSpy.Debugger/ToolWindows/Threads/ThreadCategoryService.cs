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
using System.Linq;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Images;

namespace NetSpy.Debugger.ToolWindows.Threads {
	readonly struct CategoryInfo {
		public ImageReference Image { get; }
		public string Category { get; }
		public CategoryInfo(ImageReference image, string category) {
			Image = image;
			Category = category ?? throw new ArgumentNullException(nameof(category));
		}
	}

	abstract class ThreadCategoryService {
		public abstract CategoryInfo GetInfo(string kind);
	}

	[Export(typeof(ThreadCategoryService))]
	sealed class ThreadCategoryServiceImpl : ThreadCategoryService {
		readonly Lazy<ThreadCategoryProvider, IThreadCategoryProviderMetadata>[] threadCategoryProviders;

		[ImportingConstructor]
		ThreadCategoryServiceImpl([ImportMany] IEnumerable<Lazy<ThreadCategoryProvider, IThreadCategoryProviderMetadata>> threadCategoryProviders) =>
			this.threadCategoryProviders = threadCategoryProviders.OrderBy(a => a.Metadata.Order).ToArray();

		public override CategoryInfo GetInfo(string kind) {
			foreach (var lz in threadCategoryProviders) {
				var info = lz.Value.GetCategory(kind);
				if (info is not null) {
					var imgRef = info.Value.Image as ImageReference? ?? ImageReference.None;
					return new CategoryInfo(imgRef, info.Value.Category);
				}
			}
			return new CategoryInfo(DsImages.QuestionMark, "???");
		}
	}
}

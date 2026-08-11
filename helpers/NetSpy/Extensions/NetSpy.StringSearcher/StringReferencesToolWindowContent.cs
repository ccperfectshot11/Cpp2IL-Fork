/*
    Copyright (C) 2025 Washi

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
using System.Windows;
using NetSpy.Contracts.ToolWindows;
using NetSpy.Contracts.ToolWindows.App;

namespace NetSpy.StringSearcher {
	[Export(typeof(IToolWindowContentProvider))]
	sealed class StringReferencesToolWindowContentProvider : IToolWindowContentProvider {
		private readonly Lazy<IStringReferencesService> service;
		private StringReferencesToolWindowContent? toolWindowContent;

		public StringReferencesToolWindowContent DocumentTreeViewWindowContent => toolWindowContent ??= new(service);

		[ImportingConstructor]
		StringReferencesToolWindowContentProvider(Lazy<IStringReferencesService> service) {
			this.service = service;
		}

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get {
				yield return new ToolWindowContentInfo(
					StringReferencesToolWindowContent.THE_GUID,
					StringReferencesToolWindowContent.DEFAULT_LOCATION,
					AppToolWindowConstants.DEFAULT_CONTENT_ORDER_BOTTOM_ANALYZER
				);
			}
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == StringReferencesToolWindowContent.THE_GUID ? DocumentTreeViewWindowContent : null;
	}

	sealed class StringReferencesToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new("EF36BC9C-4F48-45AC-8A0B-BC2C11A3194E");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override object? UIObject => service.Value.UIObject;

		public override IInputElement? FocusedElement => service.Value.FocusedElement;

		public override FrameworkElement? ZoomElement => service.Value.ZoomElement;

		public override Guid Guid => THE_GUID;

		public override string Title => Properties.NetSpy_StringSearcher_Resources.StringReferencesWindowTitle;

		readonly Lazy<IStringReferencesService> service;

		public StringReferencesToolWindowContent(Lazy<IStringReferencesService> service) {
			this.service = service;
		}
	}
}

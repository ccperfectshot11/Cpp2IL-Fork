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
using NetSpy.Contracts.App;
using NetSpy.Contracts.Hex.Editor;
using NetSpy.Contracts.Hex.Operations;

namespace NetSpy.Hex.Editor.Search {
	abstract class HexViewSearchServiceProvider {
		public abstract HexViewSearchService Get(WpfHexView wpfHexView);
	}

	[Export(typeof(HexViewSearchServiceProvider))]
	sealed class HexViewSearchServiceProviderImpl : HexViewSearchServiceProvider {
		readonly HexSearchServiceFactory hexSearchServiceFactory;
		readonly SearchSettings searchSettings;
		readonly IMessageBoxService messageBoxService;
		readonly HexEditorOperationsFactoryService editorOperationsFactoryService;

		[ImportingConstructor]
		HexViewSearchServiceProviderImpl(HexSearchServiceFactory hexSearchServiceFactory, SearchSettings searchSettings, IMessageBoxService messageBoxService, HexEditorOperationsFactoryService editorOperationsFactoryService) {
			this.hexSearchServiceFactory = hexSearchServiceFactory;
			this.searchSettings = searchSettings;
			this.messageBoxService = messageBoxService;
			this.editorOperationsFactoryService = editorOperationsFactoryService;
		}

		public override HexViewSearchService Get(WpfHexView wpfHexView) {
			if (wpfHexView is null)
				throw new ArgumentNullException(nameof(wpfHexView));
			return wpfHexView.Properties.GetOrCreateSingletonProperty(typeof(HexViewSearchService),
				() => new HexViewSearchServiceImpl(wpfHexView, hexSearchServiceFactory, searchSettings, messageBoxService, editorOperationsFactoryService));
		}
	}
}

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
using NetSpy.Contracts.Controls.ToolWindows;
using NetSpy.Contracts.Debugger.Evaluation;
using NetSpy.Debugger.UI;

namespace NetSpy.Debugger.Evaluation.ViewModel.Impl {
	abstract class LanguageEditValueProviderFactory {
		public abstract LanguageEditValueProvider Create(string defaultContentType);
	}

	[Export(typeof(LanguageEditValueProviderFactory))]
	sealed class LanguageEditValueProviderFactoryImpl : LanguageEditValueProviderFactory {
		readonly UIDispatcher uiDispatcher;
		readonly EditValueProviderService editValueProviderService;

		[ImportingConstructor]
		LanguageEditValueProviderFactoryImpl(UIDispatcher uiDispatcher, EditValueProviderService editValueProviderService) {
			this.uiDispatcher = uiDispatcher;
			this.editValueProviderService = editValueProviderService;
		}

		public override LanguageEditValueProvider Create(string defaultContentType) {
			if (defaultContentType is null)
				throw new ArgumentNullException(nameof(defaultContentType));
			return new LanguageEditValueProviderImpl(uiDispatcher, editValueProviderService, defaultContentType);
		}
	}

	abstract class LanguageEditValueProvider : IEditValueProvider {
		public abstract DbgLanguage? Language { get; set; }
		public abstract IEditValue Create(string text, EditValueFlags flags);
	}

	sealed class LanguageEditValueProviderImpl : LanguageEditValueProvider {
		public override DbgLanguage? Language {
			get => language;
			set {
				if (language == value)
					return;
				language = value;
				editValueProvider = null;
			}
		}

		readonly UIDispatcher uiDispatcher;
		readonly EditValueProviderService editValueProviderService;
		readonly string defaultContentType;
		IEditValueProvider? editValueProvider;
		DbgLanguage? language;

		public LanguageEditValueProviderImpl(UIDispatcher uiDispatcher, EditValueProviderService editValueProviderService, string defaultContentType) {
			this.uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
			this.editValueProviderService = editValueProviderService ?? throw new ArgumentNullException(nameof(editValueProviderService));
			this.defaultContentType = defaultContentType ?? throw new ArgumentNullException(nameof(defaultContentType));
		}

		string GetContentType() {
			if (language is not null) {
				//TODO:
			}
			return defaultContentType;
		}

		public override IEditValue Create(string text, EditValueFlags flags) {
			uiDispatcher.VerifyAccess();
			if (editValueProvider is null)
				editValueProvider = editValueProviderService.Create(GetContentType(), Array.Empty<string>());
			return editValueProvider.Create(text, flags);
		}
	}
}

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
using NetSpy.Contracts.Settings.Groups;
using NetSpy.Contracts.Text;
using Microsoft.VisualStudio.Utilities;

namespace NetSpy.Output.Settings {
	interface IOutputWindowOptionsService {
		IOutputWindowOptions Default { get; }
		event EventHandler<OptionChangedEventArgs>? OptionChanged;
	}

	[Export(typeof(IOutputWindowOptionsService))]
	sealed class OutputWindowOptionsService : IOutputWindowOptionsService {
		public IOutputWindowOptions Default { get; }
		public event EventHandler<OptionChangedEventArgs>? OptionChanged;

		[ImportingConstructor]
		OutputWindowOptionsService(ITextViewOptionsGroupService textViewOptionsGroupService, IContentTypeRegistryService contentTypeRegistryService) {
			var group = textViewOptionsGroupService.GetGroup(PredefinedTextViewGroupNames.OutputWindow);
			group.TextViewOptionChanged += TextViewOptionsGroup_TextViewOptionChanged;
			Default = new OutputWindowOptions(group, contentTypeRegistryService.GetContentType(ContentTypes.Any));
		}

		void TextViewOptionsGroup_TextViewOptionChanged(object? sender, TextViewOptionChangedEventArgs e) =>
			OptionChanged?.Invoke(this, new OptionChangedEventArgs(e.ContentType, e.OptionId));
	}
}

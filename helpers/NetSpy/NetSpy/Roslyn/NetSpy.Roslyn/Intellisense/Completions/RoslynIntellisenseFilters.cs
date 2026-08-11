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
using NetSpy.Contracts.Images;
using NetSpy.Contracts.Language.Intellisense;
using NetSpy.Roslyn.Properties;
using Microsoft.CodeAnalysis.Tags;

namespace NetSpy.Roslyn.Intellisense.Completions {
	static class RoslynIntellisenseFilters {
		public static RoslynIntellisenseFilter[] CreateFilters() => new RoslynIntellisenseFilter[] {
			new RoslynIntellisenseFilter(DsImages.LocalVariable, NetSpy_Roslyn_Resources.LocalsAndParametersToolTip, "L", WellKnownTags.Local, WellKnownTags.Parameter),
			new RoslynIntellisenseFilter(DsImages.ConstantPublic, NetSpy_Roslyn_Resources.ConstantsToolTip, "O", WellKnownTags.Constant),
			new RoslynIntellisenseFilter(DsImages.Property, NetSpy_Roslyn_Resources.PropertiesToolTip, "P", WellKnownTags.Property),
			new RoslynIntellisenseFilter(DsImages.EventPublic, NetSpy_Roslyn_Resources.EventsToolTip, "V", WellKnownTags.Event),
			new RoslynIntellisenseFilter(DsImages.FieldPublic, NetSpy_Roslyn_Resources.FieldsToolTip, "F", WellKnownTags.Field),
			new RoslynIntellisenseFilter(DsImages.MethodPublic, NetSpy_Roslyn_Resources.MethodsToolTip, "M", WellKnownTags.Method),
			new RoslynIntellisenseFilter(DsImages.ExtensionMethod, NetSpy_Roslyn_Resources.ExtensionMethodsToolTip, "X", WellKnownTags.ExtensionMethod),
			new RoslynIntellisenseFilter(DsImages.InterfacePublic, NetSpy_Roslyn_Resources.InterfacesToolTip, "I", WellKnownTags.Interface),
			new RoslynIntellisenseFilter(DsImages.ClassPublic, NetSpy_Roslyn_Resources.ClassesToolTip, "C", WellKnownTags.Class),
			new RoslynIntellisenseFilter(DsImages.ModulePublic, NetSpy_Roslyn_Resources.ModulesToolTip, "U", WellKnownTags.Module),
			new RoslynIntellisenseFilter(DsImages.StructurePublic, NetSpy_Roslyn_Resources.StructuresToolTip, "S", WellKnownTags.Structure),
			new RoslynIntellisenseFilter(DsImages.EnumerationPublic, NetSpy_Roslyn_Resources.EnumsToolTip, "E", WellKnownTags.Enum),
			new RoslynIntellisenseFilter(DsImages.DelegatePublic, NetSpy_Roslyn_Resources.DelegatesToolTip, "D", WellKnownTags.Delegate),
			new RoslynIntellisenseFilter(DsImages.Namespace, NetSpy_Roslyn_Resources.NamespacesToolTip, "N", WellKnownTags.Namespace),
			new RoslynIntellisenseFilter(DsImages.IntellisenseKeyword, NetSpy_Roslyn_Resources.KeywordsToolTip, "K", WellKnownTags.Keyword),
			new RoslynIntellisenseFilter(DsImages.Snippet, NetSpy_Roslyn_Resources.SnippetsToolTip, "T", WellKnownTags.Snippet),
		};
	}

	sealed class RoslynIntellisenseFilter : DsIntellisenseFilter {
		public string[] Tags { get; }

		public RoslynIntellisenseFilter(ImageReference imageReference, string toolTip, string accessKey, params string[] tags)
			: base(imageReference, toolTip, accessKey, false, true) {
			if (tags is null)
				throw new ArgumentNullException(nameof(tags));
			if (tags.Length == 0)
				throw new ArgumentOutOfRangeException(nameof(tags));
			Tags = tags;
		}
	}
}

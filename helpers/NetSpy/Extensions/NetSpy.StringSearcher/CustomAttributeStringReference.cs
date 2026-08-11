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

using dnlib.DotNet;
using NetSpy.Contracts.Documents.TreeView;
using NetSpy.Contracts.Text;
using NetSpy.Contracts.Text.Classification;
using NetSpy.StringSearcher.Properties;

namespace NetSpy.StringSearcher {
	public sealed class CustomAttributeStringReference(
		StringReferenceContext context,
		string literal,
		IHasCustomAttribute referrer,
		IMDTokenProvider container,
		ModuleDef module,
		CustomAttribute attribute,
		string argumentName)
		: StringReference(context, literal, referrer) {

		public CustomAttribute CustomAttribute { get; } = attribute;

		public override StringReferenceKind Kind => StringReferenceKind.Attribute;

		public override ModuleDef Module => module;

		public override MDToken Token => ((IMDTokenProvider)ContainerObject).MDToken;

		public override object ContainerObject => container;

		protected override void WriteReferrerUI(TextClassifierTextColorWriter writer) {
			switch (ContainerObject) {
			case ModuleDef module:
				writer.WriteModule(module.Name);
				break;
			case AssemblyDef assembly:
				new NodeFormatter().Write(writer, Context.Decompiler, assembly, false, true, true);
				break;
			case IMemberRef memberRef:
				Context.Decompiler.Write(writer, memberRef, DefaultFormatterOptions);
				break;
			default:
				writer.Write(ContainerObject.ToString()!);
				break;
			}

			switch (Referrer) {
			case ParamDef param:
				WriteParameterReference(writer, param);
				break;
			case GenericParam param:
				writer.Write(" ");
				writer.Write(TextColor.DarkGray, NetSpy_StringSearcher_Resources.ReferrerGenericParameter);
				writer.Write(" ");
				Context.Decompiler.Write(writer, param, DefaultFormatterOptions);
				break;
			}

			writer.Write(" ");
			writer.Write(TextColor.DarkGray, NetSpy_StringSearcher_Resources.ReferrerAttribute);
			writer.Write(" ");
			Context.Decompiler.Write(writer, CustomAttribute.AttributeType, DefaultFormatterOptions);
			writer.Write(TextColor.Punctuation, " (");
			writer.Write(TextColor.InstanceProperty, argumentName);
			writer.Write(TextColor.Punctuation, ")");
		}
	}
}

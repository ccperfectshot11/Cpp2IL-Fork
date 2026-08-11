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
using System.Windows.Input;
using dnlib.DotNet;
using NetSpy.AsmEditor.Properties;
using NetSpy.AsmEditor.ViewHelpers;
using NetSpy.Contracts.Documents.TreeView.Resources;
using NetSpy.Contracts.MVVM;
using NetSpy.Contracts.Search;

namespace NetSpy.AsmEditor.Resources {
	sealed class UserTypeVM : ViewModelBase {
		public IDnlibTypePicker DnlibTypePicker {
			set => dnlibTypePicker = value;
		}
		IDnlibTypePicker? dnlibTypePicker;

		public ICommand PickTypeCommand => new RelayCommand(a => PickType());

		public string TypeFullName {
			get => typeFullName;
			set {
				if (typeFullName != value) {
					typeFullName = value;
					OnPropertyChanged(nameof(TypeFullName));
					OnPropertyChanged(nameof(StringValue));
					HasErrorUpdated();
				}
			}
		}
		string typeFullName = string.Empty;

		public string StringValue {
			get => stringValue;
			set {
				if (stringValue != value) {
					stringValue = value;
					OnPropertyChanged(nameof(StringValue));
					HasErrorUpdated();
				}
			}
		}
		string stringValue = string.Empty;

		readonly ModuleDef ownerModule;

		public UserTypeVM(ModuleDef ownerModule) {
			this.ownerModule = ownerModule;
		}

		void PickType() {
			if (dnlibTypePicker is null)
				throw new InvalidOperationException();
			var newType = dnlibTypePicker.GetDnlibType(NetSpy_AsmEditor_Resources.Pick_Type, new FlagsDocumentTreeNodeFilter(VisibleMembersFlags.TypeDef), GetTypeRef(), ownerModule);
			if (newType is not null)
				TypeFullName = newType.AssemblyQualifiedName;
		}

		public void SetData(byte[] data) => StringValue = GetString(data);

		public byte[]? GetSerializedData() {
			return null;
		}

		string GetString(byte[] data) {
			return NetSpy_AsmEditor_Resources.Error_DeSerializationDisabledInSettings;
		}

		string GetSerializedData(out object? obj) {
			obj = null;
			return NetSpy_AsmEditor_Resources.Error_DeSerializationDisabledInSettings;
		}

		ITypeDefOrRef GetTypeRef() => TypeNameParser.ParseReflection(ownerModule, typeFullName, null);

		protected override string? Verify(string columnName) {
			if (columnName == nameof(TypeFullName)) {
				return NetSpy_AsmEditor_Resources.Error_DeSerializationDisabledInSettings;
			}

			if (columnName == nameof(StringValue)) {
				return GetSerializedData(out _);
			}

			return string.Empty;
		}

		public override bool HasError =>
			!string.IsNullOrEmpty(Verify(nameof(TypeFullName))) ||
			!string.IsNullOrEmpty(Verify(nameof(StringValue)));
	}
}

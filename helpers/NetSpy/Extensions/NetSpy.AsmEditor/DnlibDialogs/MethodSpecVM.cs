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
using System.Linq;
using System.Windows.Input;
using dnlib.DotNet;
using NetSpy.AsmEditor.Properties;
using NetSpy.AsmEditor.ViewHelpers;
using NetSpy.Contracts.MVVM;
using NetSpy.Contracts.Search;

namespace NetSpy.AsmEditor.DnlibDialogs {
	sealed class MethodSpecVM : ViewModelBase {
		readonly MethodSpecOptions origOptions;

		public IDnlibTypePicker DnlibTypePicker {
			set => dnlibTypePicker = value;
		}
		IDnlibTypePicker? dnlibTypePicker;

		public ICommand ReinitializeCommand => new RelayCommand(a => Reinitialize());
		public ICommand PickMethodCommand => new RelayCommand(a => PickMethod());

		public IMethodDefOrRef? Method {
			get => method;
			set {
				if (method != value) {
					method = value;
					OnPropertyChanged(nameof(Method));
					OnPropertyChanged(nameof(MethodFullName));
					HasErrorUpdated();
				}
			}
		}
		IMethodDefOrRef? method;

		public string MethodFullName {
			get {
				if (Method is null)
					return "null";
				return Method.FullName;
			}
		}

		public CreateTypeSigArrayVM CreateTypeSigArrayVM { get; }
		public CustomAttributesVM CustomAttributesVM { get; }

		readonly TypeSigCreatorOptions typeSigCreatorOptions;

		public MethodSpecVM(MethodSpecOptions options, TypeSigCreatorOptions typeSigCreatorOptions) {
			origOptions = options;
			this.typeSigCreatorOptions = typeSigCreatorOptions;
			CustomAttributesVM = new CustomAttributesVM(typeSigCreatorOptions.OwnerModule, typeSigCreatorOptions.DecompilerService);

			this.typeSigCreatorOptions.CanAddGenericMethodVar = true;
			this.typeSigCreatorOptions.CanAddGenericTypeVar = true;
			this.typeSigCreatorOptions.IsLocal = false;
			this.typeSigCreatorOptions.NullTypeSigAllowed = false;

			CreateTypeSigArrayVM = new CreateTypeSigArrayVM(typeSigCreatorOptions, null);

			Reinitialize();
		}

		void PickMethod() {
			if (dnlibTypePicker is null)
				throw new InvalidOperationException();
			var newMethod = dnlibTypePicker.GetDnlibType(NetSpy_AsmEditor_Resources.Pick_Method, new FlagsDocumentTreeNodeFilter(VisibleMembersFlags.MethodDef), Method, typeSigCreatorOptions.OwnerModule);
			if (newMethod is not null)
				Method = newMethod;
		}

		void Reinitialize() => InitializeFrom(origOptions);
		public MethodSpecOptions CreateMethodSpecOptions() => CopyTo(new MethodSpecOptions());

		void InitializeFrom(MethodSpecOptions options) {
			Method = options.Method;
			var gim = options.Instantiation as GenericInstMethodSig;
			CreateTypeSigArrayVM.TypeSigCollection.Clear();
			if (gim is not null)
				CreateTypeSigArrayVM.TypeSigCollection.AddRange(gim.GenericArguments);
			CustomAttributesVM.InitializeFrom(options.CustomAttributes);
		}

		MethodSpecOptions CopyTo(MethodSpecOptions options) {
			options.Method = Method;
			options.Instantiation = new GenericInstMethodSig(CreateTypeSigArrayVM.TypeSigCollection);
			options.CustomAttributes.Clear();
			options.CustomAttributes.AddRange(CustomAttributesVM.Collection.Select(a => a.CreateCustomAttributeOptions().Create()));
			return options;
		}

		public override bool HasError => Method is null;
	}
}

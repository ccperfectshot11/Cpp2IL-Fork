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

using dnlib.DotNet;
using NetSpy.AsmEditor.Properties;
using NetSpy.Contracts.Decompiler;

namespace NetSpy.AsmEditor.DnlibDialogs {
	sealed class SecurityAttributesVM : ListVM<SecurityAttributeVM, SecurityAttribute> {
		public SecurityAttributesVM(ModuleDef ownerModule, IDecompilerService decompilerService, TypeDef? ownerType, MethodDef? ownerMethod)
			: base(NetSpy_AsmEditor_Resources.EditSecurityAttribute, NetSpy_AsmEditor_Resources.CreateSecurityAttribute, ownerModule, decompilerService, ownerType, ownerMethod) {
		}

		protected override SecurityAttributeVM Create(SecurityAttribute model) => new SecurityAttributeVM(model, OwnerModule, decompilerService, ownerType, ownerMethod);
		protected override SecurityAttributeVM Clone(SecurityAttributeVM obj) => new SecurityAttributeVM(obj.CreateSecurityAttribute(), OwnerModule, decompilerService, ownerType, ownerMethod);
		protected override SecurityAttributeVM Create() => new SecurityAttributeVM(new SecurityAttribute(), OwnerModule, decompilerService, ownerType, ownerMethod);
	}
}

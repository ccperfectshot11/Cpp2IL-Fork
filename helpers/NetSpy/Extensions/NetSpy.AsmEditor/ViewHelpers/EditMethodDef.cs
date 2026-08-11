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

using System.Windows;
using dnlib.DotNet;
using NetSpy.AsmEditor.DnlibDialogs;
using NetSpy.AsmEditor.Properties;
using NetSpy.Contracts.Search;

namespace NetSpy.AsmEditor.ViewHelpers {
	sealed class EditMethodDef : IEdit<MethodDefVM> {
		readonly ModuleDef ownerModule;
		readonly DnlibTypePicker dnlibTypePicker;

		public EditMethodDef(ModuleDef ownerModule)
			: this(ownerModule, null) {
		}

		public EditMethodDef(ModuleDef ownerModule, Window? ownerWindow) {
			this.ownerModule = ownerModule;
			dnlibTypePicker = new DnlibTypePicker(ownerWindow);
		}

		public MethodDefVM? Edit(string? title, MethodDefVM vm) {
			var method = dnlibTypePicker.GetDnlibType(NetSpy_AsmEditor_Resources.Pick_Method, new SameModuleDocumentTreeNodeFilter(ownerModule, new FlagsDocumentTreeNodeFilter(VisibleMembersFlags.MethodDef)), vm.Method, ownerModule);
			if (method is null)
				return null;

			vm.Method = method;
			return vm;
		}
	}
}

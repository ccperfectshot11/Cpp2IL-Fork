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

namespace NetSpy.AsmEditor.ViewHelpers {
	sealed class TypeSigCreator : ITypeSigCreator {
		readonly Window? ownerWindow;

		public TypeSigCreator()
			: this(null) {
		}

		public TypeSigCreator(Window? ownerWindow) => this.ownerWindow = ownerWindow;

		public TypeSig? Create(TypeSigCreatorOptions options, TypeSig? typeSig, out bool canceled) {
			var data = new TypeSigCreatorVM(options, typeSig);
			data.TypeSig = typeSig;
			var win = new TypeSigCreatorDlg();
			win.DataContext = data;
			win.Owner = ownerWindow ?? Application.Current.MainWindow;
			if (win.ShowDialog() != true) {
				canceled = true;
				return null;
			}

			canceled = false;
			return data.TypeSig;
		}
	}
}

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

using NetSpy.Contracts.Text;

namespace NetSpy.Documents.Tabs.Dialogs {
	sealed class GACFilePrinter {
		readonly ITextColorWriter output;

		public GACFilePrinter(ITextColorWriter output) => this.output = output;

		public void WriteName(GACFileVM vm) => output.Write(vm.IsExe ? BoxedTextColor.AssemblyExe : BoxedTextColor.Assembly, vm.Name);
		public void WriteVersion(GACFileVM vm) => output.Write(vm.Version);
	}
}

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

using NetSpy.Contracts.App.Properties;

namespace NetSpy.Contracts.MVVM {
	/// <summary>
	/// Pick filename constants
	/// </summary>
	public static class PickFilenameConstants {
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
		public static readonly string ImagesFilter = $"{NetSpy_Contracts_NetSpy_Resources.Files_Images}|*.png;*.gif;*.bmp;*.dib;*.jpg;*.jpeg;*.jpe;*.jif;*.jfif;*.jfi;*.ico;*.cur|{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
		public static readonly string StrongNameKeyFilter = $"{NetSpy_Contracts_NetSpy_Resources.Files_StrongNameKeyFiles} (*.snk)|*.snk|{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
		public static readonly string AnyFilenameFilter = $"{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
		public static readonly string DotNetExecutableFilter = $"{NetSpy_Contracts_NetSpy_Resources.Files_DotNetExecutables} (*.exe)|*.exe|{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
		public static readonly string DotNetAssemblyOrModuleFilter = $"{NetSpy_Contracts_NetSpy_Resources.Files_DotNetExecutables} (*.exe, *.dll, *.netmodule, *.winmd)|*.exe;*.dll;*.netmodule;*.winmd|{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
		public static readonly string NetModuleFilter = $"{NetSpy_Contracts_NetSpy_Resources.Files_DotNetNetModules} (*.netmodule)|*.netmodule|{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
		public static readonly string ExecutableFilter = $"{NetSpy_Contracts_NetSpy_Resources.Files_Executables} (*.exe)|*.exe|{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
		public static readonly string XmlFilenameFilter = $"{NetSpy_Contracts_NetSpy_Resources.Files_XmlFiles} (*.xml)|*.xml|{NetSpy_Contracts_NetSpy_Resources.AllFiles} (*.*)|*.*";
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
	}
}

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
using dndbg.Engine;
using NetSpy.Contracts.Debugger;
using NetSpy.Contracts.Debugger.DotNet.Code;
using NetSpy.Contracts.Debugger.DotNet.CorDebug.Code;
using NetSpy.Contracts.Metadata;
using NetSpy.Debugger.DotNet.CorDebug.Impl;

namespace NetSpy.Debugger.DotNet.CorDebug.Code {
	abstract class DbgDotNetNativeCodeLocationFactory {
		public abstract DbgDotNetNativeCodeLocation Create(DbgModule module, ModuleId moduleId, uint token, uint ilOffset, DbgILOffsetMapping ilOffsetMapping, ulong nativeMethodAddress, ulong nativeMethodOffset, DnDebuggerObjectHolder<CorCode> corCode);
	}

	[Export(typeof(DbgDotNetNativeCodeLocationFactory))]
	sealed class DbgDotNetNativeCodeLocationFactoryImpl : DbgDotNetNativeCodeLocationFactory {
		internal Lazy<DbgManager> DbgManager { get; }

		[ImportingConstructor]
		DbgDotNetNativeCodeLocationFactoryImpl(Lazy<DbgManager> dbgManager) => DbgManager = dbgManager;

		public override DbgDotNetNativeCodeLocation Create(DbgModule module, ModuleId moduleId, uint token, uint ilOffset, DbgILOffsetMapping ilOffsetMapping, ulong nativeMethodAddress, ulong nativeMethodOffset, DnDebuggerObjectHolder<CorCode> corCode) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (corCode is null)
				throw new ArgumentNullException(nameof(corCode));
			return new DbgDotNetNativeCodeLocationImpl(this, module, moduleId, token, ilOffset, ilOffsetMapping, nativeMethodAddress, nativeMethodOffset, corCode);
		}
	}
}
